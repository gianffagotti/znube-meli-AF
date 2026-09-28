using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models.Canonical;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

/// <summary>
/// Znube as source of truth for quantities; Anti-FULL guard (skip fulfillment unless hybrid). Spec 03.
/// </summary>
public class StockSyncSourceService : IStockSyncSourceService
{
    private readonly IZnubeApiClient _znubeClient;
    private readonly IMeliApiClient _meliClient;
    private readonly ILogger<StockSyncSourceService> _logger;

    public StockSyncSourceService(IZnubeApiClient znubeClient, IMeliApiClient meliClient, ILogger<StockSyncSourceService> logger)
    {
        _znubeClient = znubeClient;
        _meliClient = meliClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CanonicalItem>> EnrichSourceItemsWithZnubeStockAsync(
        IReadOnlyList<CanonicalItem> sourceItems,
        string ruleType,
        bool fromWorker,
        CancellationToken cancellationToken = default)
    {
        if (sourceItems == null || sourceItems.Count == 0) return [];

        var useProductId = fromWorker ||
            (sourceItems.SelectMany(si => si.Variations).Count() > 5 &&
             string.Equals(ruleType, StockRuleTypes.Pack, StringComparison.OrdinalIgnoreCase));

        if (useProductId)
            return await EnrichByProductIdAsync(sourceItems, cancellationToken);
        else
            return await EnrichBySkuAsync(sourceItems, cancellationToken);
    }

    /// <summary>Strategy by SKU: one call per variant. 404 → 0; 5xx/timeout → propagate. Spec 03.</summary>
    private async Task<IReadOnlyList<CanonicalItem>> EnrichBySkuAsync(IReadOnlyList<CanonicalItem> sourceItems, CancellationToken ct)
    {
        var result = new List<CanonicalItem>(sourceItems.Count);

        foreach (var item in sourceItems)
        {
            var enrichedVariants = new List<CanonicalVariant>(item.Variations.Count);
            foreach (var variant in item.Variations)
            {
                var sku = variant.SellerSku;
                if (string.IsNullOrWhiteSpace(sku))
                {
                    enrichedVariants.Add(variant);
                    continue;
                }
                var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sku);
                var qty = await GetZnubeQuantityBySkuAsync(normalizedSku, ct);
                enrichedVariants.Add(variant with { AvailableQuantity = qty });
            }
            result.Add(item with { Variations = enrichedVariants });
        }

        return result;
    }

    /// <summary>Strategy by ProductId: group by productId, one call per product, map to variants. 404/empty → 0 for that product; 5xx → propagate. Spec 03.</summary>
    private async Task<IReadOnlyList<CanonicalItem>> EnrichByProductIdAsync(IReadOnlyList<CanonicalItem> sourceItems, CancellationToken ct)
    {
        // Build SKU → productId mapping
        var skuToProductId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var productIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in sourceItems)
        {
            foreach (var variant in item.Variations)
            {
                var sku = variant.SellerSku;
                if (string.IsNullOrWhiteSpace(sku)) continue;
                var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sku);
                var productId = ZnubeLogicExtensions.GetProductIdFromSku(normalizedSku);
                productIds.Add(productId);
                skuToProductId[normalizedSku] = productId;
            }
        }

        // Fetch stock by productId
        var skuToQty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var productId in productIds)
        {
            var response = await _znubeClient.GetStockByProductIdAsync(productId, ct);
            if (response?.Data?.Stock == null) continue;
            foreach (var skuItem in response.Data.Stock)
            {
                if (skuItem == null) continue;
                var sku = skuItem.Sku;
                if (string.IsNullOrWhiteSpace(sku) || !skuToProductId.ContainsKey(sku)) continue;
                var qty = skuItem.Stock == null ? 0 : (int)Math.Max(0, skuItem.Stock.Sum(d => d.Quantity));
                skuToQty[sku] = qty;
            }
        }

        // Build enriched CanonicalItem list (immutable, using with {})
        var result = new List<CanonicalItem>(sourceItems.Count);
        foreach (var item in sourceItems)
        {
            var enrichedVariants = new List<CanonicalVariant>(item.Variations.Count);
            foreach (var variant in item.Variations)
            {
                var sku = variant.SellerSku;
                if (string.IsNullOrWhiteSpace(sku))
                {
                    enrichedVariants.Add(variant);
                    continue;
                }
                var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sku);
                var qty = skuToQty.TryGetValue(normalizedSku, out var q) ? q : 0;
                enrichedVariants.Add(variant with { AvailableQuantity = qty });
            }
            result.Add(item with { Variations = enrichedVariants });
        }

        return result;
    }

    /// <summary>Znube 404 (null response) → 0. 5xx/timeout → propagate (never return 0 to avoid mass-zero on MELI). Spec 03.</summary>
    private async Task<int> GetZnubeQuantityBySkuAsync(string sku, CancellationToken ct)
    {
        var response = await _znubeClient.GetStockBySkuAsync(sku, ct);
        if (response?.Data?.Stock == null) return 0;
        var skuItem = response.Data.Stock.FirstOrDefault(s => string.Equals(s.Sku, sku, StringComparison.OrdinalIgnoreCase));
        if (skuItem?.Stock == null) return 0;
        return (int)Math.Max(0, skuItem.Stock.Sum(d => d.Quantity));
    }

    public async Task<bool> ShouldSkipFulfillmentTargetAsync(CanonicalItem targetItem, CancellationToken cancellationToken = default)
    {
        if (targetItem == null) return true;

        var logisticType = targetItem.LogisticType;
        if (string.IsNullOrWhiteSpace(logisticType)) return false;
        if (!string.Equals(logisticType, "fulfillment", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(logisticType, "full", StringComparison.OrdinalIgnoreCase))
            return false;

        // Item is FULL: skip unless hybrid (has selling_address stock)
        var userProductId = GetFirstVariantId(targetItem);
        if (string.IsNullOrWhiteSpace(userProductId)) return true;

        var stockResponse = await _meliClient.GetUserProductStockResponseAsync(userProductId, cancellationToken);
        var hasSellingAddress = stockResponse?.Locations?.Any(l =>
            string.Equals(l.Type, "selling_address", StringComparison.OrdinalIgnoreCase)) == true;

        if (!hasSellingAddress)
            _logger.LogDebug("Target item {ItemId} is FULL-only (no selling_address). Skipping.", targetItem.Id);
        return !hasSellingAddress;
    }

    private static string? GetFirstVariantId(CanonicalItem item)
    {
        return item.Variations.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.VariantId))?.VariantId;
    }
}
