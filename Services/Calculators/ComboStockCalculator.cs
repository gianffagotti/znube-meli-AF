using meli_znube_integration.Models;

namespace meli_znube_integration.Services.Calculators;

public class ComboStockCalculator : IStockCalculator
{
    public string RuleType => "COMBO";

    public Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleDto rule, MeliItem targetItem, List<MeliItem> sourceItems)
    {
        var updates = new List<VariantStockUpdate>();

        // Flatten sources
        var sourceVariants = sourceItems
            .SelectMany(i => i.Variations ?? new List<MeliVariation> { new MeliVariation { 
                UserProductId = !string.IsNullOrEmpty(i.UserProductId) ? i.UserProductId : i.Id,
                SellerSku = i.SellerSku, 
                AvailableQuantity = i.AvailableQuantity,
                Id = !string.IsNullOrEmpty(i.Id) && long.TryParse(i.Id.Replace("MLA", ""), out long pid) ? pid : 0 // Fallback ID extraction if needed
            }})
            .ToList();

        // Target Variants
        var targetVariants = targetItem.Variations ?? new List<MeliVariation> { new MeliVariation { 
            UserProductId = !string.IsNullOrEmpty(targetItem.UserProductId) ? targetItem.UserProductId : targetItem.Id,
            SellerSku = targetItem.SellerSku,
            Id = !string.IsNullOrEmpty(targetItem.Id) && long.TryParse(targetItem.Id.Replace("MLA", ""), out long pid) ? pid : 0
        }};

        foreach (var targetVar in targetVariants)
        {
            // 1. Determine Ingredients for this Target Variant
            List<RuleSourceMatchDto> neededIngredientsSourceMatches = new();
            bool hasSpecificMapping = false;

            // Try to find specific mapping in rule.Mappings
            if (rule.Mappings != null && rule.Mappings.Count > 0)
            {
                // Match by TargetVariantId (UserProductId)
                var mapping = rule.Mappings.FirstOrDefault(m => m.TargetVariantId == targetVar.UserProductId);
                
                // Fallback: Match by SKU
                if (mapping == null && !string.IsNullOrWhiteSpace(targetVar.SellerSku))
                {
                    mapping = rule.Mappings.FirstOrDefault(m => string.Equals(m.TargetSku, targetVar.SellerSku, StringComparison.OrdinalIgnoreCase));
                }

                if (mapping != null)
                {
                    neededIngredientsSourceMatches = mapping.SourceMatches;
                    hasSpecificMapping = true;
                }
            }

            // Fallback: Global Components if no specific mapping found (or if mapping list is empty/null which implies global rule)
            if (!hasSpecificMapping)
            {
                // Convert Global Components to SourceMatches for uniform processing
                // Note: RuleComponentDto has SourceItemId and Quantity.
                // We assume RuleComponentDto.SourceItemId is the VariantId (UserProductId) in this context.
                if (rule.Components != null)
                {
                    neededIngredientsSourceMatches = rule.Components.Select(c => new RuleSourceMatchDto
                    {
                        SourceVariantId = c.SourceItemId, // Assuming SourceItemId is the ID we look for
                        Quantity = c.Quantity
                    }).ToList();
                }
            }

            if (neededIngredientsSourceMatches == null || neededIngredientsSourceMatches.Count == 0) continue;

            // 2. Calculate Max Possible Sets
            int maxPossible = int.MaxValue;
            bool canCalculate = true;

            foreach (var ingredient in neededIngredientsSourceMatches)
            {
                // Look for source variant
                // We prefer SourceVariantId (UserProductId)
                var idToFind = ingredient.SourceVariantId;
                if (string.IsNullOrWhiteSpace(idToFind)) idToFind = ingredient.SourceItemId; // Fallback

                var sourceVar = sourceVariants.FirstOrDefault(s => s.UserProductId == idToFind);

                // Fallback by SKU if ID not found and SKU provided
                if (sourceVar == null && !string.IsNullOrWhiteSpace(ingredient.SourceSku))
                {
                    sourceVar = sourceVariants.FirstOrDefault(s => string.Equals(s.SellerSku, ingredient.SourceSku, StringComparison.OrdinalIgnoreCase));
                }

                if (sourceVar == null)
                {
                    canCalculate = false;
                    break;
                }

                int needed = ingredient.Quantity > 0 ? ingredient.Quantity : 1;
                int possible = (int)Math.Floor((double)sourceVar.AvailableQuantity / needed);
                
                if (possible < maxPossible) maxPossible = possible;
            }

            if (canCalculate)
            {
                if (maxPossible == int.MaxValue) maxPossible = 0;
                updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), maxPossible));
            }
        }

        return Task.FromResult(updates);
    }
}
