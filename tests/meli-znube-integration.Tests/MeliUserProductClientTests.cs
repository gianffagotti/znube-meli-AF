using System.Net;
using System.Text;
using System.Text.Json;
using meli_znube_integration.Clients;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;

namespace meli_znube_integration.Tests;

public class MeliUserProductClientTests
{
    [Fact]
    public void FamilyId_OnItem_KeepsDigitsAboveInt64AndTheMlaSample()
    {
        var overflow = JsonSerializer.Deserialize<MeliItem>("""{"id":"MLBU22012","family_id":18446744000000000615}""");
        var sample = JsonSerializer.Deserialize<MeliItem>("""{"id":"MLA1","family_id":4270357590777233,"family_name":"Remera dry fit"}""");

        Assert.Equal("18446744000000000615", overflow!.FamilyId);
        Assert.Equal("4270357590777233", sample!.FamilyId);
        Assert.Equal("Remera dry fit", sample.FamilyName);
    }

    [Fact]
    public async Task GetUserProductAsync_ReadsFamilyIdAboveInt64()
    {
        var client = Client("""{"id":"MLBU22012","family_id":18446744000000000615,"user_id":1295303699}""");

        var userProduct = await client.GetUserProductAsync("MLBU22012");

        Assert.Equal("18446744000000000615", userProduct!.FamilyId);
        Assert.Contains("/user-products/MLBU22012", LastPath(client));
    }

    [Fact]
    public async Task GetUserProductFamilyAsync_ReadsUserProductsIds()
    {
        const string body = """
            {
              "user_products_ids": ["MLAU1234", "MLAU1235", "MLAU1236"],
              "family_id": 9871232123,
              "site_id": "MLA",
              "user_id": 1234
            }
            """;
        var client = Client(body);

        var ids = await client.GetUserProductFamilyAsync("9871232123");

        Assert.Equal(["MLAU1234", "MLAU1235", "MLAU1236"], ids);
        Assert.Contains("/sites/MLA/user-products-families/9871232123", LastPath(client));
    }

    [Fact]
    public async Task GetUserProductFamilyAsync_NotFound_ReturnsEmpty()
    {
        var client = Client("{}", HttpStatusCode.NotFound);

        var ids = await client.GetUserProductFamilyAsync("999");

        Assert.Empty(ids);
    }

    [Fact]
    public async Task SearchItemsAsync_JoinsUserProductIdsWithALiteralComma()
    {
        var client = Client("""{"results":["MLA3752456314","MLA3752456315"],"paging":{"limit":50,"offset":0,"total":2}}""");

        var page = await client.SearchItemsAsync(1234, new MeliItemSearchQuery { UserProductId = "MLAU1, MLAU2" });

        var path = LastPath(client);
        Assert.Contains("user_product_id=MLAU1,MLAU2", path);
        Assert.DoesNotContain("%2C", path);
        Assert.Equal(["MLA3752456314", "MLA3752456315"], page!.Results.Select(r => r.Id));
    }

    [Fact]
    public async Task GetItemsAsync_RequestsFamilyFields()
    {
        var client = Client("[]");

        await client.GetItemsAsync(["MLA1"]);

        var path = Uri.UnescapeDataString(LastPath(client));
        Assert.Contains("family_id", path);
        Assert.Contains("family_name", path);
        Assert.Contains("seller_custom_field", path);
        Assert.Contains("attributes", path);
    }

    private static RecordingClient Client(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        var factory = new StubFactory(handler);
        return new RecordingClient(new MeliApiClient(factory), handler);
    }

    private static string LastPath(RecordingClient client)
    {
        var uri = client.Handler.Requests[^1].RequestUri!;
        return uri.PathAndQuery;
    }

    private sealed class RecordingClient(MeliApiClient client, RecordingHandler handler)
    {
        public RecordingHandler Handler { get; } = handler;
        public Task<IReadOnlyList<string>> GetUserProductFamilyAsync(string familyId) => client.GetUserProductFamilyAsync(familyId);
        public Task<MeliUserProductDto?> GetUserProductAsync(string id) => client.GetUserProductAsync(id);
        public Task<MeliSearchResponseDto?> SearchItemsAsync(long sellerId, MeliItemSearchQuery query) => client.SearchItemsAsync(sellerId, query);
        public Task<List<MeliItem>> GetItemsAsync(IEnumerable<string> ids) => client.GetItemsAsync(ids);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.mercadolibre.com/")
        };
    }
}
