namespace meli_znube_integration.Core.Domain.Orders;

public sealed class AllocationResult
{
    public required string ResourceName { get; init; }

    public required string Sku { get; init; }

    public int AllocatedQuantity { get; init; }

    public required string Label { get; init; }
}
