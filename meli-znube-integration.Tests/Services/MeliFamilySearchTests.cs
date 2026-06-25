using FluentAssertions;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace meli_znube_integration.Tests.Services;

/// <summary>Tests for SearchByFamilyIdAsync — direct family-id search routing and base-shell construction.</summary>
public class MeliFamilySearchTests
{
    private readonly Mock<IMeliApiClient> _meli = new();

    public MeliFamilySearchTests()
    {
        Environment.SetEnvironmentVariable("MELI_SELLER_ID", "123");
    }

    private MeliFamilyItemResolver CreateSut() =>
        new(_meli.Object, NullLogger<MeliFamilyItemResolver>.Instance);

    private MeliItem Sibling(string mlau, string sku, string logisticType, string status = "active") =>
        new()
        {
            Id = "MLA_" + mlau,
            Title = "Title_" + mlau,
            Thumbnail = "thumb_" + mlau,
            UserProductId = mlau,
            Status = status,
            Shipping = new MeliShipping { LogisticType = logisticType },
            Attributes = [new MeliAttribute { Id = MeliConstants.SellerSkuAttributeId, ValueName = sku }]
        };

    private void SetupSibling(string mlau, MeliItem? item)
    {
        var resultId = item?.Id;
        _meli.Setup(c => c.SearchItemsAsync(
                It.IsAny<long>(),
                It.Is<MeliItemSearchQuery>(q => q.UserProductId == mlau),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(item == null
                ? new MeliSearchResponseDto { Results = new() }
                : new MeliSearchResponseDto { Results = [new MeliSearchResultDto { Id = resultId }] });

        if (item != null)
        {
            _meli.Setup(c => c.GetItemsAsync(
                    It.Is<IEnumerable<string>>(ids => ids.Contains(resultId)),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<MeliItem> { item });
        }
    }

    private void SetupFamily(string familyId, params string[] mlauIds)
    {
        _meli.Setup(c => c.GetUserProductsFamilyAsync(familyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeliUserProductsFamilyDto { UserProductsIds = mlauIds.ToList() });
        _meli.Setup(c => c.ResolveUserProductsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MeliUserProductDto>());
    }

    // --- Routing: family id branch ---

    [Fact]
    public void FamilyIdMinDigits_is_10()
    {
        MeliFamilyItemResolver.FamilyIdMinDigits.Should().Be(10);
    }

    // --- Happy path ---

    [Fact]
    public async Task Two_active_fullFlex_siblings_returns_shell_with_two_variations()
    {
        SetupFamily("FAM1", "MLAU1", "MLAU2");
        SetupSibling("MLAU1", Sibling("MLAU1", "S1", "fulfillment"));
        SetupSibling("MLAU2", Sibling("MLAU2", "S2", "self_service"));

        var result = await CreateSut().SearchByFamilyIdAsync("FAM1");

        result.Should().NotBeNull();
        result!.Variations.Should().HaveCount(2);
        result.Variations.Select(v => v.UserProductId).Should().ContainInOrder("MLAU1", "MLAU2");
    }

    [Fact]
    public async Task First_active_sibling_becomes_base_shell_id_title_thumbnail()
    {
        SetupFamily("FAM1", "MLAU1", "MLAU2");
        SetupSibling("MLAU1", Sibling("MLAU1", "S1", "fulfillment"));
        SetupSibling("MLAU2", Sibling("MLAU2", "S2", "fulfillment"));

        var result = await CreateSut().SearchByFamilyIdAsync("FAM1");

        result!.Id.Should().Be("MLA_MLAU1");
        result.Title.Should().Be("Title_MLAU1");
        result.Thumbnail.Should().Be("thumb_MLAU1");
    }

    [Fact]
    public async Task All_active_siblings_appear_as_variations_including_base()
    {
        SetupFamily("FAM1", "MLAU1", "MLAU2", "MLAU3");
        SetupSibling("MLAU1", Sibling("MLAU1", "S1", "fulfillment"));
        SetupSibling("MLAU2", Sibling("MLAU2", "S2", "fulfillment"));
        SetupSibling("MLAU3", Sibling("MLAU3", "S3", "self_service"));

        var result = await CreateSut().SearchByFamilyIdAsync("FAM1");

        result!.Variations.Should().HaveCount(3);
    }

    // --- Inactive sibling exclusion ---

    [Fact]
    public async Task Inactive_sibling_is_excluded_from_variations()
    {
        SetupFamily("FAM1", "MLAU1", "MLAU2");
        SetupSibling("MLAU1", Sibling("MLAU1", "S1", "fulfillment", "paused"));
        SetupSibling("MLAU2", Sibling("MLAU2", "S2", "fulfillment", "active"));

        var result = await CreateSut().SearchByFamilyIdAsync("FAM1");

        result!.Variations.Should().HaveCount(1);
        result.Variations[0].UserProductId.Should().Be("MLAU2");
    }

    [Fact]
    public async Task Closed_sibling_is_excluded_from_variations()
    {
        SetupFamily("FAM1", "MLAU1", "MLAU2");
        SetupSibling("MLAU1", Sibling("MLAU1", "S1", "fulfillment", "closed"));
        SetupSibling("MLAU2", Sibling("MLAU2", "S2", "fulfillment", "active"));

        var result = await CreateSut().SearchByFamilyIdAsync("FAM1");

        result!.Variations.Should().HaveCount(1);
        result.Variations[0].UserProductId.Should().Be("MLAU2");
    }

    // --- Non FULL+FLEX sibling exclusion ---

    [Fact]
    public async Task Non_fullFlex_sibling_is_excluded()
    {
        SetupFamily("FAM1", "MLAU1", "MLAU2");
        SetupSibling("MLAU1", Sibling("MLAU1", "S1", "drop_off"));
        SetupSibling("MLAU2", Sibling("MLAU2", "S2", "fulfillment"));

        var result = await CreateSut().SearchByFamilyIdAsync("FAM1");

        result!.Variations.Should().HaveCount(1);
        result.Variations[0].UserProductId.Should().Be("MLAU2");
    }

    // --- No eligible siblings ---

    [Fact]
    public async Task All_siblings_inactive_returns_null()
    {
        SetupFamily("FAM1", "MLAU1");
        SetupSibling("MLAU1", Sibling("MLAU1", "S1", "fulfillment", "paused"));

        var result = await CreateSut().SearchByFamilyIdAsync("FAM1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Empty_family_returns_null()
    {
        _meli.Setup(c => c.GetUserProductsFamilyAsync("FAM_EMPTY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeliUserProductsFamilyDto { UserProductsIds = new() });
        _meli.Setup(c => c.ResolveUserProductsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MeliUserProductDto>());

        var result = await CreateSut().SearchByFamilyIdAsync("FAM_EMPTY");

        result.Should().BeNull();
    }

    // --- Graceful degradation ---

    [Fact]
    public async Task Family_endpoint_failure_returns_null_without_throwing()
    {
        _meli.Setup(c => c.GetUserProductsFamilyAsync("BAD", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var result = await CreateSut().SearchByFamilyIdAsync("BAD");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Blank_family_id_returns_null_without_http()
    {
        var result = await CreateSut().SearchByFamilyIdAsync("  ");

        result.Should().BeNull();
        _meli.Verify(c => c.GetUserProductsFamilyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
