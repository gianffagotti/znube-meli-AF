using meli_znube_integration.Common;
using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

public class OrderItemRuleResolver : IOrderItemRuleResolver
{
    private readonly StockRuleService _stockRuleService;

    public OrderItemRuleResolver(StockRuleService stockRuleService)
    {
        _stockRuleService = stockRuleService;
    }

    public async Task<StockRuleDto?> GetRuleAsync(string itemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        var sellerId = EnvVars.GetString(EnvVars.Keys.MeliSellerId);
        if (string.IsNullOrWhiteSpace(sellerId))
            return null;

        return await _stockRuleService.GetRuleAsync(sellerId, itemId);
    }
}
