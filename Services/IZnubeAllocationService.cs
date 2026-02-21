using meli_znube_integration.Clients;
using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

public interface IZnubeAllocationService
{
    Task<List<ZnubeAllocationEntry>?> GetAllocationsForOrderAsync(MeliOrder order, CancellationToken cancellationToken = default);
    Task<List<ZnubeAllocationEntry>?> GetAllocationsForOrdersAsync(IEnumerable<MeliOrder> orders, CancellationToken cancellationToken = default);
    Task<List<ZnubeAllocationEntry>> GetAllocationsForSkusAsync(IEnumerable<SkuRequest> requests, CancellationToken cancellationToken = default);
}
