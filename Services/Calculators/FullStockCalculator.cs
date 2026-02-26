using meli_znube_integration.Common;
using meli_znube_integration.Models;

namespace meli_znube_integration.Services.Calculators;

public class FullStockCalculator : IStockCalculator
{
    public string RuleType => StockRuleTypes.Full;

    public Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleDto rule, MeliItem targetItem, List<MeliItem> sourceItems)
    {
        var updates = new List<VariantStockUpdate>();
        
        // Flatten source variants for easy lookup by SKU
        var sourceVariants = sourceItems
            .SelectMany(i => i.Variations)
            .ToList();

        // Iterate Target Variants
        var targetVariants = targetItem.Variations;

        foreach (var targetVar in targetVariants)
        {
            var targetSku = targetVar.SellerSku;
            if (string.IsNullOrWhiteSpace(targetSku)) continue;

            // Find matching source
            var match = sourceVariants.FirstOrDefault(s => string.Equals(s.SellerSku, targetSku, StringComparison.OrdinalIgnoreCase));
            
            if (match != null)
            {
                updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), match.AvailableQuantity));
            }
        }

        return Task.FromResult(updates);
    }
}
