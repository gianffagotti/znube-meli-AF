using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

/// <summary>
/// Hydrates a catalog (Family model) MeliItem by discovering its sibling items and constructing
/// synthetic MeliVariation objects, so that downstream consumers (MeliItemNormalizer, OrderItemExpander,
/// stock calculators) can treat catalog items identically to Legacy marketplace items.
///
/// Implements the "Hydration-First" pattern: keeps IMeliItemNormalizer.Normalize pure and sync.
/// Only activates for items with catalog_listing == true; returns Legacy items unchanged.
///
/// Spec: meli-catalog-hydrator.
/// Design: §D1 (Hydration-First), §D3 (filter siblings by seller), §D4 (null SKU graceful).
/// </summary>
public interface IMeliCatalogHydrator
{
    /// <summary>
    /// Hydrates a catalog item by fetching its sibling items and populating <see cref="MeliItem.Variations"/>
    /// with synthetic <see cref="MeliVariation"/> objects (one per sibling).
    /// Returns Legacy items unchanged (no HTTP calls made).
    /// </summary>
    /// <param name="item">The item to hydrate. Must not be null.</param>
    /// <param name="sellerId">The seller's numeric ID string (used to filter siblings to this seller's publications).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The (possibly mutated) MeliItem with Variations populated from siblings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="item"/> is null.</exception>
    Task<MeliItem> HydrateAsync(MeliItem item, string sellerId, CancellationToken ct = default);
}
