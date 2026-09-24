using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

/// <summary>
/// Implements the "Hydration-First" pattern for MELI catalog (Family model) items.
///
/// Algorithm per item (only for catalog items where CatalogListing == true):
///   1. Search siblings: GET /users/{sellerId}/items/search?catalog_product_id={id}
///   2. Bulk-fetch sibling details.
///   3. Per sibling, resolve SKU via item_relations → original marketplace item → variation → SELLER_SKU.
///   4. Build synthetic MeliVariation for each sibling.
///   5. Assign to item.Variations.
///
/// Design decisions: §D1 (hydration-first), §D2 (reuse MeliVariation), §D3 (filter by seller),
/// §D4 (null SKU graceful — never abort). Spec: meli-catalog-hydrator.
/// </summary>
public sealed class MeliCatalogHydrator : IMeliCatalogHydrator
{
    private readonly IMeliApiClient _meliApiClient;
    private readonly ILogger<MeliCatalogHydrator> _logger;

    public MeliCatalogHydrator(IMeliApiClient meliApiClient, ILogger<MeliCatalogHydrator> logger)
    {
        _meliApiClient = meliApiClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MeliItem> HydrateAsync(MeliItem item, string sellerId, CancellationToken ct = default)
    {
        // Task 3.8 — null guard
        ArgumentNullException.ThrowIfNull(item);

        // Task 3.2 — guard: not a catalog item → return unchanged (no I/O)
        if (!item.CatalogListing || string.IsNullOrWhiteSpace(item.CatalogProductId))
            return item;

        _logger.LogDebug("Hydrating catalog item {ItemId} (CatalogProductId={CatalogProductId}).",
            item.Id, item.CatalogProductId);

        // Task 3.3 — search siblings by catalog_product_id for this seller
        if (!long.TryParse(sellerId, out var sellerIdLong))
        {
            _logger.LogWarning("Invalid sellerId '{SellerId}' for catalog hydration of item {ItemId}.", sellerId, item.Id);
            item.Variations = [];
            return item;
        }

        MeliSearchResponseDto? searchResult = null;
        try
        {
            searchResult = await _meliApiClient.SearchItemsAsync(
                sellerIdLong,
                new MeliItemSearchQuery { CatalogProductId = item.CatalogProductId, Status = "active" },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search siblings for catalog item {ItemId}. Returning empty variations.", item.Id);
            item.Variations = [];
            return item;
        }

        var siblingIds = searchResult?.Results?
            .Select(r => r.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList() ?? [];

        // Include the item itself if missing from the search results
        if (!siblingIds.Any(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)))
            siblingIds.Add(item.Id);

        if (siblingIds.Count == 0)
        {
            item.Variations = [];
            return item;
        }

        // Task 3.4 — bulk-fetch sibling details
        List<MeliItem> siblings;
        try
        {
            siblings = await _meliApiClient.GetItemsAsync(siblingIds, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch siblings for catalog item {ItemId}. Returning empty variations.", item.Id);
            item.Variations = [];
            return item;
        }

        if (siblings.Count == 0)
        {
            item.Variations = [];
            return item;
        }

        // Task 3.5 — resolve SKU per sibling via item_relations
        // Cache original marketplace items to avoid repeated fetches for the same source item.
        var originalItemCache = new Dictionary<string, MeliItem?>(StringComparer.OrdinalIgnoreCase);

        // Task 3.6 + 3.7 — build synthetic MeliVariation list and assign
        var syntheticVariations = new List<MeliVariation>(siblings.Count);

        foreach (var sibling in siblings)
        {
            var relation = sibling.ItemRelations?.FirstOrDefault();
            string? resolvedSku = null;
            long variationId = 0L;

            if (relation != null && relation.VariationId.HasValue)
            {
                variationId = relation.VariationId.Value;
                var originalItemId = relation.Id;

                if (!string.IsNullOrWhiteSpace(originalItemId))
                {
                    if (!originalItemCache.TryGetValue(originalItemId, out var originalItem))
                    {
                        try
                        {
                            var fetched = await _meliApiClient.GetItemsAsync([originalItemId], ct);
                            originalItem = fetched?.FirstOrDefault();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch original item {OriginalId} for catalog sibling {SiblingId}. SKU will be null.", originalItemId, sibling.Id);
                            originalItem = null;
                        }
                        originalItemCache[originalItemId] = originalItem;
                    }

                    if (originalItem != null)
                    {
                        var originalVariation = originalItem.Variations
                            .FirstOrDefault(v => v.Id == variationId);
                        resolvedSku = originalVariation?.SellerSku; // computed property reads SELLER_SKU attribute
                    }
                }
            }
            else
            {
                // Task 3.5 / D4 — auto-optin items may have no item_relations → SKU = null, do not abort
                _logger.LogDebug("Sibling {SiblingId} has no item_relations or VariationId. SellerSku will be null.", sibling.Id);
            }

            // Build synthetic MeliVariation — Branch A of MeliItemNormalizer handles this transparently.
            syntheticVariations.Add(new MeliVariation
            {
                Id = variationId,
                UserProductId = sibling.UserProductId ?? sibling.Id,
                Attributes =
                [
                    new MeliAttribute
                    {
                        Id = MeliConstants.SellerSkuAttributeId,   // "SELLER_SKU"
                        ValueName = resolvedSku
                    }
                ],
                AvailableQuantity = 0   // overwritten by StockSyncSourceService with Znube data
            });
        }

        item.Variations = syntheticVariations;

        _logger.LogDebug("Hydrated catalog item {ItemId}: {Count} synthetic variation(s).", item.Id, syntheticVariations.Count);
        return item;
    }
}
