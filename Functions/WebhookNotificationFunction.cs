using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Infrastructure;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace meli_znube_integration.Functions;

public class WebhookNotificationFunction
{
    private readonly PackProcessor _processor;
    private readonly IMeliApiClient _meliClient;
    private readonly StockLocationQueueService _stockQueueService;
    private readonly IOrderExecutionStore _orderExecutionStore;
    private readonly ILogger<WebhookNotificationFunction> _logger;
    private readonly IConfiguration _configuration;

    public WebhookNotificationFunction(
        PackProcessor processor,
        IMeliApiClient meliClient,
        StockLocationQueueService stockQueueService,
        IOrderExecutionStore orderExecutionStore,
        ILogger<WebhookNotificationFunction> logger,
        IConfiguration configuration)
    {
        _processor = processor;
        _meliClient = meliClient;
        _stockQueueService = stockQueueService;
        _orderExecutionStore = orderExecutionStore;
        _logger = logger;
        _configuration = configuration;
    }

    [Function("WebhookNotificacion")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook/notification")] HttpRequestData req)
    {
        var apiKey = req.Query["api-key"];
        var configuredKey = _configuration["WebhookApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey) || !string.Equals(apiKey, configuredKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("Intento de acceso no autorizado al webhook. API Key inválida o ausente.");
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }
        string? topic = null;
        string? resource = null;

        try
        {
            using (var doc = await JsonDocument.ParseAsync(req.Body))
            {
                if (doc.RootElement.TryGetProperty("topic", out var topicProp) && topicProp.ValueKind == JsonValueKind.String)
                {
                    topic = topicProp.GetString();
                }

                if (doc.RootElement.TryGetProperty("resource", out var resourceProp) && resourceProp.ValueKind == JsonValueKind.String)
                {
                    resource = resourceProp.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(topic))
            {
                _logger.LogWarning("Webhook recibido sin tópico.");
                return req.CreateResponse(HttpStatusCode.OK);
            }

            switch (topic)
            {
                case "orders_v2":
                    return await ProcessOrderNotification(req, resource);
                case "stock-locations":
                    return await EnqueueStockNotificationAsync(req, resource, topic);
                default:
                    _logger.LogInformation("Tópico no manejado: {Topic}. Resource: {Resource}", topic, resource);
                    return req.CreateResponse(HttpStatusCode.OK);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando webhook. Topic: {Topic}, Resource: {Resource}", topic, resource);
            throw;
        }
    }

    private async Task<HttpResponseData> EnqueueStockNotificationAsync(HttpRequestData req, string? resource, string? topic)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            _logger.LogWarning("Webhook stock-locations recibido sin resource.");
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var message = new StockLocationQueueMessage
        {
            Topic = topic,
            Resource = resource,
            ReceivedAtUtc = DateTimeOffset.UtcNow
        };

        await _stockQueueService.EnqueueAsync(message, CancellationToken.None);
        _logger.LogInformation("Webhook stock-locations encolado. Resource: {Resource}", resource);
        return req.CreateResponse(HttpStatusCode.OK);
    }

    private async Task<HttpResponseData> ProcessOrderNotification(HttpRequestData req, string? resource)
    {
        var (isValid, orderId) = WebhookOrderResourceHelper.TryParseOrderIdFromResource(resource);
        if (!isValid || string.IsNullOrWhiteSpace(orderId))
        {
            if (!string.IsNullOrWhiteSpace(resource))
                _logger.LogDebug("webhook ignorado: resource sin orderId válido: {Resource}", resource);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Resolve execution key: PackId (group) or OrderId (single). Spec 02.
        // If GetOrderAsync returns null (order not found), executionKey = orderId; ProcessAsync returns (null,null); we still MarkDoneAsync to avoid stuck executions.
        var orderDto = await _meliClient.GetOrderAsync(orderId);
        var executionKey = !string.IsNullOrWhiteSpace(orderDto?.PackId) ? orderDto.PackId : orderId;

        var dryRun = EnvVars.GetBool(EnvVars.Keys.DryRun, false);
        if (!dryRun && !await _orderExecutionStore.TryStartExecutionAsync(executionKey))
        {
            _logger.LogDebug("Order/Pack {Key} already locked or done. Returning 200 OK.", executionKey);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        try
        {
            var (orderIdWritten, noteText) = await _processor.ProcessAsync(orderId, orderDto);
            _logger.LogInformation("Nota: {NoteText}", noteText);

            var res = req.CreateResponse(HttpStatusCode.OK);
            if (!string.IsNullOrWhiteSpace(noteText))
            {
                await res.WriteStringAsync(noteText, Encoding.UTF8);
            }
            return res;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando orden {OrderId}", orderId);
            throw;
        }
        finally
        {
            await _orderExecutionStore.MarkDoneAsync(executionKey);
        }
    }
}


