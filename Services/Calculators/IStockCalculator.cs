using meli_znube_integration.Models;
using meli_znube_integration.Models.Canonical;

namespace meli_znube_integration.Services.Calculators;

public record VariantStockUpdate(string TargetVariantId, int NewQuantity);

public interface IStockCalculator
{
    string RuleType { get; }
    Task<List<VariantStockUpdate>> CalculateStockAsync(StockRuleDto rule, CanonicalItem targetItem, IReadOnlyList<CanonicalItem> sourceItems);
}
