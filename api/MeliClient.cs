using System.Net.Http.Headers;
using System.Text.Json;

namespace meli_znube_integration.Api;

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

    public async Task<MeliShipmentInfo?> GetShipmentInfoAsync(MeliOrder order, string accessToken)
    {
        if (order == null || string.IsNullOrWhiteSpace(order.ShippingId))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"shipments/{order.ShippingId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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

        var envFlex = Environment.GetEnvironmentVariable("MELI_LOGISTIC_TYPE_FLEX");
        var envFull = Environment.GetEnvironmentVariable("MELI_LOGISTIC_TYPE_FULL");

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
    public async Task<MeliOrder?> GetOrderAsync(string orderId, string accessToken)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"orders/{orderId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

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
            DateCreatedUtc = createdUtc
        };
    }

    public async Task<List<MeliOrder>> GetOrdersByPackAsync(string packId, string accessToken)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"packs/{Uri.EscapeDataString(packId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var res = await client.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            // Si el endpoint no está disponible, devolver lista vacía y permitir fallback por el caller
            return new List<MeliOrder>();
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
        var tasks = orderIds.Select(id => GetOrderAsync(id, accessToken)).ToArray();
        var orders = await Task.WhenAll(tasks);
        return orders.Where(o => o != null).Select(o => o!).ToList();
    }

    

    

    public async Task UpsertOrderNoteAsync(string orderId, string noteText, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(noteText))
        {
            return;
        }

        // Evitar redundancia si ya existe alguna nota automática
        try
        {
            var existingNotes = await GetOrderNotesAsync(orderId, accessToken);
            if (NoteUtils.ContainsAutoNote(existingNotes))
            {
                return;
            }
        }
        catch
        {
            // Si falla la lectura de notas, continuar y dejar que el POST decida
        }

        var finalNote = NoteUtils.EnsureAutoPrefix(noteText);

        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"orders/{orderId}/notes");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(JsonSerializer.Serialize(new { note = finalNote }), System.Text.Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            // Permitir que el caller decida cómo manejar el fallo sin loguear aquí
        }
    }

    public async Task<bool> HasAutoNoteAsync(string orderId, string accessToken)
    {
        try
        {
            var notes = await GetOrderNotesAsync(orderId, accessToken);
            return NoteUtils.ContainsAutoNote(notes);
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> GetOrderNotesAsync(string orderId, string accessToken)
    {
        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"orders/{orderId}/notes");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await client.SendAsync(req);

        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new List<string>();
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
}

public class MeliOrder
{
    public List<MeliOrderItem> Items { get; set; } = new List<MeliOrderItem>();
    public string? ShippingId { get; set; }
    public string? Id { get; set; }
    public string? PackId { get; set; }
    public DateTimeOffset? DateCreatedUtc { get; set; }
}

public class MeliOrderItem
{
    public string Title { get; set; } = string.Empty;
    public string? SellerSku { get; set; }
    public int Quantity { get; set; } = 1;
}


