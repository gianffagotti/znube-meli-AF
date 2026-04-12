using meli_znube_integration.Core.Domain.Orders;

namespace meli_znube_integration.Core.Application.Orders;

public interface INoteFormatter
{
    string? Format(
        IReadOnlyList<AllocationResult>? allocations,
        string? destinationZone,
        bool addToc,
        bool hasPack,
        bool hasCombo);
}
