using meli_znube_integration.Models;
using System.Text.Json;

namespace meli_znube_integration.Services.Calculators;

public class PackStockCalculator : IStockCalculator
{
    private readonly SkuParserService _skuParser;

    public PackStockCalculator(SkuParserService skuParser)
    {
        _skuParser = skuParser;
    }

    public string RuleType => "PACK";

    public Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleDto rule, MeliItem targetItem, List<MeliItem> sourceItems)
    {
        var updates = new List<VariantStockUpdate>();

        // 1. Parse Source Variants and Group by Size (Surtido Logic)
        var sourceVariants = sourceItems
            .SelectMany(i => i.Variations ?? new List<MeliVariation> { new MeliVariation { 
                UserProductId = !string.IsNullOrEmpty(i.UserProductId) ? i.UserProductId : i.Id,
                SellerSku = i.SellerSku, 
                AvailableQuantity = i.AvailableQuantity
            }})
            .ToList();

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

        // 2. Get Pack Quantity
        int packQty = 1;
        if (rule.Components != null && rule.Components.Count > 0)
        {
            packQty = rule.Components[0].Quantity;
        }
        if (packQty < 1) packQty = 1;

        // 3. Iterate Target Variants
        var targetVariants = targetItem.Variations ?? new List<MeliVariation> { new MeliVariation { 
            UserProductId = !string.IsNullOrEmpty(targetItem.UserProductId) ? targetItem.UserProductId : targetItem.Id,
            SellerSku = targetItem.SellerSku
        }};

        foreach (var targetVar in targetVariants)
        {
            var targetSku = targetVar.SellerSku;
            if (string.IsNullOrWhiteSpace(targetSku)) continue;

            var targetParts = _skuParser.ParseSku(targetSku);
            
            // Logic:
            // If Target Variant maps to specific Source Variant -> Direct mapping (not implemented here, assuming Surtido for now as per prompt emphasis).
            // Prompt says: "If Target Variant is 'Surtido' (mapped to a generic concept or identified by SKU pattern) -> Stock = Floor(TotalStockForSize / PackQty)."
            
            // We assume if we can match by Size, we use the Size bucket.
            if (!string.IsNullOrWhiteSpace(targetParts.Size) && stockBySize.TryGetValue(targetParts.Size, out int totalSizeStock))
            {
                int calculatedStock = (int)Math.Floor((double)totalSizeStock / packQty);
                updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), calculatedStock));
            }
            else
            {
                // Fallback: Try exact SKU match if Size logic fails
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
}
