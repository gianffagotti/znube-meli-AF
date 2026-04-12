using meli_znube_integration.Core.Domain;

namespace meli_znube_integration.Core.Ports;

public interface IMarketplacePort
{
    Task<Listing> GetListingAsync(string externalId);
}
