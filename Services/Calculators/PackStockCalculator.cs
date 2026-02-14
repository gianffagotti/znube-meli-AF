using meli_znube_integration.Models;

namespace meli_znube_integration.Services.Calculators;

/// <summary>
/// Calculates stock for PACK rules. Spec V2: algorithm is per-variant by Strategy.
/// Explicit: pool = sum of SourceMatches stock; DynamicSize: pool = sum of source variants where ParseSizeFromSku(sku) == MatchSize.
/// Target = Floor(pool / PackQuantity). Missing mapping or no match → 0 (fail-safe).
/// </summary>
public class PackStockCalculator : IStockCalculator
{
    public string RuleType => "PACK";

    /// <summary>Split by '#', take last segment (trimmed). Null/empty or no '#' → null. Used for DynamicSize and CODIGO#COLOR#TALLE-style SKUs.</summary>
    public static string? ParseSizeFromSku(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var idx = sku.LastIndexOf('#');
        if (idx < 0) return null;
        var segment = sku.Substring(idx + 1).Trim();
        return string.IsNullOrEmpty(segment) ? null : segment;
    }

    public Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleDto rule, MeliItem targetItem, List<MeliItem> sourceItems)
    {
        var updates = new List<VariantStockUpdate>();

        var sourceVariants = sourceItems
            .SelectMany(i => i.Variations ?? new List<MeliVariation> { new MeliVariation {
                UserProductId = !string.IsNullOrEmpty(i.UserProductId) ? i.UserProductId : i.Id,
                SellerSku = i.SellerSku,
                AvailableQuantity = i.AvailableQuantity
            }})
            .ToList();

        int defaultPackQty = rule.DefaultPackQuantity > 0 ? rule.DefaultPackQuantity : 1;

        var targetVariants = targetItem.Variations ?? new List<MeliVariation> { new MeliVariation {
            UserProductId = !string.IsNullOrEmpty(targetItem.UserProductId) ? targetItem.UserProductId : targetItem.Id,
            SellerSku = targetItem.SellerSku
        }};

        foreach (var targetVar in targetVariants)
        {
            if (string.IsNullOrWhiteSpace(targetVar.SellerSku) && string.IsNullOrWhiteSpace(targetVar.UserProductId)) continue;

            var mapping = FindMappingForTarget(rule, targetVar);
            if (mapping == null) continue;

            int packQty = mapping.PackQuantity ?? defaultPackQty;
            if (packQty < 1) packQty = 1;

            int poolStock;
            if (string.Equals(mapping.Strategy, "DynamicSize", StringComparison.OrdinalIgnoreCase))
            {
                // Dynamic: filter source variants by MatchSize (parsed from SKU), sum stock
                if (string.IsNullOrWhiteSpace(mapping.MatchSize))
                {
                    poolStock = 0;
                }
                else
                {
                    poolStock = sourceVariants
                        .Where(v => string.Equals(ParseSizeFromSku(v.SellerSku), mapping.MatchSize, StringComparison.OrdinalIgnoreCase))
                        .Sum(v => v.AvailableQuantity);
                }
            }
            else
            {
                // Explicit: sum stock of SourceMatches (single = Simple Pack, multiple = manual assorted)
                var sourceMatches = mapping.SourceMatches ?? new List<RuleSourceMatchDto>();
                poolStock = 0;
                foreach (var sm in sourceMatches)
                    poolStock += GetSourceStock(sourceVariants, sm);
            }

            int calculatedStock = (int)Math.Floor((double)poolStock / packQty);
            updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), calculatedStock));
        }

        return Task.FromResult(updates);
    }

    /// <summary>Resolve source variant and return AvailableQuantity; 0 if not found (fail-safe).</summary>
    private static int GetSourceStock(List<MeliVariation> sourceVariants, RuleSourceMatchDto sm)
    {
        var idToFind = !string.IsNullOrWhiteSpace(sm.SourceVariantId) ? sm.SourceVariantId : sm.SourceItemId;
        var sourceVar = sourceVariants.FirstOrDefault(s => s.UserProductId == idToFind);
        if (sourceVar == null && !string.IsNullOrWhiteSpace(sm.SourceSku))
            sourceVar = sourceVariants.FirstOrDefault(s => string.Equals(s.SellerSku, sm.SourceSku, StringComparison.OrdinalIgnoreCase));
        return sourceVar?.AvailableQuantity ?? 0;
    }

    private static VariantMappingDto? FindMappingForTarget(StockRuleDto rule, MeliVariation targetVar)
    {
        if (rule.Mappings == null || rule.Mappings.Count == 0) return null;
        var mapping = rule.Mappings.FirstOrDefault(m => m.TargetVariantId == targetVar.UserProductId);
        if (mapping == null && !string.IsNullOrWhiteSpace(targetVar.SellerSku))
            mapping = rule.Mappings.FirstOrDefault(m => string.Equals(m.TargetSku, targetVar.SellerSku, StringComparison.OrdinalIgnoreCase));
        return mapping;
    }
}
