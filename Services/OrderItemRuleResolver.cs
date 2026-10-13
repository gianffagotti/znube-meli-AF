using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

public class OrderItemRuleResolver : IOrderItemRuleResolver
{
    private readonly IStockRuleLookup _stockRuleService;
    private readonly IMeliApiClient _meliApiClient;

    public OrderItemRuleResolver(IStockRuleLookup stockRuleService, IMeliApiClient meliApiClient)
    {
        _stockRuleService = stockRuleService;
        _meliApiClient = meliApiClient;
    }

    public async Task<StockRuleDto?> GetRuleAsync(string itemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        var sellerId = EnvVars.GetString(EnvVars.Keys.MeliSellerId);
        if (string.IsNullOrWhiteSpace(sellerId))
            return null;

        var direct = await _stockRuleService.GetRuleAsync(sellerId, itemId);
        if (direct != null)
            return direct;

        var familyId = await ResolveFamilyIdAsync(itemId, cancellationToken);
        if (string.IsNullOrWhiteSpace(familyId)
            || string.Equals(familyId, itemId, StringComparison.OrdinalIgnoreCase))
            return null;

        return await _stockRuleService.GetRuleAsync(sellerId, familyId);
    }

    private async Task<string?> ResolveFamilyIdAsync(string itemId, CancellationToken cancellationToken)
    {
        var id = itemId.Trim();
        if (MeliItemIdClassifier.IsFamilyId(id))
            return null;

        if (MeliItemIdClassifier.IsUserProductId(id))
        {
            var userProduct = await _meliApiClient.GetUserProductAsync(id.ToUpperInvariant(), cancellationToken);
            return userProduct?.FamilyId;
        }

        if (!MeliItemIdClassifier.TryNormalizeItemId(id, out var mlaId))
            return null;

        var items = await _meliApiClient.GetItemsAsync([mlaId], cancellationToken);
        var item = items?.FirstOrDefault();
        if (item == null)
            return null;
        if (!string.IsNullOrWhiteSpace(item.FamilyId))
            return item.FamilyId;
        if (string.IsNullOrWhiteSpace(item.UserProductId))
            return null;

        var fromUserProduct = await _meliApiClient.GetUserProductAsync(item.UserProductId, cancellationToken);
        return fromUserProduct?.FamilyId;
    }
}
