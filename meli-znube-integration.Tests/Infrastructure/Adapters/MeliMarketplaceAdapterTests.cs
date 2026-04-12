using FluentAssertions;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Core.Domain;
using meli_znube_integration.Infrastructure.Adapters.MercadoLibre;
using meli_znube_integration.Models;
using Moq;

namespace meli_znube_integration.Tests.Infrastructure.Adapters;

public class MeliMarketplaceAdapterTests
{
    private readonly Mock<IMeliApiClient> _meli = new();

    [Fact]
    public async Task GetListingAsync_maps_standard_when_logistic_type_is_not_fulfillment()
    {
        var itemId = "MLA123";
        var item = new MeliItem
        {
            Id = itemId,
            AvailableQuantity = 12,
            Shipping = new MeliShipping { LogisticType = "xd_drop_off" },
            Attributes =
            [
                new MeliAttribute { Id = MeliConstants.SellerSkuAttributeId, ValueName = "std-sku-01" }
            ],
            Variations = []
        };

        _meli.Setup(c => c.GetItemsAsync(It.Is<IEnumerable<string>>(ids => ids.Single() == itemId), default))
            .ReturnsAsync(new List<MeliItem> { item });

        var listing = await CreateSut().GetListingAsync(itemId);

        listing.ExternalId.Should().Be(itemId);
        listing.FulfillmentType.Should().Be(ListingFulfillmentType.Standard);
        listing.Variants.Should().HaveCount(1);
        listing.Variants.Single().NormalizedSku.Should().Be("STD-SKU-01");
        listing.Variants.Single().CurrentStock.Should().Be(12);
        listing.Variants.Single().RequiredQuantity.Should().Be(1);
    }

    [Fact]
    public async Task GetListingAsync_maps_full_when_logistic_type_is_fulfillment()
    {
        var itemId = "MLA999";
        var item = new MeliItem
        {
            Id = itemId,
            AvailableQuantity = 3,
            Shipping = new MeliShipping { LogisticType = "fulfillment" },
            Attributes =
            [
                new MeliAttribute { Id = MeliConstants.SellerSkuAttributeId, ValueName = "full-sku" }
            ],
            Variations = []
        };

        _meli.Setup(c => c.GetItemsAsync(It.Is<IEnumerable<string>>(ids => ids.Single() == itemId), default))
            .ReturnsAsync(new List<MeliItem> { item });

        var listing = await CreateSut().GetListingAsync(itemId);

        listing.FulfillmentType.Should().Be(ListingFulfillmentType.Full);
        listing.Variants.Should().HaveCount(1);
        listing.Variants.Single().NormalizedSku.Should().Be("FULL-SKU");
        listing.Variants.Single().CurrentStock.Should().Be(3);
    }

    [Fact]
    public async Task GetListingAsync_maps_each_variation_with_distinct_sku_and_stock()
    {
        var itemId = "MLA777";
        var item = new MeliItem
        {
            Id = itemId,
            AvailableQuantity = 0,
            Shipping = new MeliShipping { LogisticType = "fulfillment" },
            Variations =
            [
                new MeliVariation
                {
                    Id = 10,
                    UserProductId = "up-a",
                    AvailableQuantity = 4,
                    Attributes =
                    [
                        new MeliAttribute { Id = MeliConstants.SellerSkuAttributeId, ValueName = "var-a" }
                    ]
                },
                new MeliVariation
                {
                    Id = 11,
                    UserProductId = "up-b",
                    AvailableQuantity = 7,
                    Attributes =
                    [
                        new MeliAttribute { Id = MeliConstants.SellerSkuAttributeId, ValueName = "var-b" }
                    ]
                }
            ]
        };

        _meli.Setup(c => c.GetItemsAsync(It.Is<IEnumerable<string>>(ids => ids.Single() == itemId), default))
            .ReturnsAsync(new List<MeliItem> { item });

        var listing = await CreateSut().GetListingAsync(itemId);

        listing.FulfillmentType.Should().Be(ListingFulfillmentType.Full);
        listing.Variants.Should().HaveCount(2);
        listing.Variants.Select(v => v.NormalizedSku).Should().BeEquivalentTo(["VAR-A", "VAR-B"], o => o.WithStrictOrdering());
        listing.Variants.Select(v => v.CurrentStock).Should().BeEquivalentTo([4, 7], o => o.WithStrictOrdering());
        listing.Variants.Should().OnlyContain(v => v.RequiredQuantity == 1);
    }

    [Fact]
    public async Task GetListingAsync_throws_when_item_not_returned()
    {
        _meli.Setup(c => c.GetItemsAsync(It.IsAny<IEnumerable<string>>(), default))
            .ReturnsAsync(new List<MeliItem>());

        var act = async () => await CreateSut().GetListingAsync("MLA404");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MLA404*");
    }

    private MeliMarketplaceAdapter CreateSut() => new(_meli.Object);
}
