using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Canonical;

namespace meli_znube_integration.Services.Calculators;

public class FullStockCalculator : IStockCalculator
{
    public string RuleType => StockRuleTypes.Full;

    public Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleDto rule, CanonicalItem targetItem, IReadOnlyList<CanonicalItem> sourceItems)
    {
        var updates = new List<VariantStockUpdate>();

        // Flatten source variants for easy lookup by SKU
        var sourceVariants = sourceItems
            .SelectMany(i => i.Variations)
            .ToList();

        // Iterate Target Variants
        foreach (var targetVar in targetItem.Variations)
        {
            var targetSku = targetVar.SellerSku;
            if (string.IsNullOrWhiteSpace(targetSku)) continue;

            // Find matching source variant by SKU
            var match = sourceVariants.FirstOrDefault(s => string.Equals(s.SellerSku, targetSku, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                updates.Add(new VariantStockUpdate(targetVar.VariantId, match.AvailableQuantity));
            }
        }

        return Task.FromResult(updates);
    }
}
