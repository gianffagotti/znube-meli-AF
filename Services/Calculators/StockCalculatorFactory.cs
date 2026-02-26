using meli_znube_integration.Common;

namespace meli_znube_integration.Services.Calculators;

public class StockCalculatorFactory
{
    private readonly IEnumerable<IStockCalculator> _calculators;

    public StockCalculatorFactory(IEnumerable<IStockCalculator> calculators)
    {
        _calculators = calculators;
    }

    public IStockCalculator GetCalculator(string ruleType)
    {
        return _calculators.FirstOrDefault(c => c.RuleType.Equals(ruleType, StringComparison.OrdinalIgnoreCase))
               ?? _calculators.First(c => c.RuleType == StockRuleTypes.Full); // Default
    }
}
