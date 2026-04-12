using meli_znube_integration.Core.Domain.Orders;

namespace meli_znube_integration.Core.Ports;

public interface IInventoryPort
{
    Task<int> GetAvailableStockAsync(string sku);

    /// <summary>Per-warehouse (resource) availability for a SKU, for <see cref="AllocationDomainService"/>.</summary>
    Task<IReadOnlyList<ResourceStock>> GetResourceStocksBySkuAsync(string sku, CancellationToken cancellationToken = default);
}
