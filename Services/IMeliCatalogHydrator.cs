using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

public interface IMeliCatalogHydrator
{
    /// <summary>
    /// Legacy items keep their id. A User Products item with <see cref="MeliItem.FamilyId"/> becomes
    /// one family container: <c>Id</c> is the family id and <c>Variations</c> are the sibling MLAs.
    /// </summary>
    Task<MeliItem> HydrateAsync(MeliItem item, string sellerId, CancellationToken ct = default);

    /// <summary>
    /// Loads an item by MLA, MLAU or family id and hydrates it. Returns null when the id does not resolve.
    /// </summary>
    Task<MeliItem?> FetchAndHydrateByIdAsync(string itemOrFamilyId, string sellerId, CancellationToken ct = default);
}
