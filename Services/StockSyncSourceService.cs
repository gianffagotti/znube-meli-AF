using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
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

    public async Task EnrichSourceItemsWithZnubeStockAsync(List<MeliItem> sourceItems, CancellationToken cancellationToken = default)
    {
        if (sourceItems == null) return;
        foreach (var item in sourceItems)
        {
            if (item.Variations != null && item.Variations.Count > 0)
            {
                foreach (var variation in item.Variations)
                {
                    var sku = variation.SellerSku ?? item.SellerSku;
                    if (string.IsNullOrWhiteSpace(sku)) continue;
                    var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sku);
                    var qty = await GetZnubeQuantityBySkuAsync(normalizedSku, cancellationToken);
                    variation.AvailableQuantity = qty;
                }
            }
            else
            {
                var sku = item.SellerSku;
                if (string.IsNullOrWhiteSpace(sku)) continue;
                var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sku);
                var qty = await GetZnubeQuantityBySkuAsync(normalizedSku, cancellationToken);
                item.AvailableQuantity = qty;
            }
        }
    }

    /// <summary>Znube 404 or missing data → 0 (fail-safe). Spec 03.</summary>
    private async Task<int> GetZnubeQuantityBySkuAsync(string sku, CancellationToken ct)
    {
        try
        {
            var response = await _znubeClient.GetStockBySkuAsync(sku, ct);
            if (response?.Data?.Stock == null) return 0;
            var skuItem = response.Data.Stock.FirstOrDefault(s => string.Equals(s.Sku, sku, StringComparison.OrdinalIgnoreCase));
            if (skuItem?.Stock == null) return 0;
            return (int)Math.Max(0, skuItem.Stock.Sum(d => d.Quantity));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Znube stock fetch for SKU {Sku} failed; using 0.", sku);
            return 0;
        }
    }

    public async Task<bool> ShouldSkipFulfillmentTargetAsync(MeliItem targetItem, CancellationToken cancellationToken = default)
    {
        if (targetItem == null) return true;
        var logisticType = targetItem.Shipping?.LogisticType;
        if (string.IsNullOrWhiteSpace(logisticType)) return false;
        if (!string.Equals(logisticType, "fulfillment", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(logisticType, "full", StringComparison.OrdinalIgnoreCase))
            return false;

        // Item is FULL: skip unless hybrid (has selling_address stock)
        var userProductId = GetFirstUserProductId(targetItem);
        if (string.IsNullOrWhiteSpace(userProductId)) return true;

        var stockResponse = await _meliClient.GetUserProductStockResponseAsync(userProductId, cancellationToken);
        var hasSellingAddress = stockResponse?.Locations?.Any(l =>
            string.Equals(l.Type, "selling_address", StringComparison.OrdinalIgnoreCase)) == true;

        if (!hasSellingAddress)
            _logger.LogDebug("Target item {ItemId} is FULL-only (no selling_address). Skipping.", targetItem.Id);
        return !hasSellingAddress;
    }

    private static string? GetFirstUserProductId(MeliItem item)
    {
        if (item.Variations != null && item.Variations.Count > 0)
        {
            var first = item.Variations.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.UserProductId));
            return first?.UserProductId;
        }
        return !string.IsNullOrWhiteSpace(item.UserProductId) ? item.UserProductId : null;
    }
}
