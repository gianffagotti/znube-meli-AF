using FluentAssertions;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace meli_znube_integration.Tests.Services;

public class MeliFamilyItemResolverTests
{
    private readonly Mock<IMeliApiClient> _meli = new();

    public MeliFamilyItemResolverTests()
    {
        Environment.SetEnvironmentVariable("MELI_SELLER_ID", "123");
    }

    private MeliFamilyItemResolver CreateSut() =>
        new(_meli.Object, NullLogger<MeliFamilyItemResolver>.Instance);

    private static MeliItem SiblingItem(string mlau, string sku, string logisticType)
    {
        return new MeliItem
        {
            Id = "MLA_" + mlau,
            UserProductId = mlau,
            Shipping = new MeliShipping { LogisticType = logisticType },
            Attributes =
            [
                new MeliAttribute { Id = MeliConstants.SellerSkuAttributeId, ValueName = sku }
            ]
        };
    }

    private void SetupSibling(string mlau, MeliItem? siblingItem)
    {
        _meli.Setup(c => c.SearchItemsAsync(
                It.IsAny<long>(),
                It.Is<MeliItemSearchQuery>(q => q.UserProductId == mlau),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(siblingItem == null
                ? new MeliSearchResponseDto { Results = new() }
                : new MeliSearchResponseDto { Results = new() { new MeliSearchResultDto { Id = siblingItem.Id } } });

        if (siblingItem != null)
        {
            _meli.Setup(c => c.GetItemsAsync(
                    It.Is<IEnumerable<string>>(ids => ids.Contains(siblingItem.Id)),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<MeliItem> { siblingItem });
        }
    }

    [Fact]
    public async Task Classic_item_with_variations_passes_through_untouched()
    {
        var item = new MeliItem
        {
            Id = "MLA1",
            Variations = [new MeliVariation { UserProductId = "MLAU_X" }]
        };

        var result = await CreateSut().NormalizeAsync(item);

        result.Variations.Should().HaveCount(1);
        _meli.Verify(c => c.GetUserProductsFamilyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Family_item_is_expanded_into_variations_with_mlau_and_sku()
    {
        var item = new MeliItem { Id = "MLA1", FamilyId = "FAM1", Variations = new() };
        _meli.Setup(c => c.GetUserProductsFamilyAsync("FAM1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeliUserProductsFamilyDto { UserProductsIds = new() { "MLAU1", "MLAU2" } });
        _meli.Setup(c => c.ResolveUserProductsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MeliUserProductDto>());
        SetupSibling("MLAU1", SiblingItem("MLAU1", "SKU1", "fulfillment"));
        SetupSibling("MLAU2", SiblingItem("MLAU2", "SKU2", "self_service"));

        var result = await CreateSut().NormalizeAsync(item);

        result.Variations.Should().HaveCount(2);
        result.Variations.Select(v => v.UserProductId).Should().ContainInOrder("MLAU1", "MLAU2");
        result.Variations.Single(v => v.UserProductId == "MLAU1").SellerSku.Should().Be("SKU1");
        result.Variations.Single(v => v.UserProductId == "MLAU2").SellerSku.Should().Be("SKU2");
    }

    [Fact]
    public async Task Item_with_neither_variations_nor_family_stays_empty()
    {
        var item = new MeliItem { Id = "MLA1", Variations = new() };

        var result = await CreateSut().NormalizeAsync(item);

        result.Variations.Should().BeEmpty();
        _meli.Verify(c => c.GetUserProductsFamilyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Family_expansion_failure_degrades_gracefully()
    {
        var item = new MeliItem { Id = "MLA1", FamilyId = "FAM1", Variations = new() };
        _meli.Setup(c => c.GetUserProductsFamilyAsync("FAM1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var result = await CreateSut().NormalizeAsync(item);

        result.Should().BeSameAs(item);
        result.Variations.Should().BeEmpty();
    }

    [Fact]
    public async Task Non_full_or_flex_sibling_is_excluded_from_expansion()
    {
        var item = new MeliItem { Id = "MLA1", FamilyId = "FAM1", Variations = new() };
        _meli.Setup(c => c.GetUserProductsFamilyAsync("FAM1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeliUserProductsFamilyDto { UserProductsIds = new() { "MLAU1", "MLAU2" } });
        _meli.Setup(c => c.ResolveUserProductsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MeliUserProductDto>());
        SetupSibling("MLAU1", SiblingItem("MLAU1", "SKU1", "drop_off"));     // excluded
        SetupSibling("MLAU2", SiblingItem("MLAU2", "SKU2", "fulfillment"));  // included

        var result = await CreateSut().NormalizeAsync(item);

        result.Variations.Should().HaveCount(1);
        result.Variations[0].UserProductId.Should().Be("MLAU2");
    }

    [Fact]
    public async Task Inactive_sibling_is_excluded_from_expansion_via_normalize()
    {
        var item = new MeliItem { Id = "MLA1", FamilyId = "FAM1", Variations = new() };
        _meli.Setup(c => c.GetUserProductsFamilyAsync("FAM1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeliUserProductsFamilyDto { UserProductsIds = new() { "MLAU1", "MLAU2" } });
        _meli.Setup(c => c.ResolveUserProductsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MeliUserProductDto>());

        var pausedSibling = new MeliItem { Id = "MLA_P", UserProductId = "MLAU1", Status = "paused", Shipping = new MeliShipping { LogisticType = "fulfillment" }, Attributes = [] };
        var activeSibling = SiblingItem("MLAU2", "SKU2", "fulfillment");

        SetupSibling("MLAU1", pausedSibling);   // excluded: paused
        SetupSibling("MLAU2", activeSibling);   // included: active (null status)

        var result = await CreateSut().NormalizeAsync(item);

        result.Variations.Should().HaveCount(1);
        result.Variations[0].UserProductId.Should().Be("MLAU2");
    }
}
