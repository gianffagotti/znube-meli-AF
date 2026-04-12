using meli_znube_integration.Core.Domain;

namespace meli_znube_integration.Core.Ports;

public interface IItemRulePort
{
    Task<ListingRuleSnapshot?> GetRuleForListingAsync(string externalItemId, CancellationToken cancellationToken = default);
}
