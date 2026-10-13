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
            OnItems = _ => [Item("MLA1", "Remera roja M"), Item("MLA2", "Remera azul L")]
        };

        var results = await Search(api, "#4270357590777233");

        Assert.Equal(["4270357590777233"], api.FamilyRequests);
        Assert.Equal("MLAU1,MLAU2", api.Searches.Single().UserProductId);
        Assert.Null(api.Searches.Single().CatalogProductId);
        Assert.Equal(2, results!.Count);
        Assert.All(results, dto => Assert.Equal("Remera dry fit", dto.FamilyName));
        Assert.All(results, dto => Assert.Equal("4270357590777233", dto.FamilyId));
        Assert.Contains(results, dto => dto.Title == "Remera roja M" && dto.SellerSku == "ABC#ROJO#M" && dto.Shipping!.LogisticType == "fulfillment");
        Assert.Contains(results, dto => dto.Title == "Remera azul L" && dto.Shipping!.LogisticType == "self_service");
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

    private static MeliItem Item(string id, string? title = null) => new()
    {
        Id = id,
        Title = title ?? "Remera roja M",
        FamilyId = "4270357590777233",
        FamilyName = "Remera dry fit",
        UserProductId = "MLAU1",
        SellerSku = "ABC#ROJO#M",
        Shipping = new MeliShipping { LogisticType = id.EndsWith('2') ? "self_service" : "fulfillment" }
    };
}
