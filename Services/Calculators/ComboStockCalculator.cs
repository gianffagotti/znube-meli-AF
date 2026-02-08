using meli_znube_integration.Models;

namespace meli_znube_integration.Services.Calculators;

/// <summary>
/// Calculates stock for COMBO rules. Prefers per-variant Mappings (SourceMatches with SourceVariantId/SourceSku).
/// Fallback: when no mapping exists, Components are used only when each component's SourceItemId (ItemId MLA...)
/// refers to an item with exactly one variation; otherwise the fallback is not applied (multi-variant items require Mappings).
/// </summary>
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

            // Fallback: Global Components only when each component is an item with exactly one variation.
            // RuleComponentDto.SourceItemId is ItemId (MLA...), not UserProductId. Using it as SourceVariantId
            // would fail when the item has multiple variants. So we resolve ItemId → single variant when possible.
            if (!hasSpecificMapping && rule.Components != null && rule.Components.Count > 0)
            {
                var fallbackMatches = new List<RuleSourceMatchDto>();
                bool canUseFallback = true;
                foreach (var c in rule.Components)
                {
                    var sourceItem = sourceItems.FirstOrDefault(s => string.Equals(s.Id, c.SourceItemId, StringComparison.OrdinalIgnoreCase));
                    if (sourceItem == null)
                    {
                        canUseFallback = false;
                        break;
                    }
                    var variations = sourceItem.Variations ?? new List<MeliVariation>();
                    if (variations.Count != 1)
                    {
                        // Item has 0 or multiple variants: cannot use Components fallback; Mappings required.
                        canUseFallback = false;
                        break;
                    }
                    var singleVar = variations[0];
                    fallbackMatches.Add(new RuleSourceMatchDto
                    {
                        SourceItemId = sourceItem.Id ?? c.SourceItemId,
                        SourceVariantId = !string.IsNullOrWhiteSpace(singleVar.UserProductId) ? singleVar.UserProductId : singleVar.Id.ToString(),
                        Quantity = c.Quantity
                    });
                }
                if (canUseFallback && fallbackMatches.Count > 0)
                    neededIngredientsSourceMatches = fallbackMatches;
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
