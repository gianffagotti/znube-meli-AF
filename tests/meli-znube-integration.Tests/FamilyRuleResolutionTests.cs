using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace meli_znube_integration.Tests;

public class FamilyRuleResolutionTests : IDisposable
{
    public FamilyRuleResolutionTests()
    {
        Environment.SetEnvironmentVariable(EnvVars.Keys.MeliSellerId, "1234");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVars.Keys.MeliSellerId, null);
    }

    [Fact]
    public void AffectedSourceKeys_IncludesMlaAndFamily_WithoutDuplicates()
    {
        Assert.Equal(["MLA1", "1780129655050322"], MeliItemIdClassifier.AffectedSourceKeys("MLA1", "1780129655050322"));
        Assert.Equal(["MLA1"], MeliItemIdClassifier.AffectedSourceKeys("MLA1", null));
        Assert.Equal(["MLA1"], MeliItemIdClassifier.AffectedSourceKeys("MLA1", "MLA1"));
    }

    [Fact]
    public async Task Resolver_FindsPackRuleByFamilyId_BeforeReturningNull()
    {
        var familyId = "1780129655050322";
        var pack = new StockRuleDto { TargetItemId = familyId, RuleType = StockRuleTypes.Pack };
        var lookup = new FakeLookup();
        lookup.Rules[familyId] = pack;
        var api = new FakeMeliApiClient
        {
            OnItems = _ => [new MeliItem { Id = "MLA1", FamilyId = familyId }]
        };

        var rule = await new OrderItemRuleResolver(lookup, api).GetRuleAsync("MLA1");

        Assert.Same(pack, rule);
        Assert.Equal(["MLA1", familyId], lookup.Requests);
    }

    [Fact]
    public async Task Resolver_PrefersTheMlaRule()
    {
        var mla = new StockRuleDto { TargetItemId = "MLA1", RuleType = StockRuleTypes.Pack };
        var lookup = new FakeLookup();
        lookup.Rules["MLA1"] = mla;
        lookup.Rules["1780129655050322"] = new StockRuleDto { TargetItemId = "1780129655050322", RuleType = StockRuleTypes.Pack };
        var api = new FakeMeliApiClient();

        var rule = await new OrderItemRuleResolver(lookup, api).GetRuleAsync("MLA1");

        Assert.Same(mla, rule);
        Assert.Empty(api.ItemRequests);
    }

    [Fact]
    public async Task Expander_AppliesFamilyPack_WhenSkuExistsInZnube()
    {
        var rule = new StockRuleDto
        {
            TargetItemId = "1780129655050322",
            RuleType = StockRuleTypes.Pack,
            Mappings =
            [
                new VariantMappingDto
                {
                    TargetVariantId = "MLAU10",
                    Strategy = "Explicit",
                    SourceMatches = [new RuleSourceMatchDto { SourceItemId = "1780129655050322", SourceVariantId = "MLAU20", SourceSku = "SRC", Quantity = 2 }]
                }
            ]
        };
        var znube = new ThrowingZnube();
        var expander = new OrderItemExpander(
            znube,
            new FixedResolver(rule),
            new MeliItemNormalizer(),
            new MeliCatalogHydrator(new FakeMeliApiClient(), NullLogger<MeliCatalogHydrator>.Instance),
            NullLogger<OrderItemExpander>.Instance);

        var resolved = await expander.ExpandItemsAsync(
        [
            new MeliOrderItem { ItemId = "MLA1", UserProductId = "MLAU10", SellerSku = "ABC#ROJO#M", Quantity = 1, Title = "Remera" }
        ]);

        var line = Assert.Single(resolved!);
        Assert.Equal(StockRuleTypes.Pack, line.RuleType);
        Assert.Equal("SRC", line.Sku);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(0, znube.Calls);
    }

    [Fact]
    public async Task Expander_MatchesSiblingMla_WhenOrderHasNoUserProduct()
    {
        var rule = new StockRuleDto
        {
            TargetItemId = "1780129655050322",
            RuleType = StockRuleTypes.Pack,
            Mappings =
            [
                new VariantMappingDto
                {
                    TargetVariantId = "MLAU10",
                    Strategy = "Explicit",
                    SourceMatches = [new RuleSourceMatchDto { SourceItemId = "1780", SourceSku = "SRC", Quantity = 1 }]
                }
            ]
        };
        var api = new FakeMeliApiClient
        {
            OnItems = ids =>
            [
                new MeliItem
                {
                    Id = ids[0],
                    FamilyId = "1780129655050322",
                    FamilyName = "Remera",
                    UserProductId = "MLAU10"
                }
            ],
            OnFamily = _ => ["MLAU10"],
            OnSearch = _ => new MeliSearchResponseDto { Results = [new MeliSearchResultDto { Id = "MLA3752456314" }] }
        };
        var expander = new OrderItemExpander(
            new ThrowingZnube(),
            new FixedResolver(rule),
            new MeliItemNormalizer(),
            new MeliCatalogHydrator(api, NullLogger<MeliCatalogHydrator>.Instance),
            NullLogger<OrderItemExpander>.Instance);

        var resolved = await expander.ExpandItemsAsync(
        [
            new MeliOrderItem { ItemId = "MLA3752456314", SellerSku = "NOT-THE-VARIANT", Quantity = 1, Title = "Remera" }
        ]);

        Assert.Equal(StockRuleTypes.Pack, Assert.Single(resolved!).RuleType);
    }

    private sealed class FakeLookup : IStockRuleLookup
    {
        public Dictionary<string, StockRuleDto> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Requests { get; } = [];

        public Task<StockRuleDto?> GetRuleAsync(string sellerId, string targetItemId)
        {
            Requests.Add(targetItemId);
            Rules.TryGetValue(targetItemId, out var rule);
            return Task.FromResult(rule);
        }
    }

    private sealed class FixedResolver : IOrderItemRuleResolver
    {
        private readonly StockRuleDto _rule;
        public FixedResolver(StockRuleDto rule) => _rule = rule;
        public Task<StockRuleDto?> GetRuleAsync(string itemId, CancellationToken cancellationToken = default) => Task.FromResult<StockRuleDto?>(_rule);
    }

    private sealed class ThrowingZnube : IZnubeApiClient
    {
        public int Calls { get; private set; }
        public Task<OmnichannelResponse?> GetStockBySkuAsync(string sku, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Znube FULL shortcut must not run for a family PACK.");
        }

        public Task<OmnichannelResponse?> GetStockByProductIdAsync(string productId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OmnichannelResponse?>(null);
    }
}
