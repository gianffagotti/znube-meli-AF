using meli_znube_integration.Models;
using System.Text.Json;

namespace meli_znube_integration.Services.Calculators;

public class ComboStockCalculator : IStockCalculator
{
    public string RuleType => "COMBO";

    public Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleEntity rule, MeliItem targetItem, List<MeliItem> sourceItems)
    {
        var updates = new List<VariantStockUpdate>();

        // 1. Parse Mapping
        // MappingJson: { "TargetVariantId_or_Sku": "SourceSku1:Qty1,SourceSku2:Qty2" } ?
        // Or maybe MappingJson maps TargetVariant -> List of Components?
        // The prompt says: "Iterate through the Mapping defined in the rule."
        // And "ComponentsJson: Serialized list of components."
        
        // If it's a COMBO, usually the Rule defines the components for the WHOLE item if it's a simple combo.
        // But if the Target has variations (e.g. Combo Red, Combo Blue), each variation needs different components.
        
        // Let's assume MappingJson is `Dictionary<string, List<RuleComponentDto>>` where Key is TargetVariantId (or SKU).
        // OR `Dictionary<string, string>` where Value is some identifier.
        
        // Re-reading prompt: "ComponentsJson: Serialized list of components. Each component has SourceItemId and Quantity."
        // "MappingJson: Serialized logic for variant mapping. e.g., 'Black Shirt L' maps to 'Combo Black L'."
        
        // If MappingJson maps "Source Variant Name" to "Target Variant Name", it's for UI/Auto-mapping.
        // But for calculation, we need to know: For Target Variant X, what are the ingredients?
        
        // If the Rule is "Combo X", and it has Components A and B.
        // Does "Combo X" have variations?
        // If yes, maybe "Combo X Red" needs "Item A Red" + "Item B Red".
        
        // Let's assume a simpler model first:
        // The `ComponentsJson` defines the ingredients for the DEFAULT combo.
        // If `MappingJson` is present, it might override or specify per variant.
        
        // Let's try to parse MappingJson as `Dictionary<string, List<RuleComponentDto>>`.
        // If that fails, we fall back to using `ComponentsJson` for ALL target variants (assuming they all share the same recipe, which is rare for variations, but possible).
        
        // Actually, the prompt says: "Iterate through the Mapping... For each Target Variant, identify the required Source Variants".
        
        // Let's implement a robust mechanism:
        // 1. Flatten Source Variants.
        // 2. Iterate Target Variants.
        // 3. For each Target Variant, determine ingredients.
        //    - If Mapping exists for this variant, use it.
        //    - Else, use global ComponentsJson (but maybe try to match SKU/Color if possible? No, that's too magic).
        
        // Let's assume MappingJson is `Dictionary<string, List<RuleComponentDto>>` keyed by Target SKU or ID.
        
        Dictionary<string, List<RuleComponentDto>>? variantMapping = null;
        try 
        {
            variantMapping = JsonSerializer.Deserialize<Dictionary<string, List<RuleComponentDto>>>(rule.MappingJson);
        }
        catch 
        {
            // Try simpler mapping or ignore
        }

        var globalComponents = JsonSerializer.Deserialize<List<RuleComponentDto>>(rule.ComponentsJson) ?? new List<RuleComponentDto>();

        // Flatten sources
        var sourceVariants = sourceItems
            .SelectMany(i => i.Variations ?? new List<MeliVariation> { new MeliVariation { 
                UserProductId = !string.IsNullOrEmpty(i.UserProductId) ? i.UserProductId : i.Id,
                SellerSku = i.SellerSku, 
                AvailableQuantity = i.AvailableQuantity
            }})
            .ToList();

        var targetVariants = targetItem.Variations ?? new List<MeliVariation> { new MeliVariation { 
            UserProductId = !string.IsNullOrEmpty(targetItem.UserProductId) ? targetItem.UserProductId : targetItem.Id,
            SellerSku = targetItem.SellerSku
        }};

        foreach (var targetVar in targetVariants)
        {
            List<RuleComponentDto> ingredients = globalComponents;
            
            // Try to find specific mapping
            if (variantMapping != null)
            {
                if (variantMapping.ContainsKey(targetVar.Id.ToString())) ingredients = variantMapping[targetVar.Id.ToString()];
                else if (!string.IsNullOrWhiteSpace(targetVar.SellerSku) && variantMapping.ContainsKey(targetVar.SellerSku)) ingredients = variantMapping[targetVar.SellerSku];
            }

            if (ingredients == null || ingredients.Count == 0) continue;

            int maxPossible = int.MaxValue;
            bool canCalculate = true;

            foreach (var ingredient in ingredients)
            {
                // Find source variant
                // Ingredient.SourceItemId might be the ItemID (if simple) or we need to find the specific variant?
                // RuleComponentDto has `SourceItemId`.
                // If Source is a Variation, `SourceItemId` should be the UserProductId or we need another field?
                // The prompt says `SourceItemId` in `RuleComponentDto`.
                // If the source is an Item with variations, `SourceItemId` usually refers to the ItemID, not the variant?
                // OR `SourceItemId` IS the `UserProductId` (Variant ID).
                // Given the context of "Stock Synchronization", we usually sync VARIANTS.
                // So `SourceItemId` in the component list likely refers to the specific Source Variant ID (UserProductId).
                
                var sourceVar = sourceVariants.FirstOrDefault(s => s.UserProductId == ingredient.SourceItemId || s.Id.ToString() == ingredient.SourceItemId);
                
                // Fallback: If not found by ID, try SKU?
                // But ComponentDto doesn't have SKU.
                
                if (sourceVar == null)
                {
                    // Maybe the SourceItemId refers to the PARENT Item and we need to match SKU?
                    // That would be ambiguous.
                    // Let's assume SourceItemId IS the Variant ID (UserProductId).
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
