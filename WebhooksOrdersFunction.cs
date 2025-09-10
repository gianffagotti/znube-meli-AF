using meli_znube_integration.Api;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace meli_znube_integration;

public class WebhooksOrdersFunction
{
    private readonly PackProcessor _processor;
    private readonly ILogger<WebhooksOrdersFunction> _logger;

    public WebhooksOrdersFunction(PackProcessor processor, ILogger<WebhooksOrdersFunction> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    [Function("WebhooksOrders")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhooks/orders")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        string? resource = null;
        string? orderId = null;
        try
        {
            using (var doc = await JsonDocument.ParseAsync(req.Body, default, cancellationToken))
            {
                if (doc.RootElement.TryGetProperty("resource", out var resourceProp) && resourceProp.ValueKind == JsonValueKind.String)
                {
                    resource = resourceProp.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(resource))
            {
                var resEmpty = req.CreateResponse(HttpStatusCode.OK);
                return resEmpty;
            }

            // Aceptar solo recursos de órdenes
            if (resource!.IndexOf("/orders/", StringComparison.OrdinalIgnoreCase) < 0)
            {
                var resSkip = req.CreateResponse(HttpStatusCode.OK);
                return resSkip;
            }

            // Validaciones tempranas del recurso y orderId
            orderId = ExtractLastSegment(resource!);
            if (string.IsNullOrWhiteSpace(orderId) || !orderId.Trim().All(char.IsDigit))
            {
                _logger.LogDebug("webhook ignorado: resource sin orderId válido: {Resource}", resource);
                var resInvalid = req.CreateResponse(HttpStatusCode.OK);
                return resInvalid;
            }
            var (orderIdWritten, noteText) = await _processor.ProcessAsync(orderId, cancellationToken);

            var res = req.CreateResponse(HttpStatusCode.OK);
            if (!string.IsNullOrWhiteSpace(noteText))
            {
                await res.WriteStringAsync(noteText, Encoding.UTF8);
            }
            return res;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error no controlado procesando orden {OrderId}", orderId);
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


