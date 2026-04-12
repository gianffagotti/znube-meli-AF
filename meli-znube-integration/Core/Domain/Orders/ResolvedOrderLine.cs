namespace meli_znube_integration.Core.Domain.Orders;

public sealed class ResolvedOrderLine
{
    public required string Sku { get; init; }

    public int Quantity { get; init; }

    public required string Label { get; init; }

    public OrderLineRuleType RuleType { get; init; }
}
