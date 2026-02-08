using meli_znube_integration.Models;
using System.Net;
using System.Text.Json;

namespace meli_znube_integration.Clients;

public class ZnubeApiClient : IZnubeApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ZnubeApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private HttpClient GetClient() => _httpClientFactory.CreateClient("znube");

    public async Task<OmnichannelResponse?> GetStockBySkuAsync(string sku, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var client = GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"Omnichannel/GetStock?sku={Uri.EscapeDataString(sku)}");
        using var res = await client.SendAsync(req, cancellationToken);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        var dto = JsonSerializer.Deserialize<OmnichannelResponse>(body, JsonOptions);
        if (dto?.Data == null || dto.Data.TotalSku == 0) return null;
        return dto;
    }

    public async Task<OmnichannelResponse?> GetStockByProductIdAsync(string productId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        var client = GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"Omnichannel/GetStock?sku={Uri.EscapeDataString(productId)}#");
        using var res = await client.SendAsync(req, cancellationToken);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<OmnichannelResponse>(body, JsonOptions);
    }
}
