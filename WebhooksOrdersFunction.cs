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
        string? resource = null;
        string? orderId = null;
        string? noteText = null;
        try
        {
            using (var doc = await JsonDocument.ParseAsync(req.Body))
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

            orderId = ExtractLastSegment(resource!);
            var accessToken = await _auth.GetValidAccessTokenAsync();

            // Verificación temprana para evitar trabajo innecesario si ya existe una nota automática
            try
            {
                const string autoPrefix = "[AUTO] ";
                var existingNotes = await _meli.GetOrderNotesAsync(orderId, accessToken);
                if (existingNotes.Any(n => !string.IsNullOrWhiteSpace(n) && n!.StartsWith(autoPrefix, StringComparison.Ordinal)))
                {
                    var resEarly = req.CreateResponse(HttpStatusCode.OK);
                    return resEarly;
                }
            }
            catch
            {
                // Si falla la lectura de notas, continuar con el flujo normal
            }

            var order = await _meli.GetOrderAsync(orderId, accessToken);
            if (order == null)
            {
                var resOk = req.CreateResponse(HttpStatusCode.OK);
                return resOk;
            }

            var zone = await _meli.TryGetBuyerZoneAsync(order, accessToken);

            var assignments = await _znube.GetAssignmentsForOrderAsync(order);
            var lines = new List<string>();
            lines.AddRange(assignments);
            if (!string.IsNullOrWhiteSpace(zone))
            {
                lines.Add($"({zone})");
            }
            noteText = string.Join("\n", lines);

            // await _meli.UpsertOrderNoteAsync(orderId, noteText, accessToken);

            var res = req.CreateResponse(HttpStatusCode.OK);
            if (!string.IsNullOrWhiteSpace(noteText))
            {
                await res.WriteStringAsync(noteText, Encoding.UTF8);
            }

            _logger.LogInformation("noteText compuesto: {NoteText}", noteText);
            _logger.LogInformation("Orden {OrderId} procesada correctamente.", orderId);

            return res;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error no controlado procesando orden {OrderId}", orderId);
            try
            {
                if (!string.IsNullOrWhiteSpace(orderId))
                {
                    var generic = $"ERROR procesando webhook para orden {orderId}. Verificar y reintentar.";
                    string? at = null;
                    try { at = await _auth.GetValidAccessTokenAsync(); } catch { }
                    if (!string.IsNullOrWhiteSpace(at))
                    {
                        //await _meli.UpsertOrderNoteAsync(orderId!, generic, at!);
                    }
                }
            }
            catch { }
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


