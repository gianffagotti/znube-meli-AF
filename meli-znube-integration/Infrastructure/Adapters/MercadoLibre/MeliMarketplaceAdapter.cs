using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Core.Domain;
using meli_znube_integration.Core.Ports;
using meli_znube_integration.Models;

namespace meli_znube_integration.Infrastructure.Adapters.MercadoLibre;

public sealed class MeliMarketplaceAdapter : IMarketplacePort
{
    private readonly IMeliApiClient _meliApiClient;

    public MeliMarketplaceAdapter(IMeliApiClient meliApiClient)
    {
        _meliApiClient = meliApiClient ?? throw new ArgumentNullException(nameof(meliApiClient));
    }

    public async Task<Listing> GetListingAsync(string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("External id is required.", nameof(externalId));

        var id = externalId.Trim();
        var items = await _meliApiClient.GetItemsAsync(new[] { id }).ConfigureAwait(false);
        var item = items?.FirstOrDefault();
        if (item == null)
            throw new InvalidOperationException($"Mercado Libre item '{id}' was not returned by the API.");

        var fulfillment = MapFulfillmentType(item);
        var listing = new Listing(Guid.NewGuid(), item.Id, fulfillment);

        var variations = item.Variations;
        if (variations != null && variations.Count > 0)
        {
            foreach (var v in variations)
            {
                var sku = StockLocationHelpers.ExtractSku(item, v);
                if (string.IsNullOrWhiteSpace(sku))
                    throw new InvalidOperationException(
                        $"Could not resolve SELLER_SKU for variation {v.Id} on item '{item.Id}'.");

                listing.AddVariant(new ProductVariant(sku, requiredQuantity: 1, currentStock: v.AvailableQuantity));
            }
        }
        else
        {
            var sku = StockLocationHelpers.ExtractSku(item, variation: null);
            if (string.IsNullOrWhiteSpace(sku))
                throw new InvalidOperationException($"Could not resolve SELLER_SKU for item '{item.Id}'.");

            listing.AddVariant(new ProductVariant(sku, requiredQuantity: 1, currentStock: item.AvailableQuantity));
        }

        return listing;
    }

    private static ListingFulfillmentType MapFulfillmentType(MeliItem item)
    {
        // TODO: When item tags are available on the API model without breaking DTO constraints, map Full using
        // Mercado Libre full-shipment tags (e.g. "fulfillment_b2c", "fulfillment") in addition to logistic type.
        // TODO: Classify Pack/Combo via SKU heuristics when product rules are defined.

        var logisticType = item.Shipping?.LogisticType;
        if (!string.IsNullOrWhiteSpace(logisticType)
            && string.Equals(logisticType, "fulfillment", StringComparison.OrdinalIgnoreCase))
            return ListingFulfillmentType.Full;

        return ListingFulfillmentType.Standard;
    }
}
