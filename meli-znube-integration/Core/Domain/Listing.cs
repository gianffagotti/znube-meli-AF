using System.Collections.ObjectModel;

namespace meli_znube_integration.Core.Domain;

public sealed class Listing
{
    private readonly List<ProductVariant> _variants = [];

    public Listing(Guid internalId, string externalId, ListingFulfillmentType fulfillmentType)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("External id is required.", nameof(externalId));

        InternalId = internalId;
        ExternalId = externalId.Trim();
        FulfillmentType = fulfillmentType;
    }

    public Guid InternalId { get; }

    public string ExternalId { get; }

    public ListingFulfillmentType FulfillmentType { get; }

    public IReadOnlyCollection<ProductVariant> Variants => new ReadOnlyCollection<ProductVariant>(_variants);

    public void AddVariant(ProductVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        _variants.Add(variant);
    }
}
