using meli_znube_integration.Models;

namespace meli_znube_integration.Services.Calculators;

public class FullStockCalculator : IStockCalculator
{
    public string RuleType => "FULL";

    public Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleEntity rule, MeliItem targetItem, List<MeliItem> sourceItems)
    {
        var updates = new List<VariantStockUpdate>();
        
        // Flatten source variants for easy lookup by SKU
        var sourceVariants = sourceItems
            .SelectMany(i => i.Variations ?? new List<MeliVariation> { new MeliVariation { 
                // Id is long, Item.Id is string. Use UserProductId or Item.Id as UPID.
                UserProductId = !string.IsNullOrEmpty(i.UserProductId) ? i.UserProductId : i.Id,
                SellerSku = i.SellerSku, 
                AvailableQuantity = i.AvailableQuantity
            }})
            .ToList();

        // Iterate Target Variants
        var targetVariants = targetItem.Variations ?? new List<MeliVariation> { new MeliVariation { 
            UserProductId = !string.IsNullOrEmpty(targetItem.UserProductId) ? targetItem.UserProductId : targetItem.Id,
            SellerSku = targetItem.SellerSku
        }};

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
