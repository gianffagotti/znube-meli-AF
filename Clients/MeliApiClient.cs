using System.Net;
using System.Text.Json;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;

namespace meli_znube_integration.Clients;

public class MeliApiClient : IMeliApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public MeliApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private HttpClient GetClient() => _httpClientFactory.CreateClient("meli");

    public async Task<MeliShipmentDto?> GetShipmentAsync(string shipmentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shipmentId)) return null;
        var client = GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"shipments/{Uri.EscapeDataString(shipmentId)}");
        req.Headers.TryAddWithoutValidation("x-format-new", "true");
        using var res = await client.SendAsync(req, cancellationToken);
        if (!res.IsSuccessStatusCode) return null;
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<MeliShipmentDto>(json, JsonOptions);
    }

    public async Task<MeliOrderDto?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return null;
        var client = GetClient();
        using var res = await client.GetAsync($"orders/{Uri.EscapeDataString(orderId)}", cancellationToken);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        var dto = JsonSerializer.Deserialize<MeliOrderDto>(json, JsonOptions);
        if (dto != null && dto.Id == null)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl))
                dto.Id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString();
        }
        return dto;
    }

    public async Task<List<MeliOrderDto>> GetPackOrdersAsync(string packId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packId)) return new List<MeliOrderDto>();
        var client = GetClient();
        using var res = await client.GetAsync($"packs/{Uri.EscapeDataString(packId)}", cancellationToken);
        if (!res.IsSuccessStatusCode) return new List<MeliOrderDto>();
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        var pack = JsonSerializer.Deserialize<MeliPackDto>(json, JsonOptions);
        if (pack?.Orders != null && pack.Orders.Count > 0)
            return pack.Orders;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var orderIds = new List<string>();
        if (root.TryGetProperty("orders", out var ordersEl) && ordersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in ordersEl.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var idEl))
                {
                    var idStr = idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(idStr)) orderIds.Add(idStr!);
                }
                else if (entry.ValueKind == JsonValueKind.Number)
                    orderIds.Add(entry.GetRawText());
                else if (entry.ValueKind == JsonValueKind.String)
                {
                    var idStr = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(idStr)) orderIds.Add(idStr!);
                }
            }
        }
        if (orderIds.Count == 0) return new List<MeliOrderDto>();
        var tasks = orderIds.Select(id => GetOrderAsync(id, cancellationToken)).ToArray();
        var orders = await Task.WhenAll(tasks);
        return orders.Where(o => o != null).Cast<MeliOrderDto>().ToList();
    }

    public async Task<bool> CreateOrderNoteAsync(string orderId, string note, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(note)) return false;
        var client = GetClient();
        var body = JsonSerializer.Serialize(new { note }, JsonOptions);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var res = await client.PostAsync($"orders/{Uri.EscapeDataString(orderId)}/notes", content, cancellationToken);
        return res.IsSuccessStatusCode;
    }

    public async Task<List<string>> GetOrderNotesAsync(string orderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return new List<string>();
        var client = GetClient();
        using var res = await client.GetAsync($"orders/{Uri.EscapeDataString(orderId)}/notes", cancellationToken);
        if (res.StatusCode == HttpStatusCode.NotFound) return new List<string>();
        if (!res.IsSuccessStatusCode) res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var list = JsonSerializer.Deserialize<List<MeliNotesResponseDto>>(json, JsonOptions);
            if (list != null)
                return list.SelectMany(x => x.Results ?? new List<MeliNoteResultDto>())
                    .Select(r => r.Note)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Cast<string>()
                    .ToList();
        }
        catch
        {
            // Fallback: root might be a single object with results
            var single = JsonSerializer.Deserialize<MeliNotesResponseDto>(json, JsonOptions);
            if (single?.Results != null)
                return single.Results.Select(r => r.Note).Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().ToList();
        }
        return new List<string>();
    }

    public async Task<MeliSearchResponseDto?> SearchOrdersAsync(long sellerId, string buyerNickname, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(buyerNickname)) return null;
        var client = GetClient();
        if (from > to) (to, from) = (from, to);
        var fromParam = from.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
        var toParam = to.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
        var query = "orders/search?seller=" + Uri.EscapeDataString(sellerId.ToString())
            + "&order.date_created.from=" + Uri.EscapeDataString(fromParam)
            + "&order.date_created.to=" + Uri.EscapeDataString(toParam)
            + "&q=" + Uri.EscapeDataString(buyerNickname);
        using var res = await client.GetAsync(query, cancellationToken);
        if (!res.IsSuccessStatusCode) return null;
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<MeliSearchResponseDto>(json, JsonOptions);
    }

    public async Task<bool> SendMessageAsync(string packOrOrderId, string text, string optionId = "OTHER", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packOrOrderId) || string.IsNullOrWhiteSpace(text)) return false;
        var client = GetClient();
        var body = JsonSerializer.Serialize(new { option_id = optionId, text }, JsonOptions);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var res = await client.PostAsync($"messages/action_guide/packs/{Uri.EscapeDataString(packOrOrderId)}/option", content, cancellationToken);
        return res.IsSuccessStatusCode;
    }

    public async Task<MeliScanResponseDto?> ScanItemsAsync(long userId, string? scrollId, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        var url = $"users/{userId}/items/search?search_type=scan&status=active";
        if (!string.IsNullOrWhiteSpace(scrollId))
            url += "&scroll_id=" + Uri.EscapeDataString(scrollId);
        using var res = await client.GetAsync(url, cancellationToken);
        if (!res.IsSuccessStatusCode) return null;
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<MeliScanResponseDto>(json, JsonOptions);
    }

    public async Task<List<MeliItem>> GetItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return new List<MeliItem>();
        var client = GetClient();
        var idsParam = string.Join(",", idList);
        var url = $"items?ids={idsParam}&include_attributes=all&attributes=id,shipping,variations,seller_custom_field,attributes,title,price,thumbnail,permalink";
        using var res = await client.GetAsync(url, cancellationToken);
        if (!res.IsSuccessStatusCode) return new List<MeliItem>();
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = new List<MeliItem>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                if (!entry.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) continue;
                if (body.TryGetProperty("error", out _)) continue;
                var item = JsonSerializer.Deserialize<MeliItem>(body.GetRawText(), JsonOptions);
                if (item != null) items.Add(item);
            }
        }
        return items;
    }

    public async Task<(int Quantity, string Version)?> GetUserProductStockAsync(string userProductId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProductId)) return null;
        var client = GetClient();
        using var res = await client.GetAsync($"user-products/{Uri.EscapeDataString(userProductId)}/stock", cancellationToken);
        if (!res.IsSuccessStatusCode) return null;
        var version = res.Headers.TryGetValues("x-version", out var values) ? (values.FirstOrDefault() ?? string.Empty) : string.Empty;
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        var stock = JsonSerializer.Deserialize<MeliUserProductStockResponseDto>(json, JsonOptions);
        var quantity = stock?.Locations?.FirstOrDefault(l => string.Equals(l.Type, "selling_address", StringComparison.OrdinalIgnoreCase))?.Quantity ?? 0;
        return (quantity, version);
    }

    public async Task<MeliUserProductStockResponseDto?> GetUserProductStockResponseAsync(string userProductId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProductId)) return null;
        var client = GetClient();
        using var res = await client.GetAsync($"user-products/{Uri.EscapeDataString(userProductId)}/stock", cancellationToken);
        if (!res.IsSuccessStatusCode) return null;
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<MeliUserProductStockResponseDto>(json, JsonOptions);
    }

    public async Task<bool> UpdateUserProductStockAsync(string userProductId, int quantity, string version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProductId)) return false;
        var client = GetClient();
        using var req = new HttpRequestMessage(HttpMethod.Put, $"user-products/{Uri.EscapeDataString(userProductId)}/stock/type/selling_address");
        req.Headers.TryAddWithoutValidation("x-version", version ?? string.Empty);
        req.Content = new StringContent(JsonSerializer.Serialize(new { quantity }, JsonOptions), System.Text.Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req, cancellationToken);
        if (res.StatusCode == HttpStatusCode.Conflict) return false;
        res.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<MeliSearchResponseDto?> SearchItemsAsync(long sellerId, MeliItemSearchQuery query, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        var q = query?.Status ?? "active";
        var parts = new List<string> { "status=" + Uri.EscapeDataString(q) };
        if (!string.IsNullOrWhiteSpace(query?.Query)) parts.Add("q=" + Uri.EscapeDataString(query.Query));
        if (!string.IsNullOrWhiteSpace(query?.SellerSku)) parts.Add("seller_sku=" + Uri.EscapeDataString(query.SellerSku));
        if (!string.IsNullOrWhiteSpace(query?.UserProductId)) parts.Add("user_product_id=" + Uri.EscapeDataString(query.UserProductId));
        var queryString = string.Join("&", parts);
        var url = $"users/{sellerId}/items/search?{queryString}";
        using var res = await client.GetAsync(url, cancellationToken);
        if (!res.IsSuccessStatusCode) return null;
        var json = await res.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<MeliSearchResponseDto>(json, JsonOptions);
    }
}
