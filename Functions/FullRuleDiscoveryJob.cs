using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace meli_znube_integration.Functions;

internal sealed class DiscoveryResult { public int Processed; public int Created; public int Incomplete; }
internal sealed class ProcessItemResult { public bool Saved; public bool IsIncomplete; }

/// <summary>Replaces StockMappingIndexer. Scans MELI for fulfillment items, validates SKUs in Znube, saves FULL rules and StockSkuIndex. Spec 05.</summary>
public class FullRuleDiscoveryJob
{
    private readonly IMeliApiClient _meliClient;
    private readonly IZnubeApiClient _znubeClient;
    private readonly StockRuleService _stockRuleService;
    private readonly IDashboardLogService _dashboardLogService;
    private readonly ILogger<FullRuleDiscoveryJob> _logger;

    public FullRuleDiscoveryJob(
        IMeliApiClient meliClient,
        IZnubeApiClient znubeClient,
        StockRuleService stockRuleService,
        IDashboardLogService dashboardLogService,
        ILogger<FullRuleDiscoveryJob> logger)
    {
        _meliClient = meliClient;
        _znubeClient = znubeClient;
        _stockRuleService = stockRuleService;
        _dashboardLogService = dashboardLogService;
        _logger = logger;
    }

    [Function("FullRuleDiscoveryJob")]
    public async Task Run([TimerTrigger("0 0 4 * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Full Rule Discovery job started at {Time}.", DateTime.UtcNow);

        if (!EnvVars.GetBool(EnvVars.Keys.EnableJobFullDiscovery, true))
        {
            _logger.LogWarning("Job 'FullRuleDiscoveryJob' is disabled via configuration.");
            return;
        }

        try
        {
            var result = await ExecuteDiscoveryAsync(CancellationToken.None);
            var processed = result.Processed;
            var created = result.Created;
            var incomplete = result.Incomplete;
            _logger.LogInformation("Full Rule Discovery finished. Processed: {Processed}, FULL rules created: {Created}, Incomplete: {Incomplete}.",
                processed, created, incomplete);
            var summaryMessage = "Full Rule Discovery finalizado. Publicaciones procesadas: " + processed + ". Reglas FULL creadas: " + created + ". Reglas incompletas: " + incomplete + ".";
            var detailsJson = JsonSerializer.Serialize(new { processed, created, incomplete });
            await _dashboardLogService.AppendLogAsync("Info", "FullRuleDiscovery", summaryMessage, detailsJson, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Full Rule Discovery job failed.");
            throw;
        }
    }

    [Function("DiscoverFullRules")]
    public async Task<HttpResponseData> RunManual(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jobs/discover-full-rules")] HttpRequestData req)
    {
        if (!EnvVars.GetBool(EnvVars.Keys.EnableJobFullDiscovery, true))
        {
            var disabled = req.CreateResponse(HttpStatusCode.OK);
            await disabled.WriteAsJsonAsync(new { message = "Job is disabled.", run = false });
            return disabled;
        }

        try
        {
            var result = await ExecuteDiscoveryAsync(req.FunctionContext.CancellationToken);
            var processed = result.Processed;
            var created = result.Created;
            var incomplete = result.Incomplete;
            var summaryMessage = "Full Rule Discovery finalizado. Publicaciones procesadas: " + processed + ". Reglas FULL creadas: " + created + ". Reglas incompletas: " + incomplete + ".";
            var detailsJson = JsonSerializer.Serialize(new { processed, created, incomplete });
            await _dashboardLogService.AppendLogAsync("Info", "FullRuleDiscovery", summaryMessage, detailsJson, null, req.FunctionContext.CancellationToken);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { processed, created, incomplete, message = "Discovery completed." });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DiscoverFullRules HTTP execution failed.");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Discovery failed.", message = ex.Message });
            return errorResponse;
        }
    }

    private async Task<DiscoveryResult> ExecuteDiscoveryAsync(CancellationToken ct)
    {
        var sellerId = long.Parse(EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId));
        var sellerIdStr = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
        int processed = 0, created = 0, incomplete = 0;
        string? scrollId = null;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 2 };

        while (true)
        {
            MeliScanResponseDto? scanResult;
            try
            {
                scanResult = await _meliClient.ScanItemsAsync(sellerId, scrollId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning items. Aborting.");
                break;
            }

            if (scanResult?.Results == null || scanResult.Results.Count == 0)
                break;

            scrollId = scanResult.ScrollId;
            var chunks = scanResult.Results.Chunk(10);

            await Parallel.ForEachAsync(chunks, parallelOptions, async (chunk, cancellationToken) =>
            {
                try
                {
                    var items = await _meliClient.GetItemsAsync(chunk, cancellationToken);
                    foreach (var item in items ?? new List<MeliItem>())
                    {
                        Interlocked.Increment(ref processed);
                        var processResult = await ProcessFulfillmentItemAsync(item, sellerIdStr, cancellationToken);
                        var saved = processResult.Saved;
                        var isIncomplete = processResult.IsIncomplete;
                        if (saved)
                        {
                            Interlocked.Increment(ref created);
                            if (isIncomplete) Interlocked.Increment(ref incomplete);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing chunk. Trying one-by-one.");
                    foreach (var id in chunk)
                    {
                        try
                        {
                            var single = await _meliClient.GetItemsAsync(new[] { id }, cancellationToken);
                            if (single != null && single.Count > 0)
                            {
                                Interlocked.Increment(ref processed);
                                var processResult = await ProcessFulfillmentItemAsync(single[0], sellerIdStr, cancellationToken);
                                var saved = processResult.Saved;
                                var isIncomplete = processResult.IsIncomplete;
                                if (saved) { Interlocked.Increment(ref created); if (isIncomplete) Interlocked.Increment(ref incomplete); }
                            }
                        }
                        catch (Exception inner) { _logger.LogError(inner, "Error processing item {Id}.", id); }
                    }
                }
            });
        }

        return new DiscoveryResult { Processed = processed, Created = created, Incomplete = incomplete };
    }

    private async Task<ProcessItemResult> ProcessFulfillmentItemAsync(MeliItem item, string sellerId, CancellationToken ct)
    {
        var logisticType = item.Shipping?.LogisticType;
        if (string.IsNullOrWhiteSpace(logisticType) || !string.Equals(logisticType, "fulfillment", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessItemResult { Saved = false, IsIncomplete = false };
        }

        var existingRule = await _stockRuleService.GetRuleAsync(sellerId, item.Id);
        var isPackOrCombo = existingRule != null && StockRuleTypes.IsPackOrCombo(existingRule.RuleType);
        if (isPackOrCombo)
        {
            return new ProcessItemResult { Saved = false, IsIncomplete = false };
        }

        var mappings = new List<(string TargetVariantId, string TargetSku)>();
        if (item.Variations != null && item.Variations.Count > 0)
        {
            foreach (var v in item.Variations)
            {
                var sku = ExtractSku(item, v);
                if (!string.IsNullOrWhiteSpace(sku))
                    mappings.Add((v.UserProductId ?? v.Id.ToString(), sku!));
            }
        }
        else
        {
            var sku = ExtractSku(item, null);
            if (!string.IsNullOrWhiteSpace(sku))
                mappings.Add((item.UserProductId ?? item.Id, sku!));
        }

        if (mappings.Count == 0) return new ProcessItemResult { Saved = false, IsIncomplete = false };

        var skusInZnube = new List<string>();
        var skusMissing = new List<string>();
        foreach (var m in mappings)
        {
            var sku = m.TargetSku;
            var normalized = StockRuleService.NormalizeSku(sku);
            var response = await _znubeClient.GetStockBySkuAsync(normalized, ct);
            if (response?.Data != null && response.Data.TotalSku >= 1)
                skusInZnube.Add(sku);
            else
                skusMissing.Add(sku);
        }

        if (skusInZnube.Count == 0)
            return new ProcessItemResult { Saved = false, IsIncomplete = false };

        var isIncomplete = skusMissing.Count > 0;
        if (isIncomplete)
        {
            await _dashboardLogService.AppendLogAsync("Warning", "FullRuleDiscovery",
                "Regla FULL incompleta: algunos SKUs no encontrados en Znube.",
                JsonSerializer.Serialize(new { targetItemId = item.Id, targetTitle = item.Title, missingSkus = skusMissing }),
                new[] { item.Id }, ct);
        }

        var targetSku = mappings[0].TargetSku;
        var ruleDto = new StockRuleDto
        {
            TargetItemId = item.Id,
            TargetTitle = item.Title ?? "",
            TargetThumbnail = item.Thumbnail,
            TargetSku = targetSku,
            RuleType = StockRuleTypes.Full,
            IsIncomplete = isIncomplete,
            Components = new List<RuleComponentDto>(),
            Mappings = mappings.Select(m => new VariantMappingDto
            {
                TargetVariantId = m.TargetVariantId,
                TargetSku = m.TargetSku
            }).ToList()
        };

        await _stockRuleService.SaveRuleAsync(ruleDto);
        return new ProcessItemResult { Saved = true, IsIncomplete = isIncomplete };
    }

    private static string? ExtractSku(MeliItem item, MeliVariation? variation)
    {
        if (variation != null)
        {
            var skuAttr = variation.Attributes?.FirstOrDefault(a => string.Equals(a.Id, MeliConstants.SellerSkuAttributeId, StringComparison.OrdinalIgnoreCase));
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName.Trim();
            if (!string.IsNullOrWhiteSpace(variation.SellerCustomField)) return variation.SellerCustomField.Trim();
        }
        else
        {
            var skuAttr = item.Attributes?.FirstOrDefault(a => string.Equals(a.Id, MeliConstants.SellerSkuAttributeId, StringComparison.OrdinalIgnoreCase));
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName.Trim();
            if (!string.IsNullOrWhiteSpace(item.SellerSku)) return item.SellerSku.Trim();
            if (!string.IsNullOrWhiteSpace(item.SellerCustomField)) return item.SellerCustomField.Trim();
        }
        return null;
    }
}
