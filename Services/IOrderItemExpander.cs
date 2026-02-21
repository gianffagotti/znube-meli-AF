using meli_znube_integration.Clients;
using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

public interface IOrderItemExpander
{
    Task<List<OrderItemResolved>?> ExpandItemsAsync(IEnumerable<MeliOrderItem> items, CancellationToken cancellationToken = default);
}
