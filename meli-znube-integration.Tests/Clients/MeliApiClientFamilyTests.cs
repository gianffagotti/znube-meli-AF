using System.Net;
using System.Text;
using FluentAssertions;
using meli_znube_integration.Clients;
using meli_znube_integration.Models.Dtos;
using Moq;

namespace meli_znube_integration.Tests.Clients;

public class MeliApiClientFamilyTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(_responder(request));
        }
    }

    private static (MeliApiClient client, FakeHttpMessageHandler handler) CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        return (new MeliApiClient(factory.Object), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetUserProductsFamilyAsync_returns_family_on_success()
    {
        var (client, handler) = CreateClient(_ =>
            Json(HttpStatusCode.OK, "{\"id\":\"F1\",\"user_products_ids\":[\"MLAU1\",\"MLAU2\"]}"));

        var family = await client.GetUserProductsFamilyAsync("F1");

        family.Should().NotBeNull();
        family!.UserProductsIds.Should().ContainInOrder("MLAU1", "MLAU2");
        handler.LastRequestUri!.AbsolutePath.Should().Contain("user-products-families/F1");
    }

    [Fact]
    public async Task GetUserProductsFamilyAsync_returns_null_on_non_success()
    {
        var (client, _) = CreateClient(_ => Json(HttpStatusCode.NotFound, "{}"));

        var family = await client.GetUserProductsFamilyAsync("missing");

        family.Should().BeNull();
    }

    [Fact]
    public async Task GetUserProductsFamilyAsync_returns_null_and_skips_http_on_blank_id()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, "{}"));

        var family = await client.GetUserProductsFamilyAsync("   ");

        family.Should().BeNull();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveUserProductsAsync_returns_products_for_batch()
    {
        var (client, _) = CreateClient(_ =>
            Json(HttpStatusCode.OK, "[{\"id\":\"MLAU1\",\"seller_sku\":\"S1\",\"attributes\":[]},{\"id\":\"MLAU2\",\"seller_sku\":\"S2\",\"attributes\":[]}]"));

        var result = await client.ResolveUserProductsAsync(new[] { "MLAU1", "MLAU2" });

        result.Should().HaveCount(2);
        result.Select(r => r.Id).Should().ContainInOrder("MLAU1", "MLAU2");
    }

    [Fact]
    public async Task ResolveUserProductsAsync_skips_error_envelope_entries()
    {
        var (client, _) = CreateClient(_ =>
            Json(HttpStatusCode.OK, "[{\"code\":200,\"body\":{\"id\":\"MLAU1\",\"seller_sku\":\"S1\"}},{\"code\":404,\"body\":{\"error\":\"not_found\"}}]"));

        var result = await client.ResolveUserProductsAsync(new[] { "MLAU1", "MLAU2" });

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("MLAU1");
    }

    [Fact]
    public async Task ResolveUserProductsAsync_returns_empty_without_http_when_no_ids()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK, "[]"));

        var result = await client.ResolveUserProductsAsync(Array.Empty<string>());

        result.Should().BeEmpty();
        handler.CallCount.Should().Be(0);
    }
}
