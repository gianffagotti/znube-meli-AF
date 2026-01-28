using meli_znube_integration.Models;

namespace meli_znube_integration.Services.Calculators;

public record VariantStockUpdate(string TargetVariantId, int NewQuantity);

public interface IStockCalculator
{
    string RuleType { get; }
    Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleEntity rule, MeliItem targetItem, List<MeliItem> sourceItems);
}
