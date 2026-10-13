using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

public interface IStockRuleLookup
{
    Task<StockRuleDto?> GetRuleAsync(string sellerId, string targetItemId);
}
