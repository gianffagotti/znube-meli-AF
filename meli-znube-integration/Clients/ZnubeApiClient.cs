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

    /// <summary>
    /// Page size for Znube GetStock by productId. API max is 100. Spec 03.
    /// </summary>
    private const int StockPageLimit = 100;

    public async Task<OmnichannelResponse?> GetStockByProductIdAsync(string productId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        var client = GetClient();
        var baseQuery = $"Omnichannel/GetStock?sku={Uri.EscapeDataString(productId)}%23";
        var allStock = new List<OmnichannelStockItem>();
        OmnichannelResponse? firstResponse = null;
        var offset = 0;

        do
        {
            var url = $"{baseQuery}&limit={StockPageLimit}&offset={offset}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await client.SendAsync(req, cancellationToken);
            if (res.StatusCode == HttpStatusCode.NotFound) return null;
            if (!res.IsSuccessStatusCode) res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadAsStringAsync(cancellationToken);
            var page = JsonSerializer.Deserialize<OmnichannelResponse>(body, JsonOptions);
            if (page?.Data == null) return firstResponse ?? page;

            if (firstResponse == null)
                firstResponse = page;

            if (page.Data.Stock != null)
                allStock.AddRange(page.Data.Stock);

            var totalSku = page.Data.TotalSku;
            offset += StockPageLimit;
            if (totalSku <= offset || (page.Data.Stock?.Count ?? 0) == 0)
                break;
        } while (true);

        if (firstResponse?.Data == null) return firstResponse;
        firstResponse.Data.Stock = allStock;
        return firstResponse;
    }
}
