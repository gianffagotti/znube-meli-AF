namespace meli_znube_integration.Core.Domain.Orders;

public interface IAllocationDomainService
{
    List<AllocationResult> Allocate(
        List<ResolvedOrderLine> items,
        Dictionary<string, List<ResourceStock>> stockPerSku);
}
