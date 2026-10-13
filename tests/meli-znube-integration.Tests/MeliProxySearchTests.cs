using meli_znube_integration.Common;
using meli_znube_integration.Functions;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace meli_znube_integration.Tests;

public class MeliProxySearchTests : IDisposable
{
    public MeliProxySearchTests()
    {
        Environment.SetEnvironmentVariable(EnvVars.Keys.MeliSellerId, "1234");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVars.Keys.MeliSellerId, null);
    }

    [Fact]
    public async Task UserProductQuery_SearchesByUserProductId()
    {
        var api = new FakeMeliApiClient
        {
            OnSearch = q => q.UserProductId == "MLAU1234"
                ? new MeliSearchResponseDto { Results = [new MeliSearchResultDto { Id = "MLA1" }] }
                : null,
            OnItems = ids => [Item(ids[0])]
        };

        var results = await Search(api, "mlau1234");

        Assert.Equal("MLAU1234", api.Searches.Single().UserProductId);
        Assert.Empty(api.FamilyRequests);
        Assert.Equal("MLA1", Assert.Single(results!).Id);
    }

    [Fact]
    public async Task NumericItemId_NormalizesToMla_AndDoesNotSearchTheFamily()
    {
        var api = new FakeMeliApiClient
        {
            OnItems = ids => ids[0] == "MLA3752456314" ? [Item("MLA3752456314")] : []
        };

        var results = await Search(api, "3752456314");

        Assert.Equal(["MLA3752456314"], api.ItemRequests.Single());
        Assert.Empty(api.Searches);
        Assert.Empty(api.FamilyRequests);
        Assert.Equal("MLA3752456314", results!.Single().Id);
    }

    [Fact]
    public async Task MissingItemId_FallsThroughToSkuAndTextSearch()
    {
        var api = new FakeMeliApiClient
        {
            OnItems = ids => ids.Contains("MLA3752456314") ? [] : [Item("MLA9")],
            OnSearch = q => q.SellerSku != null
                ? new MeliSearchResponseDto { Results = [new MeliSearchResultDto { Id = "MLA9" }] }
                : new MeliSearchResponseDto { Results = [] }
        };

        var results = await Search(api, "3752456314");

        Assert.Contains(api.ItemRequests, ids => ids.SequenceEqual(["MLA3752456314"]));
        Assert.Contains(api.Searches, q => q.SellerSku == "3752456314");
        Assert.Contains(api.Searches, q => q.Query == "3752456314");
        Assert.Equal("MLA9", results!.Single().Id);
    }

    [Fact]
    public async Task FamilyId_UsesUserProductsFamily_NotCatalogProductId()
    {
        var api = new FakeMeliApiClient
        {
            OnFamily = _ => ["MLAU1", "MLAU2"],
            OnSearch = _ => new MeliSearchResponseDto
            {
                Results = [new MeliSearchResultDto { Id = "MLA1" }, new MeliSearchResultDto { Id = "MLA2" }]
            },
            OnItems = _ =>
            [
                Item("MLA1", "Remera roja M", "4270357590777233"),
                Item("MLA2", "Remera azul L", "4270357590777233")
            ]
        };

        var results = await Search(api, "#4270357590777233");

        Assert.Contains(api.FamilyRequests, id => id == "4270357590777233");
        Assert.Contains(api.Searches, q => q.UserProductId == "MLAU1,MLAU2" && q.CatalogProductId == null);
        var family = Assert.Single(results!);
        Assert.Equal("4270357590777233", family.Id);
        Assert.Equal("Remera dry fit", family.Title);
        Assert.Equal("4270357590777233", family.FamilyId);
        Assert.Equal(2, family.Variations.Count);
        Assert.All(family.Variations, v => Assert.False(string.IsNullOrWhiteSpace(v.UserProductId)));
        Assert.Contains(family.Variations, v => v.UserProductId == "MLAU1");
        Assert.Contains(family.Variations, v => v.UserProductId == "MLAU2");
    }

    [Fact]
    public async Task FreeText_CollapsesFamilySiblingsAndKeepsLegacy()
    {
        var api = new FakeMeliApiClient
        {
            OnSearch = q => q.UserProductId != null
                ? new MeliSearchResponseDto
                {
                    Results = [new MeliSearchResultDto { Id = "MLA1" }, new MeliSearchResultDto { Id = "MLA2" }]
                }
                : new MeliSearchResponseDto
                {
                    Results =
                    [
                        new MeliSearchResultDto { Id = "MLA-LEGACY" },
                        new MeliSearchResultDto { Id = "MLA1" },
                        new MeliSearchResultDto { Id = "MLA2" }
                    ]
                },
            OnFamily = _ => ["MLAU1", "MLAU2"],
            OnItems = ids => ids.Select(id => id == "MLA-LEGACY"
                ? Item(id, "Media")
                : Item(id, id, "4270357590777233")).ToList()
        };

        var results = await Search(api, "remera");

        Assert.Equal(2, results!.Count);
        Assert.Contains(results, dto => dto.Id == "MLA-LEGACY");
        Assert.Contains(results, dto => dto.Id == "4270357590777233" && dto.Variations.Count == 2);
    }

    [Fact]
    public async Task GetItem_FamilyIdAndSiblingMla_ReturnTheSameContainer()
    {
        var api = new FakeMeliApiClient
        {
            OnFamily = _ => ["MLAU1", "MLAU2"],
            OnSearch = _ => new MeliSearchResponseDto
            {
                Results = [new MeliSearchResultDto { Id = "MLA1" }, new MeliSearchResultDto { Id = "MLA2" }]
            },
            OnItems = ids => ids.Select(id => Item(id, id, "4270357590777233")).ToList()
        };
        var proxy = new MeliProxyApi(api, new MeliCatalogHydrator(api, NullLogger<MeliCatalogHydrator>.Instance), NullLogger<MeliProxyApi>.Instance);

        var byFamily = await proxy.GetItemCoreAsync("4270357590777233", CancellationToken.None);
        var byMla = await proxy.GetItemCoreAsync("MLA1", CancellationToken.None);

        Assert.Equal("4270357590777233", byFamily!.Id);
        Assert.Equal("4270357590777233", byMla!.Id);
        Assert.Equal(2, byFamily.Variations.Count);
        Assert.Equal(2, byMla.Variations.Count);
    }

    [Fact]
    public async Task GetItem_VariationWithoutUserProduct_UsesNumericMlaId()
    {
        var api = new FakeMeliApiClient
        {
            OnFamily = _ => ["MLAU1"],
            OnSearch = _ => new MeliSearchResponseDto { Results = [new MeliSearchResultDto { Id = "MLA3752456314" }] },
            OnItems = _ =>
            [
                new MeliItem
                {
                    Id = "MLA3752456314",
                    FamilyId = "4270357590777233",
                    FamilyName = "Remera",
                    UserProductId = ""
                }
            ]
        };
        var proxy = new MeliProxyApi(api, new MeliCatalogHydrator(api, NullLogger<MeliCatalogHydrator>.Instance), NullLogger<MeliProxyApi>.Instance);

        var dto = await proxy.GetItemCoreAsync("4270357590777233", CancellationToken.None);

        Assert.Equal("3752456314", Assert.Single(dto!.Variations).UserProductId);
    }

    [Fact]
    public async Task FreeText_SearchesSkuAndQueryTogether()
    {
        var api = new FakeMeliApiClient
        {
            OnSearch = q => new MeliSearchResponseDto
            {
                Results = [new MeliSearchResultDto { Id = q.SellerSku != null ? "MLA-SKU" : "MLA-Q" }]
            },
            OnItems = ids => ids.Select(id => Item(id)).ToList()
        };

        var results = await Search(api, "ABC#ROJO#M");

        Assert.Equal(2, api.Searches.Count);
        Assert.Contains(api.Searches, q => q.SellerSku == "ABC#ROJO#M" && q.Query == null);
        Assert.Contains(api.Searches, q => q.Query == "ABC#ROJO#M" && q.SellerSku == null);
        Assert.Equal(2, results!.Count);
    }

    private static Task<List<MeliProxyItemDto>?> Search(FakeMeliApiClient api, string query)
    {
        var proxy = new MeliProxyApi(api, new MeliCatalogHydrator(api, NullLogger<MeliCatalogHydrator>.Instance), NullLogger<MeliProxyApi>.Instance);
        return proxy.SearchCoreAsync(query, CancellationToken.None);
    }

    private static MeliItem Item(string id, string? title = null, string? familyId = null) => new()
    {
        Id = id,
        Title = title ?? "Remera roja M",
        FamilyId = familyId,
        FamilyName = familyId == null ? null : "Remera dry fit",
        UserProductId = id.EndsWith('2') ? "MLAU2" : "MLAU1",
        SellerSku = "ABC#ROJO#M",
        Shipping = new MeliShipping { LogisticType = id.EndsWith('2') ? "self_service" : "fulfillment" }
    };
}
