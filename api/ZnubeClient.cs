using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace meli_znube_integration.Api
{
    public class ZnubeClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ZnubeClient> _logger;

        public ZnubeClient(IHttpClientFactory httpClientFactory, ILogger<ZnubeClient> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<IEnumerable<string>> GetAssignmentsForOrderAsync(MeliOrder order, CancellationToken cancellationToken = default)
        {
            var results = new List<string>();
            foreach (var item in order.Items)
            {
                string label = "Sin asignación";
                if (!string.IsNullOrWhiteSpace(item.SellerSku))
                {
                    var assignment = await TryGetAssignmentBySkuAsync(item.SellerSku!, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(assignment))
                    {
                        label = assignment!;
                    }
                }
                results.Add($"{item.Title} → {label}");
            }
            return results;
        }

        private async Task<string?> TryGetAssignmentBySkuAsync(string sellerSku, CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("znube");
                using var req = new HttpRequestMessage(HttpMethod.Get, $"Omnichannel/GetStock?sku={Uri.EscapeDataString(sellerSku)}");
                using var res = await client.SendAsync(req, cancellationToken);
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning("ZNube no devolvió éxito para sku {Sku}. Status {Status}", sellerSku, (int)res.StatusCode);
                    return null;
                }

                var body = await res.Content.ReadAsStringAsync(cancellationToken);
                var dto = JsonSerializer.Deserialize<OmnichannelResponse>(body);
                if (dto?.Data == null)
                {
                    return null;
                }

                var resources = BuildResourcesMap(dto.Data);
                var skuResourceIdsWithQty = BuildResourceIdsWithQty(dto.Data);

                var assignment = ResolveAssignmentFromSku(resources, skuResourceIdsWithQty);
                if (!string.IsNullOrWhiteSpace(assignment))
                {
                    return assignment;
                }

                // Si hay ambigüedad, reintentar por productId
                string? productId = TryGetProductId(dto.Data);
                if (!string.IsNullOrWhiteSpace(productId))
                {
                    return await TryGetAssignmentByProductIdAsync(productId!, skuResourceIdsWithQty, cancellationToken);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consultando ZNube para sku {Sku}", sellerSku);
                return null;
            }
        }

        private async Task<string?> TryGetAssignmentByProductIdAsync(string productId, HashSet<string> allowedResourceIds, CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("znube");
                using var req = new HttpRequestMessage(HttpMethod.Get, $"Omnichannel/GetStock?productId={Uri.EscapeDataString(productId)}");
                using var res = await client.SendAsync(req, cancellationToken);
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning("ZNube no devolvió éxito para productId {ProductId}. Status {Status}", productId, (int)res.StatusCode);
                    return null;
                }

                var body = await res.Content.ReadAsStringAsync(cancellationToken);
                var dto = JsonSerializer.Deserialize<OmnichannelResponse>(body);
                if (dto?.Data == null)
                {
                    return null;
                }

                var resources = BuildResourcesMap(dto.Data);
                var assignment = ResolveAssignmentFromProduct(resources, allowedResourceIds);
                return assignment;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consultando ZNube para productId {ProductId}", productId);
                return null;
            }
        }

        private static Dictionary<string, (string Name, double TotalStock)> BuildResourcesMap(OmnichannelData data)
        {
            var resources = new Dictionary<string, (string Name, double TotalStock)>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in data.Resources)
            {
                resources[r.ResourceId] = (r.Name ?? string.Empty, r.TotalStock);
            }
            return resources;
        }

        private static HashSet<string> BuildResourceIdsWithQty(OmnichannelData data)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in data.Stock)
            {
                foreach (var st in s.Stock)
                {
                    if (st.Quantity > 0.0 && !string.IsNullOrWhiteSpace(st.ResourceId))
                    {
                        set.Add(st.ResourceId);
                    }
                }
            }
            return set;
        }

        // Lógica cuando la respuesta es por SKU: solo cuenta stock del SKU
        private static string? ResolveAssignmentFromSku(Dictionary<string, (string Name, double TotalStock)> resources,
                                                       HashSet<string> resourceIdsWithQty)
        {
            // 1) Si hay stock en 'Deposito' para el SKU, asignar 'Deposito'
            string? depositoId = null;
            foreach (var kvp in resources)
            {
                if (IsDepositoName(kvp.Value.Name))
                {
                    depositoId = kvp.Key;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(depositoId) && resourceIdsWithQty.Contains(depositoId))
            {
                return resources.TryGetValue(depositoId!, out var info2) ? info2.Name : "Deposito";
            }

            // 2) Si entre el resto hay solo uno con stock del SKU, devolver ese
            string? unicoId = null;
            int count = 0;
            foreach (var id in resourceIdsWithQty)
            {
                if (!string.IsNullOrWhiteSpace(depositoId) && string.Equals(id, depositoId, StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
                if (count == 1)
                {
                    unicoId = id;
                }
            }

            if (count == 1 && !string.IsNullOrWhiteSpace(unicoId) && resources.TryGetValue(unicoId!, out var info))
            {
                return info.Name;
            }

            // 3) Si hay 0 o más de uno, devolver null para que el caller reintente por productId
            return null;
        }

        // Lógica cuando la respuesta es por ProductId: elegir sucursal con mayor stock total ENTRE las que tenían stock del SKU
        private static string? ResolveAssignmentFromProduct(Dictionary<string, (string Name, double TotalStock)> resources,
                                                           HashSet<string> allowedResourceIds)
        {
            string? bestId = null;
            double bestQty = double.MinValue;
            foreach (var id in allowedResourceIds)
            {
                if (resources.TryGetValue(id, out var info))
                {
                    if (info.TotalStock > bestQty)
                    {
                        bestQty = info.TotalStock;
                        bestId = id;
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(bestId) && resources.TryGetValue(bestId!, out var best))
            {
                return best.Name;
            }
            return null;
        }

        private static string? TryGetProductId(OmnichannelData data)
        {
            foreach (var s in data.Stock)
            {
                if (!string.IsNullOrWhiteSpace(s.ProductId))
                {
                    return s.ProductId;
                }
            }
            return null;
        }

        private static bool IsDepositoName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var normalized = RemoveDiacritics(name).Trim().ToLowerInvariant();
            return normalized == "deposito";
        }

        private static string RemoveDiacritics(string text)
        {
            var formD = text.Normalize(NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(formD.Length);
            foreach (var ch in formD)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static double TryGetDouble(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
            if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
            return 0.0;
        }
    }
}


