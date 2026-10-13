using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace meli_znube_integration.Tests;

public class MeliCatalogHydratorTests
{
    [Fact]
    public async Task LegacyItem_WithVariations_DoesNotCallMeli()
    {
        var api = new FakeMeliApiClient();
        var hydrator = NewHydrator(api);
        var item = new MeliItem
        {
            Id = "MLA1",
            Variations = [new MeliVariation { Id = 10, UserProductId = "MLAU1" }, new MeliVariation { Id = 11 }]
        };

        var result = await hydrator.HydrateAsync(item, "1234");

        Assert.Equal(2, result.Variations.Count);
        Assert.Empty(api.FamilyRequests);
        Assert.Empty(api.UserProductRequests);
        Assert.Empty(api.Searches);
        Assert.Empty(api.ItemRequests);
    }

    [Fact]
    public async Task Family_BuildsOneVariationPerSibling_AndInjectsSkuFromSellerCustomField()
    {
        var api = new FakeMeliApiClient
        {
            OnFamily = _ => ["MLAU1", "MLAU2"],
            OnSearch = _ => new MeliSearchResponseDto
            {
                Results = [new MeliSearchResultDto { Id = "MLA3752456314" }, new MeliSearchResultDto { Id = "MLA3752456400" }]
            },
            OnItems = _ =>
            [
                Sibling("MLA3752456314", "MLAU1", "Rojo", "M", sellerCustomField: "ABC#ROJO#M", quantity: 4),
                Sibling("MLA3752456400", "MLAU2", "Azul", "L", sellerSku: "ABC#AZUL#L", quantity: 2)
            ]
        };
        var hydrator = NewHydrator(api);
        var item = new MeliItem
        {
            Id = "MLA3752456314",
            Title = "Remera roja M",
            FamilyName = "Remera dry fit",
            FamilyId = "4270357590777233",
            UserProductId = "MLAU1"
        };

        var result = await hydrator.HydrateAsync(item, "1234");

        Assert.Equal("4270357590777233", result.Id);
        Assert.Equal("Remera dry fit", result.Title);
        Assert.Equal("Remera dry fit", result.FamilyName);
        Assert.Equal(["4270357590777233"], api.FamilyRequests);
        Assert.Equal("MLAU1,MLAU2", api.Searches.Single().UserProductId);
        Assert.Null(api.Searches.Single().CatalogProductId);
        Assert.Equal(2, result.Variations.Count);

        var red = result.Variations.Single(v => v.Id == 3752456314);
        Assert.Equal("MLAU1", red.UserProductId);
        Assert.Equal(4, red.AvailableQuantity);
        Assert.Equal("ABC#ROJO#M", red.SellerSku);
        Assert.Contains(red.Attributes, a => a.Id == "COLOR" && a.ValueName == "Rojo");
        Assert.Contains(red.Attributes, a => a.Id == "SIZE" && a.ValueName == "M");

        var blue = result.Variations.Single(v => v.Id == 3752456400);
        Assert.Equal("ABC#AZUL#L", blue.SellerSku);
        Assert.Contains(blue.Attributes, a => a.Id == "COLOR" && a.ValueName == "Azul");
    }

    [Fact]
    public async Task MissingFamilyId_IsResolvedFromTheUserProduct()
    {
        var api = new FakeMeliApiClient
        {
            OnUserProduct = _ => new MeliUserProductDto { Id = "MLAU999", FamilyId = "4270357590777233" },
            OnFamily = _ => ["MLAU999"],
            OnSearch = _ => new MeliSearchResponseDto { Results = [new MeliSearchResultDto { Id = "MLA10" }] },
            OnItems = _ => [Sibling("MLA10", "MLAU999", "Negro", "S", sellerSku: "SKU-1", quantity: 1)]
        };
        var item = new MeliItem { Id = "MLA10", UserProductId = "MLAU999" };

        var result = await NewHydrator(api).HydrateAsync(item, "1234");

        Assert.Equal(["MLAU999"], api.UserProductRequests);
        Assert.Equal(["4270357590777233"], api.FamilyRequests);
        Assert.Equal("4270357590777233", result.Id);
        Assert.Equal("4270357590777233", result.FamilyId);
        Assert.Single(result.Variations);
    }

    [Fact]
    public async Task PublicationWithoutFamily_BecomesAlwaysOne()
    {
        var api = new FakeMeliApiClient();
        var item = new MeliItem
        {
            Id = "MLA222",
            AvailableQuantity = 5,
            SellerCustomField = "ABC#NEGRO#L",
            Attributes = [new MeliAttribute { Id = "COLOR", ValueName = "Negro" }]
        };

        var result = await NewHydrator(api).HydrateAsync(item, "1234");

        Assert.Empty(api.UserProductRequests);
        Assert.Equal("MLA222", result.Id);
        var only = Assert.Single(result.Variations);
        Assert.Equal(0, only.Id);
        Assert.Equal("MLA222", only.UserProductId);
        Assert.Equal(5, only.AvailableQuantity);
        Assert.Equal("ABC#NEGRO#L", only.SellerSku);
        Assert.Contains(only.Attributes, a => a.Id == "COLOR" && a.ValueName == "Negro");
    }

    [Fact]
    public async Task AlreadyContainer_DoesNotRequestTheFamilyAgain()
    {
        var api = new FakeMeliApiClient();
        var item = new MeliItem
        {
            Id = "4270357590777233",
            FamilyId = "4270357590777233",
            Title = "Remera dry fit",
            Variations = [new MeliVariation { Id = 10, UserProductId = "MLAU1" }]
        };

        var result = await NewHydrator(api).HydrateAsync(item, "1234");

        Assert.Same(item, result);
        Assert.Empty(api.FamilyRequests);
    }

    [Fact]
    public async Task MlaWithVariationsAlreadyFilled_StillBecomesTheFamilyContainer()
    {
        var api = new FakeMeliApiClient
        {
            OnFamily = _ => ["MLAU1"],
            OnSearch = _ => new MeliSearchResponseDto { Results = [new MeliSearchResultDto { Id = "MLA10" }] },
            OnItems = _ => [Sibling("MLA10", "MLAU1", "Negro", "S", sellerSku: "SKU", quantity: 1)]
        };
        var item = new MeliItem
        {
            Id = "MLA10",
            FamilyId = "4270357590777233",
            FamilyName = "Remera",
            Variations = [new MeliVariation { Id = 1, UserProductId = "STALE" }]
        };

        var result = await NewHydrator(api).HydrateAsync(item, "1234");

        Assert.Equal("4270357590777233", result.Id);
        Assert.Equal("MLAU1", Assert.Single(result.Variations).UserProductId);
    }

    [Fact]
    public async Task FamilyWithoutSiblings_KeepsTheMlaAndAppliesAlwaysOne()
    {
        var api = new FakeMeliApiClient { OnFamily = _ => [] };
        var item = new MeliItem { Id = "MLA10", FamilyId = "4270357590777233", Title = "Suelta" };

        var result = await NewHydrator(api).HydrateAsync(item, "1234");

        Assert.Equal("MLA10", result.Id);
        Assert.Equal("Suelta", result.Title);
        Assert.Single(result.Variations);
    }

    [Fact]
    public async Task FetchFamilyId_DoesNotSearchItAsUserProduct_AndKeepsValuesAboveInt64()
    {
        var api = new FakeMeliApiClient
        {
            OnFamily = _ => ["MLAU1"],
            OnSearch = _ => new MeliSearchResponseDto { Results = [new MeliSearchResultDto { Id = "MLA10" }] },
            OnItems = _ => [Sibling("MLA10", "MLAU1", "Negro", "S", sellerSku: "SKU", quantity: 3)]
        };

        var result = await NewHydrator(api).FetchAndHydrateByIdAsync("18446744000000000615", "1234");

        Assert.NotNull(result);
        Assert.Equal("18446744000000000615", result!.Id);
        Assert.Equal(["18446744000000000615"], api.FamilyRequests);
        Assert.DoesNotContain(api.Searches, q => q.UserProductId == "18446744000000000615");
        Assert.DoesNotContain(api.ItemRequests, ids => ids.Contains("MLA18446744000000000615"));
    }

    [Fact]
    public async Task FetchUserProduct_HydratesTheResolvedMla()
    {
        var api = new FakeMeliApiClient
        {
            OnSearch = q => q.UserProductId == "MLAU999"
                ? new MeliSearchResponseDto { Results = [new MeliSearchResultDto { Id = "MLA10" }] }
                : null,
            OnItems = _ => [new MeliItem { Id = "MLA10", Title = "Suelta", SellerSku = "SKU" }]
        };

        var result = await NewHydrator(api).FetchAndHydrateByIdAsync("mlau999", "1234");

        Assert.Equal("MLA10", result!.Id);
        Assert.Equal("MLAU999", api.Searches.Single().UserProductId);
        Assert.Empty(api.FamilyRequests);
    }

    [Fact]
    public async Task FetchNumericItemId_NormalizesToMla()
    {
        var api = new FakeMeliApiClient
        {
            OnItems = ids => ids[0] == "MLA3752456314" ? [new MeliItem { Id = "MLA3752456314", Title = "Item" }] : []
        };

        var result = await NewHydrator(api).FetchAndHydrateByIdAsync("3752456314", "1234");

        Assert.Equal("MLA3752456314", result!.Id);
        Assert.Equal(["MLA3752456314"], api.ItemRequests.Single());
    }

    [Fact]
    public async Task FetchUnknownId_ReturnsNull()
    {
        var result = await NewHydrator(new FakeMeliApiClient()).FetchAndHydrateByIdAsync("no-es-un-id", "1234");
        Assert.Null(result);
    }

    private static MeliCatalogHydrator NewHydrator(FakeMeliApiClient api) =>
        new(api, NullLogger<MeliCatalogHydrator>.Instance);

    private static MeliItem Sibling(string id, string userProductId, string color, string size, string? sellerSku = null, string? sellerCustomField = null, int quantity = 0) =>
        new()
        {
            Id = id,
            UserProductId = userProductId,
            SellerSku = sellerSku,
            SellerCustomField = sellerCustomField,
            AvailableQuantity = quantity,
            Attributes =
            [
                new MeliAttribute { Id = "COLOR", ValueName = color },
                new MeliAttribute { Id = "SIZE", ValueName = size }
            ]
        };
}
