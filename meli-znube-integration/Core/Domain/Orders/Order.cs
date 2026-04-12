namespace meli_znube_integration.Core.Domain.Orders;

public sealed class Order
{
    public required string Id { get; init; }

    public DateTime? DateCreatedUtc { get; init; }

    public string? PackId { get; init; }

    public ShipmentType ShipmentType { get; init; }

    public string? DestinationZone { get; init; }

    public string? BuyerNickname { get; init; }

    /// <summary>Mercado Libre order lines (items). Empty when not yet populated by the order port.</summary>
    public IReadOnlyList<OrderLine> Lines { get; init; } = Array.Empty<OrderLine>();
}
