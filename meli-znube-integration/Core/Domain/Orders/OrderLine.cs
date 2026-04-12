namespace meli_znube_integration.Core.Domain.Orders;

/// <summary>
/// One marketplace line on an <see cref="Order"/> (Meli item + quantity) used before expansion to <see cref="ResolvedOrderLine"/>.
/// </summary>
public sealed class OrderLine
{
    public required string MarketplaceItemId { get; init; }

    public int Quantity { get; init; }

    public required string Title { get; init; }

    public string? SellerSku { get; init; }
}
