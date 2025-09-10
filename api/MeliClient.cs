using System;
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

    public async Task<bool> IsFullShipmentAsync(MeliOrder order, string accessToken)
    {
        if (order == null || string.IsNullOrWhiteSpace(order.ShippingId))
        {
            return false;
        }

        var client = _httpClientFactory.CreateClient("meli");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"shipments/{order.ShippingId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("x-format-new", "true");

        using var res = await client.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? logisticType = null;
        if (root.TryGetProperty("logistic", out var logistic) && logistic.ValueKind == JsonValueKind.Object)
        {
            if (logistic.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            {
                logisticType = typeEl.GetString();
            }
        }

        var envFull = Environment.GetEnvironmentVariable("MELI_LOGISTIC_TYPE_FULL");
        bool isFull = !string.IsNullOrWhiteSpace(logisticType)
            && (
                (!string.IsNullOrWhiteSpace(envFull) && string.Equals(logisticType, envFull, StringComparison.OrdinalIgnoreCase))
                || string.Equals(logisticType, "full", StringComparison.OrdinalIgnoreCase)
                || string.Equals(logisticType, "fulfillment", StringComparison.OrdinalIgnoreCase)
            );

        return isFull;
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
                string? stateNew = null;
                if (newAddr.TryGetProperty("city", out var cityObjNew) && cityObjNew.ValueKind == JsonValueKind.Object && cityObjNew.TryGetProperty("name", out var cityNameNew) && cityNameNew.ValueKind == JsonValueKind.String)
                {
                    cityNew = cityNameNew.GetString();
                }
                if (newAddr.TryGetProperty("state", out var stateObjNew) && stateObjNew.ValueKind == JsonValueKind.Object && stateObjNew.TryGetProperty("name", out var stateNameNew) && stateNameNew.ValueKind == JsonValueKind.String)
                {
                    stateNew = stateNameNew.GetString();
                }
                if (!string.IsNullOrWhiteSpace(cityNew) && !string.IsNullOrWhiteSpace(stateNew))
                {
                    zone = $"{cityNew}, {stateNew}";
                }
                else if (!string.IsNullOrWhiteSpace(cityNew))
                {
                    zone = cityNew;
                }
                else if (!string.IsNullOrWhiteSpace(stateNew))
                {
                    zone = stateNew;
                }
            }
        }
        if (zone == null)
        {
            if (root.TryGetProperty("receiver_address", out var addr) && addr.ValueKind == JsonValueKind.Object)
            {
                string? city = null;
                string? state = null;
                if (addr.TryGetProperty("city", out var cityObj) && cityObj.ValueKind == JsonValueKind.Object && cityObj.TryGetProperty("name", out var cityName) && cityName.ValueKind == JsonValueKind.String)
                {
                    city = cityName.GetString();
                }
                if (addr.TryGetProperty("state", out var stateObj) && stateObj.ValueKind == JsonValueKind.Object && stateObj.TryGetProperty("name", out var stateName) && stateName.ValueKind == JsonValueKind.String)
                {
                    state = stateName.GetString();
                }
                if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(state))
                {
                    zone = $"{city}, {state}";
                }
                else if (!string.IsNullOrWhiteSpace(city))
                {
                    zone = city;
                }
                else if (!string.IsNullOrWhiteSpace(state))
                {
                    zone = state;
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
        if (root.TryGetProperty("order_items", out var orderItems) && orderItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var oi in orderItems.EnumerateArray())
            {
                string? title = null;
                string? sellerSku = null;
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
                if (!string.IsNullOrWhiteSpace(title))
                {
                    items.Add(new MeliOrderItem { Title = title!, SellerSku = sellerSku });
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
            ShippingId = shippingId
        };
    }

    public async Task<string?> TryGetBuyerZoneAsync(MeliOrder order, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(order.ShippingId))
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
        // Determinar tipo de envío (logistic.type) y actuar según reglas
        static bool IsMatch(string? value, string? expected)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(expected)) return false;
            return string.Equals(value.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        static bool IsAny(string? value, params string[] candidates)
        {
            if (string.IsNullOrWhiteSpace(value) || candidates == null || candidates.Length == 0) return false;
            var v = value.Trim();
            foreach (var c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c) && string.Equals(v, c.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

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
        var envRetiro = Environment.GetEnvironmentVariable("MELI_LOGISTIC_TYPE_RETIRO");

        bool isFull = IsMatch(logisticType, envFull) || IsAny(logisticType, "full", "fulfillment");
        bool isFlex = IsMatch(logisticType, envFlex) || IsAny(logisticType, "flex", "self_service");
        bool isRetiro = IsMatch(logisticType, envRetiro) || IsAny(logisticType, "pickup", "retiro", "drop_off");

        // Si es FULL o cualquier otro distinto de FLEX, no agregamos zona
        if (isFull)
        {
            return null;
        }
        if (!isFlex)
        {
            // Incluye RETIRO u otros tipos no FLEX
            return null;
        }

        // Nuevo formato: destination.shipping_address.city/state
        if (root.TryGetProperty("destination", out var destination) && destination.ValueKind == JsonValueKind.Object)
        {
            if (destination.TryGetProperty("shipping_address", out var newAddr) && newAddr.ValueKind == JsonValueKind.Object)
            {
                string? cityNew = null;
                string? stateNew = null;
                if (newAddr.TryGetProperty("city", out var cityObjNew) && cityObjNew.ValueKind == JsonValueKind.Object && cityObjNew.TryGetProperty("name", out var cityNameNew) && cityNameNew.ValueKind == JsonValueKind.String)
                {
                    cityNew = cityNameNew.GetString();
                }
                if (newAddr.TryGetProperty("state", out var stateObjNew) && stateObjNew.ValueKind == JsonValueKind.Object && stateObjNew.TryGetProperty("name", out var stateNameNew) && stateNameNew.ValueKind == JsonValueKind.String)
                {
                    stateNew = stateNameNew.GetString();
                }

                if (!string.IsNullOrWhiteSpace(cityNew) && !string.IsNullOrWhiteSpace(stateNew))
                {
                    return $"{cityNew}, {stateNew}";
                }
                if (!string.IsNullOrWhiteSpace(cityNew))
                {
                    return cityNew;
                }
                if (!string.IsNullOrWhiteSpace(stateNew))
                {
                    return stateNew;
                }
            }
        }
        if (root.TryGetProperty("receiver_address", out var addr) && addr.ValueKind == JsonValueKind.Object)
        {
            string? city = null;
            string? state = null;
            if (addr.TryGetProperty("city", out var cityObj) && cityObj.ValueKind == JsonValueKind.Object && cityObj.TryGetProperty("name", out var cityName) && cityName.ValueKind == JsonValueKind.String)
            {
                city = cityName.GetString();
            }
            if (addr.TryGetProperty("state", out var stateObj) && stateObj.ValueKind == JsonValueKind.Object && stateObj.TryGetProperty("name", out var stateName) && stateName.ValueKind == JsonValueKind.String)
            {
                state = stateName.GetString();
            }

            if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(state))
            {
                return $"{city}, {state}";
            }
            if (!string.IsNullOrWhiteSpace(city))
            {
                return city;
            }
            if (!string.IsNullOrWhiteSpace(state))
            {
                return state;
            }
        }

        return null;
    }

    public async Task UpsertOrderNoteAsync(string orderId, string noteText, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(noteText))
        {
            return;
        }

        const string autoPrefix = "[AUTO] ";

        // Evitar redundancia si ya existe alguna nota automática
        try
        {
            var existingNotes = await GetOrderNotesAsync(orderId, accessToken);
            if (existingNotes.Any(n => !string.IsNullOrWhiteSpace(n) && n!.StartsWith(autoPrefix, StringComparison.Ordinal)))
            {
                return;
            }
        }
        catch
        {
            // Si falla la lectura de notas, continuar y dejar que el POST decida
        }

        var finalNote = noteText.StartsWith(autoPrefix, StringComparison.Ordinal) ? noteText : $"{autoPrefix}{noteText}";

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
}

public class MeliOrderItem
{
    public string Title { get; set; } = string.Empty;
    public string? SellerSku { get; set; }
}


