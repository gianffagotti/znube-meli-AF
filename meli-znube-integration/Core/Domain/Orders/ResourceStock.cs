namespace meli_znube_integration.Core.Domain.Orders;

public sealed class ResourceStock
{
    public required string ResourceName { get; init; }

    public int AvailableQuantity { get; init; }
}
