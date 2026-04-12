using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using meli_znube_integration.Clients;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace meli_znube_integration.Functions;

/// <summary>Proxy/validation endpoints for Znube (e.g. validate SKUs for FULL rule editor).</summary>
public class ZnubeProxyApi
{
    private readonly IZnubeApiClient _znubeClient;

    public ZnubeProxyApi(IZnubeApiClient znubeClient)
    {
        _znubeClient = znubeClient;
    }

    /// <summary>POST body: { "skus": ["SKU1", "SKU2", ...] }. Returns { "results": [ { "sku": "SKU1", "exists": true }, ... ] }. Exists when Znube GetStockBySku returns TotalSku >= 1.</summary>
    [Function("ValidateSkusInZnube")]
    public async Task<HttpResponseData> ValidateSkus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "znube/validate-skus")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<ValidateSkusRequest>(body ?? "{}");
            var skus = payload?.Skus ?? Array.Empty<string>();
            if (skus.Length == 0)
            {
                var emptyResponse = req.CreateResponse(HttpStatusCode.OK);
                await emptyResponse.WriteAsJsonAsync(new ValidateSkusResponse { Results = new List<SkuValidationResult>() });
                return emptyResponse;
            }

            var results = new List<SkuValidationResult>();
            foreach (var sku in skus.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(sku))
                {
                    results.Add(new SkuValidationResult { Sku = sku ?? "", Exists = false });
                    continue;
                }
                var normalized = StockRuleService.NormalizeSku(sku);
                var response = await _znubeClient.GetStockBySkuAsync(normalized);
                var exists = response?.Data != null && response.Data.TotalSku >= 1;
                results.Add(new SkuValidationResult { Sku = sku.Trim(), Exists = exists });
            }

            var res = req.CreateResponse(HttpStatusCode.OK);
            await res.WriteAsJsonAsync(new ValidateSkusResponse { Results = results });
            return res;
        }
        catch (Exception)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Validation failed." });
            return errorResponse;
        }
    }

    private class ValidateSkusRequest
    {
        [JsonPropertyName("skus")]
        public string[]? Skus { get; set; }
    }

    private class ValidateSkusResponse
    {
        [JsonPropertyName("results")]
        public List<SkuValidationResult> Results { get; set; } = new();
    }

    private class SkuValidationResult
    {
        [JsonPropertyName("sku")]
        public string Sku { get; set; } = "";
        [JsonPropertyName("exists")]
        public bool Exists { get; set; }
    }
}
