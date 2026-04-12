using FluentAssertions;
using meli_znube_integration.Core.Domain;

namespace meli_znube_integration.Tests.Core.Domain;

public class ListingTests
{
    [Fact]
    public void Constructor_sets_identifiers_and_fulfillment_type()
    {
        var internalId = Guid.NewGuid();

        var listing = new Listing(internalId, "MLA123456789", ListingFulfillmentType.Pack);

        listing.InternalId.Should().Be(internalId);
        listing.ExternalId.Should().Be("MLA123456789");
        listing.FulfillmentType.Should().Be(ListingFulfillmentType.Pack);
        listing.Variants.Should().BeEmpty();
    }

    [Fact]
    public void AddVariant_appends_to_listing()
    {
        var listing = new Listing(Guid.NewGuid(), "MLA999", ListingFulfillmentType.Standard);
        var v1 = new ProductVariant("SKU-A", requiredQuantity: 2, currentStock: 10);
        var v2 = new ProductVariant("SKU-B", requiredQuantity: 1, currentStock: 5);

        listing.AddVariant(v1);
        listing.AddVariant(v2);

        listing.Variants.Should().HaveCount(2);
        listing.Variants.Should().ContainInOrder(v1, v2);
    }

    [Fact]
    public void Constructor_throws_when_external_id_is_empty()
    {
        var act = () => new Listing(Guid.NewGuid(), "   ", ListingFulfillmentType.Combo);

        act.Should().Throw<ArgumentException>().WithParameterName("externalId");
    }
}
