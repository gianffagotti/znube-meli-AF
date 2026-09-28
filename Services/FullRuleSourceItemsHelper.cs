using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Canonical;

namespace meli_znube_integration.Services;

/// <summary>Builds synthetic source CanonicalItems for FULL rules from Mappings (SKU-only; quantities filled by EnrichSourceItemsWithZnubeStockAsync).</summary>
public static class FullRuleSourceItemsHelper
{
    public static IReadOnlyList<CanonicalItem> BuildSyntheticSourceItemsForFullRule(StockRuleDto rule, string? filterSku = null)
    {
        if (rule?.Mappings == null || rule.Mappings.Count == 0) return [];

        var variations = rule.Mappings
            .Where(m => filterSku is null || m.TargetSku.Equals(filterSku, StringComparison.OrdinalIgnoreCase))
            .Select(m => new CanonicalVariant
            {
                VariantId = "",  // Placeholder: FULL rules match by SKU, VariantId is unused for source variants
                SellerSku = StockRuleService.NormalizeSku(m.TargetSku),
                AvailableQuantity = 0   // Filled by EnrichSourceItemsWithZnubeStockAsync
            })
            .ToList();

        return
        [
            new CanonicalItem { Id = "", Variations = variations }
        ];
    }
}
