using meli_znube_integration.Common;
using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

/// <summary>Builds synthetic source items for FULL rules from Mappings (SKU-only; quantities filled by EnrichSourceItemsWithZnubeStockAsync).</summary>
public static class FullRuleSourceItemsHelper
{
    public static List<MeliItem> BuildSyntheticSourceItemsForFullRule(StockRuleDto rule, string? filterSku = null)
    {
        if (rule?.Mappings == null || rule.Mappings.Count == 0) return [];
        var variations = rule.Mappings
            .Where(m => filterSku is null || m.TargetSku.Equals(filterSku, StringComparison.OrdinalIgnoreCase))
            .Select(m => new MeliVariation
            {
                Attributes = [new() { Id = MeliConstants.SellerSkuAttributeId, ValueName = m.TargetSku }],
                AvailableQuantity = 0
            }).ToList();
        return
        [
            new() { Id = "", Variations = variations }
        ];
    }
}
