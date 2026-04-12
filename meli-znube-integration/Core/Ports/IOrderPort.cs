using meli_znube_integration.Core.Domain.Orders;

namespace meli_znube_integration.Core.Ports;

public interface IOrderPort
{
    Task<Order?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Order>> GetPackOrdersAsync(string packId, CancellationToken cancellationToken = default);

    Task<bool> HasNotesAsync(string orderId, CancellationToken cancellationToken = default);

    Task<bool> HasOtherOrdersFromBuyerIn24hAsync(Order referenceOrder, CancellationToken cancellationToken = default);
}
