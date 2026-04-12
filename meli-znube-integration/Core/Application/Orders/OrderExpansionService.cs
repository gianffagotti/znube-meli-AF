using meli_znube_integration.Common;
using meli_znube_integration.Core.Domain;
using meli_znube_integration.Core.Domain.Orders;
using meli_znube_integration.Core.Ports;

namespace meli_znube_integration.Core.Application.Orders;

/// <summary>
/// Expands marketplace order lines into <see cref="ResolvedOrderLine"/> using listing rules and Meli listings.
/// </summary>
public sealed class OrderExpansionService : IOrderExpansionService
{
    private readonly IItemRulePort _itemRulePort;
    private readonly IMarketplacePort _marketplacePort;

    public OrderExpansionService(IItemRulePort itemRulePort, IMarketplacePort marketplacePort)
    {
        _itemRulePort = itemRulePort ?? throw new ArgumentNullException(nameof(itemRulePort));
        _marketplacePort = marketplacePort ?? throw new ArgumentNullException(nameof(marketplacePort));
    }

    public async Task<IReadOnlyList<ResolvedOrderLine>> ExpandAsync(Order order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var lines = order.Lines;
        if (lines.Count == 0)
            return Array.Empty<ResolvedOrderLine>();

        var resolved = new List<ResolvedOrderLine>();
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await ExpandLineAsync(line, cancellationToken).ConfigureAwait(false);
            resolved.AddRange(batch);
        }

        return resolved;
    }

    private async Task<IReadOnlyList<ResolvedOrderLine>> ExpandLineAsync(OrderLine line, CancellationToken cancellationToken)
    {
        var qty = line.Quantity <= 0 ? 1 : line.Quantity;
        var itemId = line.MarketplaceItemId?.Trim();
        if (string.IsNullOrEmpty(itemId))
            return Array.Empty<ResolvedOrderLine>();

        var rule = await _itemRulePort.GetRuleForListingAsync(itemId, cancellationToken).ConfigureAwait(false);
        var ruleKind = rule?.RuleKind?.Trim();

        if (rule != null && StockRuleTypes.IsPackOrCombo(ruleKind))
        {
            var targetId = rule.ExternalTargetItemId?.Trim();
            if (string.IsNullOrEmpty(targetId))
            {
                return await ExpandStandardFromListingAsync(line, itemId!, qty, ruleKind, cancellationToken).ConfigureAwait(false);
            }

            var targetListing = await _marketplacePort.GetListingAsync(targetId).ConfigureAwait(false);
            var ruleType = string.Equals(ruleKind, StockRuleTypes.Combo, StringComparison.OrdinalIgnoreCase)
                ? OrderLineRuleType.Combo
                : OrderLineRuleType.Pack;

            var rows = new List<ResolvedOrderLine>();
            foreach (var v in targetListing.Variants)
            {
                var lineQty = checked(qty * Math.Max(1, v.RequiredQuantity));
                rows.Add(new ResolvedOrderLine
                {
                    Sku = v.NormalizedSku,
                    Quantity = lineQty,
                    Label = line.Title,
                    RuleType = ruleType,
                });
            }
            return rows;
        }

        return await ExpandStandardFromListingAsync(line, itemId!, qty, ruleKind, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ResolvedOrderLine>> ExpandStandardFromListingAsync(
        OrderLine line,
        string itemId,
        int qty,
        string? ruleKind,
        CancellationToken cancellationToken)
    {
        var listing = await _marketplacePort.GetListingAsync(itemId).ConfigureAwait(false);
        var sku = PickVariantSku(listing, line.SellerSku);
        var ruleType = ResolveRuleType(listing, ruleKind);
        return new[]
        {
            new ResolvedOrderLine
            {
                Sku = sku,
                Quantity = qty,
                Label = line.Title,
                RuleType = ruleType,
            },
        };
    }

    private static OrderLineRuleType ResolveRuleType(Listing listing, string? ruleKind)
    {
        if (StockRuleTypes.IsFull(ruleKind))
            return OrderLineRuleType.Full;
        if (listing.FulfillmentType == ListingFulfillmentType.Full)
            return OrderLineRuleType.Full;
        return OrderLineRuleType.Standard;
    }

    private static string PickVariantSku(Listing listing, string? sellerSku)
    {
        var variants = listing.Variants.ToList();
        if (variants.Count == 0)
            throw new InvalidOperationException($"Listing '{listing.ExternalId}' has no variants.");

        if (!string.IsNullOrWhiteSpace(sellerSku))
        {
            var match = variants.FirstOrDefault(v =>
                string.Equals(v.NormalizedSku, sellerSku.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match.NormalizedSku;
        }

        if (variants.Count == 1)
            return variants[0].NormalizedSku;

        return variants.OrderBy(v => v.NormalizedSku, StringComparer.Ordinal).First().NormalizedSku;
    }
}
