using meli_znube_integration.Common;
using meli_znube_integration.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace meli_znube_integration.Clients;

public class MeliClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public MeliClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public sealed class MeliShipmentInfo
    {
        public string? LogisticType { get; set; }
        public bool IsFull { get; set; }
        public bool IsFlex { get; set; }
        public string? Zone { get; set; }
    }

    public async Task<MeliShipmentInfo?> GetShipmentInfoAsync(MeliOrder order)
    {
        if (order == null || string.IsNullOrWhiteSpace(order.ShippingId))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"shipments/{order.ShippingId}");
        req.Headers.TryAddWithoutValidation("x-format-new", "true");

        using var res = await client.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Tipo logístico
        string? logisticType = null;
        if (root.TryGetProperty("logistic", out var logistic) && logistic.ValueKind == JsonValueKind.Object)
        {
            if (logistic.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            {
                logisticType = typeEl.GetString();
            }
        }

        var envFlex = EnvVars.GetString(EnvVars.Keys.MeliLogisticTypeFlex);
        var envFull = EnvVars.GetString(EnvVars.Keys.MeliLogisticTypeFull);

        bool isFull = !string.IsNullOrWhiteSpace(logisticType)
            && (
                (!string.IsNullOrWhiteSpace(envFull) && string.Equals(logisticType, envFull, StringComparison.OrdinalIgnoreCase))
                || string.Equals(logisticType, "full", StringComparison.OrdinalIgnoreCase)
                || string.Equals(logisticType, "fulfillment", StringComparison.OrdinalIgnoreCase)
            );
        bool isFlex = !string.IsNullOrWhiteSpace(logisticType)
            && (
                (!string.IsNullOrWhiteSpace(envFlex) && string.Equals(logisticType, envFlex, StringComparison.OrdinalIgnoreCase))
                || string.Equals(logisticType, "flex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(logisticType, "self_service", StringComparison.OrdinalIgnoreCase)
            );

        // Zona (nuevo formato con fallback)
        string? zone = null;
        if (root.TryGetProperty("destination", out var destination) && destination.ValueKind == JsonValueKind.Object)
        {
            if (destination.TryGetProperty("shipping_address", out var newAddr) && newAddr.ValueKind == JsonValueKind.Object)
            {
                string? cityNew = null;
                if (newAddr.TryGetProperty("city", out var cityObjNew) && cityObjNew.ValueKind == JsonValueKind.Object && cityObjNew.TryGetProperty("name", out var cityNameNew) && cityNameNew.ValueKind == JsonValueKind.String)
                {
                    cityNew = cityNameNew.GetString();
                }

                if (!string.IsNullOrWhiteSpace(cityNew))
                {
                    zone = cityNew;
                }
            }
        }
        if (zone == null)
        {
            if (root.TryGetProperty("receiver_address", out var addr) && addr.ValueKind == JsonValueKind.Object)
            {
                string? city = null;
                if (addr.TryGetProperty("city", out var cityObj) && cityObj.ValueKind == JsonValueKind.Object && cityObj.TryGetProperty("name", out var cityName) && cityName.ValueKind == JsonValueKind.String)
                {
                    city = cityName.GetString();
                }
                if (!string.IsNullOrWhiteSpace(city))
                {
                    zone = city;
                }
            }
        }

        return new MeliShipmentInfo
        {
            LogisticType = logisticType,
            IsFull = isFull,
            IsFlex = isFlex,
            Zone = zone
        };
    }
    public async Task<MeliOrder?> GetOrderAsync(string orderId)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"orders/{orderId}");

        using var res = await client.SendAsync(req);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!res.IsSuccessStatusCode)
        {
            res.EnsureSuccessStatusCode();
        }

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var items = new List<MeliOrderItem>();
        string? idStr = null;
        string? packId = null;
        DateTimeOffset? createdUtc = null;
        string? buyerNickname = null;
        string? buyerFirstName = null;
        if (root.TryGetProperty("id", out var idEl))
        {
            if (idEl.ValueKind == JsonValueKind.Number)
            {
                idStr = idEl.GetRawText();
            }
            else if (idEl.ValueKind == JsonValueKind.String)
            {
                idStr = idEl.GetString();
            }
        }
        if (root.TryGetProperty("pack_id", out var packEl))
        {
            if (packEl.ValueKind == JsonValueKind.Number)
            {
                packId = packEl.GetRawText();
            }
            else if (packEl.ValueKind == JsonValueKind.String)
            {
                packId = packEl.GetString();
            }
            else if (packEl.ValueKind == JsonValueKind.Null)
            {
                packId = null;
            }
        }
        if (root.TryGetProperty("date_created", out var dc) && dc.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(dc.GetString(), out var parsed))
            {
                createdUtc = parsed.ToUniversalTime();
            }
        }
        if (root.TryGetProperty("order_items", out var orderItems) && orderItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var oi in orderItems.EnumerateArray())
            {
                string? title = null;
                string? sellerSku = null;
                int quantity = 1;
                if (oi.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        title = t.GetString();
                    }
                    if (item.TryGetProperty("seller_sku", out var skuInItem) && skuInItem.ValueKind == JsonValueKind.String)
                    {
                        sellerSku = skuInItem.GetString();
                    }
                }
                if (string.IsNullOrWhiteSpace(sellerSku) && oi.TryGetProperty("seller_sku", out var sku) && sku.ValueKind == JsonValueKind.String)
                {
                    sellerSku = sku.GetString();
                }
                if (oi.TryGetProperty("quantity", out var qEl))
                {
                    if (qEl.ValueKind == JsonValueKind.Number)
                    {
                        if (!qEl.TryGetInt32(out quantity))
                        {
                            // fallback si viene como double
                            var raw = qEl.GetRawText();
                            if (int.TryParse(raw, out var qi)) quantity = qi;
                        }
                    }
                    else if (qEl.ValueKind == JsonValueKind.String)
                    {
                        var s = qEl.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && int.TryParse(s, out var qi))
                        {
                            quantity = qi;
                        }
                    }
                    if (quantity <= 0) quantity = 1;
                }
                if (!string.IsNullOrWhiteSpace(title))
                {
                    items.Add(new MeliOrderItem { Title = title!, SellerSku = sellerSku, Quantity = quantity });
                }
            }
        }

        // buyer.nickname / buyer.first_name
        if (root.TryGetProperty("buyer", out var buyer) && buyer.ValueKind == JsonValueKind.Object)
        {
            if (buyer.TryGetProperty("nickname", out var nick) && nick.ValueKind == JsonValueKind.String)
            {
                buyerNickname = nick.GetString();
            }
            if (buyer.TryGetProperty("first_name", out var fn) && fn.ValueKind == JsonValueKind.String)
            {
                buyerFirstName = fn.GetString();
            }
        }

        string? shippingId = null;
        if (root.TryGetProperty("shipping", out var shipping) && shipping.ValueKind == JsonValueKind.Object)
        {
            if (shipping.TryGetProperty("id", out var sid) && sid.ValueKind == JsonValueKind.Number)
            {
                shippingId = sid.GetRawText();
            }
            else if (shipping.TryGetProperty("id", out var sidStr) && sidStr.ValueKind == JsonValueKind.String)
            {
                shippingId = sidStr.GetString();
            }
        }

        return new MeliOrder
        {
            Items = items,
            ShippingId = shippingId,
            Id = idStr,
            PackId = packId,
            DateCreatedUtc = createdUtc,
            BuyerNickname = buyerNickname,
            BuyerFirstName = buyerFirstName
        };
    }

    public async Task<List<MeliOrder>> GetOrdersByPackAsync(string packId)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"packs/{Uri.EscapeDataString(packId)}");

        using var res = await client.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            // Si el endpoint no está disponible, devolver lista vacía y permitir fallback por el caller
            return [];
        }

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var orderIds = new List<string>();
        if (root.TryGetProperty("orders", out var ordersEl) && ordersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in ordersEl.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var idEl))
                {
                    string? idStr = null;
                    if (idEl.ValueKind == JsonValueKind.Number)
                    {
                        idStr = idEl.GetRawText();
                    }
                    else if (idEl.ValueKind == JsonValueKind.String)
                    {
                        idStr = idEl.GetString();
                    }
                    if (!string.IsNullOrWhiteSpace(idStr))
                    {
                        orderIds.Add(idStr!);
                    }
                }
                else if (entry.ValueKind == JsonValueKind.Number)
                {
                    orderIds.Add(entry.GetRawText());
                }
                else if (entry.ValueKind == JsonValueKind.String)
                {
                    var idStr = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(idStr))
                    {
                        orderIds.Add(idStr!);
                    }
                }
            }
        }

        // Recuperar cada orden con detalles para poder construir asignaciones
        var tasks = orderIds.Select(id => GetOrderAsync(id)).ToArray();
        var orders = await Task.WhenAll(tasks);
        return [.. orders.Where(o => o != null).Select(o => o!)];
    }

    public async Task<bool> UpsertOrderNoteAsync(string orderId, string noteText)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(noteText))
        {
            return false;
        }

        // Evitar redundancia si ya existe alguna nota automática
        try
        {
            var existingNotes = await GetOrderNotesAsync(orderId);
            if (NoteUtils.ContainsAutoNote(existingNotes))
            {
                return false;
            }
        }
        catch
        {
            // Si falla la lectura de notas, continuar y dejar que el POST decida
        }

        var finalNote = NoteUtils.EnsureAutoPrefix(noteText);

        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"orders/{orderId}/notes");
        req.Content = new StringContent(JsonSerializer.Serialize(new { note = finalNote }), System.Text.Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req);

        return res.IsSuccessStatusCode;
    }

    public async Task<bool> HasAutoNoteAsync(string orderId)
    {
        try
        {
            var notes = await GetOrderNotesAsync(orderId);
            return NoteUtils.ContainsAutoNote(notes);
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> GetOrderNotesAsync(string orderId)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"orders/{orderId}/notes");
        using var res = await client.SendAsync(req);

        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
        if (!res.IsSuccessStatusCode)
        {
            res.EnsureSuccessStatusCode();
        }

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var notes = new List<string>();
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in results.EnumerateArray())
                    {
                        if (r.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String)
                        {
                            var value = n.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                notes.Add(value!);
                            }
                        }
                    }
                }
            }
        }

        return notes;
    }

    public async Task<bool> HasTwoOrMoreOrdersByBuyerAsync(
        DateTimeOffset fromDate,
        DateTimeOffset toDate,
        string buyerNickname,
        string sellerId)
    {
        if (string.IsNullOrWhiteSpace(buyerNickname) || string.IsNullOrWhiteSpace(sellerId))
        {
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("meli");

            if (fromDate > toDate)
            {
                (toDate, fromDate) = (fromDate, toDate);
            }

            string fromParam = fromDate.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
            string toParam = toDate.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");

            var uri = "orders/search?seller=" + Uri.EscapeDataString(sellerId)
                      + "&order.date_created.from=" + Uri.EscapeDataString(fromParam)
                      + "&order.date_created.to=" + Uri.EscapeDataString(toParam)
                      + "&q=" + Uri.EscapeDataString(buyerNickname);

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);

            using var res = await client.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                var uniquePedidoKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in results.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;

                    // Filtrar por coincidencia exacta del nickname del comprador
                    string? resultNick = null;
                    if (r.TryGetProperty("buyer", out var buyerEl) && buyerEl.ValueKind == JsonValueKind.Object)
                    {
                        if (buyerEl.TryGetProperty("nickname", out var nickEl) && nickEl.ValueKind == JsonValueKind.String)
                        {
                            resultNick = nickEl.GetString();
                        }
                    }
                    var targetNick = buyerNickname?.Trim();
                    var actualNick = resultNick?.Trim();
                    if (string.IsNullOrWhiteSpace(actualNick) || string.IsNullOrWhiteSpace(targetNick) || !string.Equals(actualNick, targetNick, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? packKey = null;
                    if (r.TryGetProperty("pack_id", out var packEl))
                    {
                        if (packEl.ValueKind == JsonValueKind.Number)
                        {
                            packKey = packEl.GetRawText();
                        }
                        else if (packEl.ValueKind == JsonValueKind.String)
                        {
                            packKey = packEl.GetString();
                        }
                        else if (packEl.ValueKind == JsonValueKind.Null)
                        {
                            packKey = null;
                        }
                    }

                    string? idKey = null;
                    if (r.TryGetProperty("id", out var idEl))
                    {
                        if (idEl.ValueKind == JsonValueKind.Number)
                        {
                            idKey = idEl.GetRawText();
                        }
                        else if (idEl.ValueKind == JsonValueKind.String)
                        {
                            idKey = idEl.GetString();
                        }
                    }

                    var key = packKey ?? idKey;
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        uniquePedidoKeys.Add(key!);
                        if (uniquePedidoKeys.Count >= 2)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Success, string? ResponseBody, int StatusCode)> SendActionGuideMessageAsync(string packIdOrOrderId, string text)
    {
        if (string.IsNullOrWhiteSpace(packIdOrOrderId) || string.IsNullOrWhiteSpace(text))
        {
            return (false, "packIdOrOrderId o texto inválido", 400);
        }

        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"messages/action_guide/packs/{Uri.EscapeDataString(packIdOrOrderId)}/option");
        var body = new { option_id = "OTHER", text = text };
        req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

        using var res = await client.SendAsync(req);
        var content = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode >= 200 && (int)res.StatusCode < 300, content, (int)res.StatusCode);
    }
    public async Task<MeliScanResponse?> ScanItemsAsync(string userId, string? scrollId = null)
    {
        var client = _httpClientFactory.CreateClient("meli");
        var url = $"users/{userId}/items/search?search_type=scan&status=active";
        if (!string.IsNullOrWhiteSpace(scrollId))
        {
            url += $"&scroll_id={Uri.EscapeDataString(scrollId)}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await client.SendAsync(req);

        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<MeliScanResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task<List<MeliItem>> GetItemsAsync(IEnumerable<string> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return [];

        var client = _httpClientFactory.CreateClient("meli");
        var idsParam = string.Join(",", idList);
        var url = $"items?ids={idsParam}&include_attributes=all&attributes=id,shipping,variations,seller_custom_field,attributes,title,price,thumbnail,permalink";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await client.SendAsync(req);

        if (!res.IsSuccessStatusCode) return new List<MeliItem>();

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var items = new List<MeliItem>();
        JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
                {
                    // Check for error in body (multiget can return errors per item)
                    if (body.TryGetProperty("error", out _)) continue;

                    var item = JsonSerializer.Deserialize<MeliItem>(body.GetRawText(), options);
                    if (item != null)
                    {
                        items.Add(item);
                    }
                }
            }
        }
        return items;
    }
    public async Task<(int Quantity, string Version)?> GetUserProductStockAsync(string userProductId)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"user-products/{userProductId}/stock");
        using var res = await client.SendAsync(req);

        if (!res.IsSuccessStatusCode) return null;

        string version = string.Empty;
        if (res.Headers.TryGetValues("x-version", out var values))
        {
            version = values.FirstOrDefault() ?? string.Empty;
        }

        var json = await res.Content.ReadAsStringAsync();
        var stockResponse = JsonSerializer.Deserialize<MeliUserProductStockResponse>(json);

        var quantity = stockResponse?.Locations
            .FirstOrDefault(l => l.Type == "selling_address")?.Quantity ?? 0;

        return (quantity, version);
    }

    public async Task<bool> UpdateUserProductStockAsync(string userProductId, int quantity, string version)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Put, $"user-products/{userProductId}/stock/type/selling_address");

        req.Headers.TryAddWithoutValidation("x-version", version);

        var body = new { quantity };
        req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

        using var res = await client.SendAsync(req);

        if (res.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return false;
        }

        res.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<string?> SearchItemByUserProductIdAsync(string sellerId, string userProductId)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"users/{sellerId}/items/search?user_product_id={userProductId}");
        using var res = await client.SendAsync(req);

        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                return item.GetString(); // Return first match
            }
        }
        return null;
    }

    public async Task<string?> SearchItemBySkuAsync(string sellerId, string sku)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"users/{sellerId}/items/search?sku={Uri.EscapeDataString(sku)}");
        using var res = await client.SendAsync(req);

        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                return item.GetString(); // Return first match
            }
        }
        return null;
    }

    public async Task<List<string>> SearchItemsGeneralAsync(string sellerId, string query)
    {
        var cleanQuery = query.Trim();

        if (Regex.IsMatch(cleanQuery, @"^MLA\d+$", RegexOptions.IgnoreCase))
        {
            return [cleanQuery.ToUpper()];
        }

        var tasks = new List<Task<List<string>>>
        {
            // Búsqueda por Título ("q")
            SearchIdsInternalAsync(sellerId, $"q={Uri.EscapeDataString(cleanQuery)}"),

            // Búsqueda por SKU ("seller_sku")
            SearchIdsInternalAsync(sellerId, $"seller_sku={Uri.EscapeDataString(cleanQuery)}")
        };

        await Task.WhenAll(tasks);

        var allIds = tasks.SelectMany(t => t.Result)
                          .Where(id => !string.IsNullOrWhiteSpace(id))
                          .Distinct()
                          .ToList();

        return allIds;
    }

    private async Task<List<string>> SearchIdsInternalAsync(string sellerId, string queryParam)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"users/{sellerId}/items/search?{queryParam}&status=active");
        using var res = await client.SendAsync(req);

        if (!res.IsSuccessStatusCode) return [];

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            return [.. results.EnumerateArray()
                .Select(item => item.GetString())
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()];
        }

        return [];
    }
}

public class MeliOrder
{
    public List<MeliOrderItem> Items { get; set; } = [];
    public string? ShippingId { get; set; }
    public string? Id { get; set; }
    public string? PackId { get; set; }
    public DateTimeOffset? DateCreatedUtc { get; set; }
    public string? BuyerNickname { get; set; }
    public string? BuyerFirstName { get; set; }
}

public class MeliOrderItem
{
    public string Title { get; set; } = string.Empty;
    public string? SellerSku { get; set; }
    public string? ItemId { get; set; }
    public string? TargetSku { get; set; }
    public int Quantity { get; set; } = 1;
}