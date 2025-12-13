using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace meli_znube_integration.Functions;

public class WebhookNotificationFunction
{
    private readonly PackProcessor _processor;
    private readonly ILogger<WebhookNotificationFunction> _logger;
    private readonly IConfiguration _configuration;

    public WebhookNotificationFunction(PackProcessor processor, ILogger<WebhookNotificationFunction> logger, IConfiguration configuration)
    {
        _processor = processor;
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
                case "stock-location":
                    _logger.LogInformation("Notificación de stock recibida e ignorada por configuración actual. Resource: {Resource}", resource);
                    return req.CreateResponse(HttpStatusCode.OK);
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

    private async Task<HttpResponseData> ProcessOrderNotification(HttpRequestData req, string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Aceptar solo recursos de órdenes
        if (resource!.IndexOf("/orders/", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Validaciones tempranas del recurso y orderId
        var orderId = ExtractLastSegment(resource!);
        if (string.IsNullOrWhiteSpace(orderId) || !orderId.Trim().All(char.IsDigit))
        {
            _logger.LogDebug("webhook ignorado: resource sin orderId válido: {Resource}", resource);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        try
        {
            var (orderIdWritten, noteText) = await _processor.ProcessAsync(orderId);
            _logger.LogInformation($"Nota: {noteText}");

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
    }

    private static string ExtractLastSegment(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[parts.Length - 1] : string.Empty;
    }
}


