using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace meli_znube_integration.Functions;

public class WebhookNotificationFunction
{
    private readonly StockLocationQueueService _stockQueueService;
    private readonly OrderQueueService _orderQueueService;
    private readonly ILogger<WebhookNotificationFunction> _logger;
    private readonly IConfiguration _configuration;

    public WebhookNotificationFunction(
        StockLocationQueueService stockQueueService,
        OrderQueueService orderQueueService,
        ILogger<WebhookNotificationFunction> logger,
        IConfiguration configuration)
    {
        _stockQueueService = stockQueueService;
        _orderQueueService = orderQueueService;
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
                    return await EnqueueOrderNotificationAsync(req, resource, topic);
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

    private async Task<HttpResponseData> EnqueueOrderNotificationAsync(HttpRequestData req, string? resource, string? topic)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            _logger.LogWarning("Webhook orders_v2 recibido sin resource.");
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var message = new OrderQueueMessage
        {
            Topic = topic,
            Resource = resource,
            ReceivedAtUtc = DateTimeOffset.UtcNow
        };

        await _orderQueueService.EnqueueAsync(message, CancellationToken.None);
        _logger.LogInformation("Webhook orders_v2 encolado. Resource: {Resource}", resource);
        return req.CreateResponse(HttpStatusCode.OK);
    }
}


