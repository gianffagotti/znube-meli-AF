using meli_znube_integration.Core.Domain.Orders;

namespace meli_znube_integration.Core.Application.Orders;

public interface IOrderExpansionService
{
    Task<IReadOnlyList<ResolvedOrderLine>> ExpandAsync(Order order, CancellationToken cancellationToken = default);
}
