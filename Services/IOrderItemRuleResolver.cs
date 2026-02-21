using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

public interface IOrderItemRuleResolver
{
    Task<StockRuleDto?> GetRuleAsync(string itemId, CancellationToken cancellationToken = default);
}
