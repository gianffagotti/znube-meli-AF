using meli_znube_integration.Api;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace meli_znube_integration
{
    public class WebhooksOrdersFunction
    {
        private readonly MeliAuth _auth;
        private readonly MeliClient _meli;
        private readonly ZnubeClient _znube;
        private readonly ILogger<WebhooksOrdersFunction> _logger;

        public WebhooksOrdersFunction(MeliAuth auth, MeliClient meli, ZnubeClient znube, ILogger<WebhooksOrdersFunction> logger)
        {
            _auth = auth;
            _meli = meli;
            _znube = znube;
            _logger = logger;
        }

        [Function("WebhooksOrders")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhooks/orders")] HttpRequestData req)
        {
            _logger.LogInformation("Webhook /webhooks/orders recibido.");
            string? resource = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.TryGetProperty("resource", out var resourceProp) && resourceProp.ValueKind == JsonValueKind.String)
                {
                    resource = resourceProp.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo parsear el body del webhook.");
            }

            _logger.LogInformation("resource: {Resource}", resource);

            if (string.IsNullOrWhiteSpace(resource))
            {
                var resEmpty = req.CreateResponse(HttpStatusCode.OK);
                return resEmpty;
            }

            string orderId = ExtractLastSegment(resource!);
            _logger.LogInformation("orderId: {OrderId}", orderId);

            string? noteText = null;
            try
            {
                var accessToken = await _auth.GetValidAccessTokenAsync();
                var order = await _meli.GetOrderAsync(orderId, accessToken);
                if (order == null)
                {
                    _logger.LogInformation("Orden {OrderId} no disponible. Respondiendo 200 OK sin contenido.", orderId);
                    var resOk = req.CreateResponse(HttpStatusCode.OK);
                    return resOk;
                }

                var zone = await _meli.TryGetBuyerZoneAsync(order, accessToken);

                var assignments = await _znube.GetAssignmentsForOrderAsync(order);
                var lines = new List<string>();
                if (!string.IsNullOrWhiteSpace(zone))
                {
                    lines.Add($"Zona: {zone}");
                }
                lines.AddRange(assignments);
                noteText = string.Join("\n", lines);

                _logger.LogInformation("noteText compuesto: {NoteText}", noteText);

                // await _meli.UpsertOrderNoteAsync(orderId, noteText, accessToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando webhook de orden {OrderId}", orderId);
            }

            var res = req.CreateResponse(HttpStatusCode.OK);
            if (!string.IsNullOrWhiteSpace(noteText))
            {
                await res.WriteStringAsync(noteText, Encoding.UTF8);
            }
            return res;
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
}


