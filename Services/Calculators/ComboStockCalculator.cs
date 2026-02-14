using meli_znube_integration.Models;

namespace meli_znube_integration.Services.Calculators;

/// <summary>
/// Calculates stock for COMBO rules. Spec V2: Limiting reagent (MIN of FLOOR(Stock_i / Qty_i)).
/// Fail-safe: if a component is missing (not in sourceItems), treat as stock 0 and still emit VariantStockUpdate with quantity 0 (do not skip variant).
/// </summary>
public class ComboStockCalculator : IStockCalculator
{
    public string RuleType => "COMBO";

    public Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleDto rule, MeliItem targetItem, List<MeliItem> sourceItems)
    {
        var updates = new List<VariantStockUpdate>();

        var sourceVariants = sourceItems
            .SelectMany(i => i.Variations ?? new List<MeliVariation> {
                new MeliVariation {
                    UserProductId = !string.IsNullOrEmpty(i.UserProductId) ? i.UserProductId : i.Id,
                    SellerSku = i.SellerSku,
                    AvailableQuantity = i.AvailableQuantity,
                    Id = !string.IsNullOrEmpty(i.Id) && long.TryParse(i.Id.Replace("MLA", ""), out long pid) ? pid : 0
                }
            })
            .ToList();

        var targetVariants = targetItem.Variations ?? new List<MeliVariation> {
            new MeliVariation {
                UserProductId = !string.IsNullOrEmpty(targetItem.UserProductId) ? targetItem.UserProductId : targetItem.Id,
                SellerSku = targetItem.SellerSku,
                Id = !string.IsNullOrEmpty(targetItem.Id) && long.TryParse(targetItem.Id.Replace("MLA", ""), out long tid) ? tid : 0
            }
        };

        foreach (var targetVar in targetVariants)
        {
            List<RuleSourceMatchDto>? neededIngredientsSourceMatches = null;
            bool hasSpecificMapping = false;

            if (rule.Mappings != null && rule.Mappings.Count > 0)
            {
                var mapping = rule.Mappings.FirstOrDefault(m => m.TargetVariantId == targetVar.UserProductId);
                if (mapping == null && !string.IsNullOrWhiteSpace(targetVar.SellerSku))
                    mapping = rule.Mappings.FirstOrDefault(m => string.Equals(m.TargetSku, targetVar.SellerSku, StringComparison.OrdinalIgnoreCase));

                if (mapping != null)
                {
                    neededIngredientsSourceMatches = mapping.SourceMatches;
                    hasSpecificMapping = true;
                }
            }

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
                        canUseFallback = false;
                        break;
                    }
                    var singleVar = variations[0];
                    fallbackMatches.Add(new RuleSourceMatchDto
                    {
                        SourceItemId = sourceItem.Id ?? c.SourceItemId,
                        SourceVariantId = !string.IsNullOrWhiteSpace(singleVar.UserProductId) ? singleVar.UserProductId : singleVar.Id.ToString(),
                        SourceSku = singleVar.SellerSku ?? "",
                        Quantity = c.Quantity
                    });
                }
                if (canUseFallback && fallbackMatches.Count > 0)
                    neededIngredientsSourceMatches = fallbackMatches;
            }

            if (neededIngredientsSourceMatches == null || neededIngredientsSourceMatches.Count == 0) continue;

            // Limiting reagent: maxPossible = MIN( FLOOR(Stock_i / Qty_i) ). Missing component → use 0 (fail-safe), still emit update.
            int maxPossible = int.MaxValue;

            foreach (var ingredient in neededIngredientsSourceMatches)
            {
                var idToFind = !string.IsNullOrWhiteSpace(ingredient.SourceVariantId) ? ingredient.SourceVariantId : ingredient.SourceItemId;
                var sourceVar = sourceVariants.FirstOrDefault(s => s.UserProductId == idToFind);
                if (sourceVar == null && !string.IsNullOrWhiteSpace(ingredient.SourceSku))
                    sourceVar = sourceVariants.FirstOrDefault(s => string.Equals(s.SellerSku, ingredient.SourceSku, StringComparison.OrdinalIgnoreCase));

                // Fail-safe: missing component → stock 0 (do not throw, do not skip variant)
                int sourceStock = sourceVar?.AvailableQuantity ?? 0;
                int needed = ingredient.Quantity > 0 ? ingredient.Quantity : 1;
                int possible = (int)Math.Floor((double)sourceStock / needed);

                if (possible < maxPossible) maxPossible = possible;
            }

            if (maxPossible == int.MaxValue) maxPossible = 0;
            updates.Add(new VariantStockUpdate(targetVar.UserProductId ?? targetVar.Id.ToString(), maxPossible));
        }

        return Task.FromResult(updates);
    }
}
