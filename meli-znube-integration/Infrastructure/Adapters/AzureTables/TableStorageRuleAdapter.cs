using meli_znube_integration.Common;
using meli_znube_integration.Core.Domain;
using meli_znube_integration.Core.Ports;
using meli_znube_integration.Models;
using meli_znube_integration.Services;

namespace meli_znube_integration.Infrastructure.Adapters.AzureTables;

/// <summary>
/// Maps persisted stock rules (Azure Tables via <see cref="StockRuleService"/>) into <see cref="ListingRuleSnapshot"/>.
/// </summary>
public sealed class TableStorageRuleAdapter : IItemRulePort
{
    private readonly StockRuleService _stockRuleService;

    public TableStorageRuleAdapter(StockRuleService stockRuleService)
    {
        _stockRuleService = stockRuleService ?? throw new ArgumentNullException(nameof(stockRuleService));
    }

    public async Task<ListingRuleSnapshot?> GetRuleForListingAsync(string externalItemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalItemId))
            return null;

        var sellerId = EnvVars.GetString(EnvVars.Keys.MeliSellerId);
        if (string.IsNullOrWhiteSpace(sellerId))
            return null;

        var rule = await _stockRuleService.GetRuleAsync(sellerId, externalItemId.Trim()).ConfigureAwait(false);
        if (rule == null)
            return null;

        return new ListingRuleSnapshot
        {
            ExternalTargetItemId = rule.TargetItemId,
            RuleKind = rule.RuleType,
        };
    }
}
