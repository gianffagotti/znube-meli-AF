using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace meli_znube_integration.Services;

internal sealed class DiscoveryResult { public int Processed; public int Created; public int Incomplete; }
internal sealed class ProcessItemResult { public bool Saved; public bool IsIncomplete; }

/// <summary>Scans MELI for fulfillment items, validates SKUs in Znube, saves FULL rules and StockSkuIndex. Spec 05.</summary>
public class FullRuleDiscoveryService
{
    private readonly IMeliApiClient _meliClient;
    private readonly IZnubeApiClient _znubeClient;
    private readonly StockRuleService _stockRuleService;
    private readonly IDashboardLogService _dashboardLogService;
    private readonly ILogger<FullRuleDiscoveryService> _logger;

    public FullRuleDiscoveryService(
        IMeliApiClient meliClient,
        IZnubeApiClient znubeClient,
        StockRuleService stockRuleService,
        IDashboardLogService dashboardLogService,
        ILogger<FullRuleDiscoveryService> logger)
    {
        _meliClient = meliClient;
        _znubeClient = znubeClient;
        _stockRuleService = stockRuleService;
        _dashboardLogService = dashboardLogService;
        _logger = logger;
    }

    public async Task<(int Processed, int Created, int Incomplete)> ExecuteDiscoveryAsync(CancellationToken ct, Action? heartbeat = null)
    {
        var sellerId = long.Parse(EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId));
        var sellerIdStr = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
        int processed = 0, created = 0, incomplete = 0;
        string? scrollId = null;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            heartbeat?.Invoke();

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
                    cancellationToken.ThrowIfCancellationRequested();
                    var items = await _meliClient.GetItemsAsync(chunk, cancellationToken);
                    foreach (var item in items ?? new List<MeliItem>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
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
                            cancellationToken.ThrowIfCancellationRequested();
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

        return (processed, created, incomplete);
    }

    private async Task<ProcessItemResult> ProcessFulfillmentItemAsync(MeliItem item, string sellerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();
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
