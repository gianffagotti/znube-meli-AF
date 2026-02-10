using meli_znube_integration.Models;

namespace meli_znube_integration.Services.Calculators;

/// <summary>
/// Calculates stock for PACK rules. When PackMode is "fixed", only fixed-variant logic is used (mapping with single SourceMatch).
/// When "assorted", only surtido by Size is used. When null, priority: (1) mapping with single SourceMatch, (2) surtido by Size.
/// PackSurtidoGroupBy is persisted for future use (e.g. "Code+Size").
/// </summary>
public class PackStockCalculator : IStockCalculator
{
    private readonly ISkuParser _skuParser;

    public PackStockCalculator(ISkuParser skuParser)
    {
        _skuParser = skuParser;
    }

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

        // Build stock by Size for assorted PACK
        var stockBySize = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in sourceVariants)
        {
            if (string.IsNullOrWhiteSpace(variant.SellerSku)) continue;
            var parts = _skuParser.ParseSku(variant.SellerSku);
            if (!string.IsNullOrWhiteSpace(parts.Size))
            {
                if (!stockBySize.ContainsKey(parts.Size)) stockBySize[parts.Size] = 0;
                stockBySize[parts.Size] += variant.AvailableQuantity;
            }
        }

        int packQty = 1;
        if (rule.Components != null && rule.Components.Count > 0)
            packQty = rule.Components[0].Quantity;
        if (packQty < 1) packQty = 1;

        var targetVariants = targetItem.Variations ?? new List<MeliVariation> { new MeliVariation {
            UserProductId = !string.IsNullOrEmpty(targetItem.UserProductId) ? targetItem.UserProductId : targetItem.Id,
            SellerSku = targetItem.SellerSku
        }};

        foreach (var targetVar in targetVariants)
        {
            var targetSku = targetVar.SellerSku;
            if (string.IsNullOrWhiteSpace(targetSku)) continue;

            var forceAssorted = string.Equals(rule.PackMode, "assorted", StringComparison.OrdinalIgnoreCase);
            var forceFixed = string.Equals(rule.PackMode, "fixed", StringComparison.OrdinalIgnoreCase);

            // Step 1: PACK variante fija — when not forced to assorted, try mapping with exactly one SourceMatch
            if (!forceAssorted)
            {
                var mapping = FindMappingForTarget(rule, targetVar);
                if (mapping != null && mapping.SourceMatches != null && mapping.SourceMatches.Count == 1)
                {
                    var sm = mapping.SourceMatches[0];
                    var idToFind = !string.IsNullOrWhiteSpace(sm.SourceVariantId) ? sm.SourceVariantId : sm.SourceItemId;
                    var sourceVar = sourceVariants.FirstOrDefault(s => s.UserProductId == idToFind);
                    if (sourceVar == null && !string.IsNullOrWhiteSpace(sm.SourceSku))
                        sourceVar = sourceVariants.FirstOrDefault(s => string.Equals(s.SellerSku, sm.SourceSku, StringComparison.OrdinalIgnoreCase));
                    if (sourceVar != null)
                    {
                        int qty = sm.Quantity > 0 ? sm.Quantity : 1;
                        int calculatedStock = (int)Math.Floor((double)sourceVar.AvailableQuantity / qty);
                        updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), calculatedStock));
                        continue;
                    }
                }
                if (forceFixed) continue; // fixed mode but no valid mapping — skip this variant
            }

            // Step 2: PACK surtido — by Size (or exact SKU fallback); skipped when forceFixed and we already continued
            var targetParts = _skuParser.ParseSku(targetSku);
            if (!string.IsNullOrWhiteSpace(targetParts.Size) && stockBySize.TryGetValue(targetParts.Size, out int totalSizeStock))
            {
                int calculatedStock = (int)Math.Floor((double)totalSizeStock / packQty);
                updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), calculatedStock));
            }
            else
            {
                var match = sourceVariants.FirstOrDefault(s => string.Equals(s.SellerSku, targetSku, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    int calculatedStock = (int)Math.Floor((double)match.AvailableQuantity / packQty);
                    updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), calculatedStock));
                }
            }
        }

        return Task.FromResult(updates);
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
