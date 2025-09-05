using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Api
{
    public class MeliClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MeliClient> _logger;

        public MeliClient(IHttpClientFactory httpClientFactory, ILogger<MeliClient> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<MeliOrder?> GetOrderAsync(string orderId, string accessToken)
        {
            var client = _httpClientFactory.CreateClient("meli");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"orders/{orderId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var res = await client.SendAsync(req);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Orden {OrderId} no encontrada en MELI.", orderId);
                return null;
            }
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync();
                _logger.LogError("Error recuperando orden {OrderId}. Status {Status}. Body: {Body}", orderId, (int)res.StatusCode, body);
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
                    if (oi.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                    {
                        if (item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                        {
                            title = t.GetString();
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        items.Add(new MeliOrderItem { Title = title! });
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

            using var res = await client.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("No se pudo obtener shipment {ShipmentId}. Status {Status}", order.ShippingId, (int)res.StatusCode);
                return null;
            }

            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
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
            var client = _httpClientFactory.CreateClient("meli");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"orders/{orderId}/notes");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = new StringContent(JsonSerializer.Serialize(new { note = noteText }), System.Text.Encoding.UTF8, "application/json");
            using var res = await client.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync();
                _logger.LogWarning("No se pudo upsert note para orden {OrderId}. Status {Status}. Body: {Body}", orderId, (int)res.StatusCode, body);
            }
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
    }
}


