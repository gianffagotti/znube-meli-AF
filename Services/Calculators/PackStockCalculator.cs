using meli_znube_integration.Models;

namespace meli_znube_integration.Services.Calculators;

/// <summary>
/// Calculates stock for PACK rules. Spec V2: algorithm is per-variant from mapping topology only.
/// One SourceMatch = Simple: FLOOR(Source.Stock / Pack_Quantity). Multiple SourceMatches = Assorted: Pool_Stock = SUM(sources), FLOOR(Pool_Stock / Pack_Quantity).
/// Pack quantity: mapping.PackQuantity ?? rule.DefaultPackQuantity ?? 1. Missing source → stock 0 (fail-safe, no exception).
/// </summary>
public class PackStockCalculator : IStockCalculator
{
    public string RuleType => "PACK";

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

            var sourceMatches = mapping.SourceMatches ?? new List<RuleSourceMatchDto>();

            // Case B: Simple — single source SKU
            if (sourceMatches.Count == 1)
            {
                int sourceStock = GetSourceStock(sourceVariants, sourceMatches[0]);
                int calculatedStock = (int)Math.Floor((double)sourceStock / packQty);
                updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), calculatedStock));
                continue;
            }

            // Case A: Assorted — pool from multiple source SKUs; or no sources → 0
            if (sourceMatches.Count == 0)
            {
                updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), 0));
                continue;
            }

            int poolStock = 0;
            foreach (var sm in sourceMatches)
                poolStock += GetSourceStock(sourceVariants, sm);

            int assortedStock = (int)Math.Floor((double)poolStock / packQty);
            updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), assortedStock));
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
