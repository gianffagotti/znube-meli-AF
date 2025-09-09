using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace meli_znube_integration.Api
{
    public class ZnubeClient
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public ZnubeClient(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IEnumerable<string>> GetAssignmentsForOrderAsync(MeliOrder order, CancellationToken cancellationToken = default)
        {
            var results = new List<string>();
            foreach (var item in order.Items)
            {
                string label = "Sin asignación";
                AssignmentLookup? lookup = null;
                if (!string.IsNullOrWhiteSpace(item.SellerSku))
                {
                    lookup = await TryGetAssignmentBySkuAsync(item.SellerSku!, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(lookup.Assignment))
                    {
                        label = lookup.Assignment!;
                    }
                    else if (!string.IsNullOrWhiteSpace(lookup.ControlledErrorMessage))
                    {
                        label = $"ERROR: {lookup.ControlledErrorMessage}";
                    }
                }
                var titleToShow = !string.IsNullOrWhiteSpace(lookup?.TitleFromZnube)
                    ? lookup!.TitleFromZnube!
                    : (!string.IsNullOrWhiteSpace(item.SellerSku) ? item.SellerSku! : "SIN SKU");
                results.Add($"{titleToShow} → {label}");
            }
            return results;
        }

        private sealed class AssignmentLookup
        {
            public string? Assignment { get; set; }
            public string? ControlledErrorMessage { get; set; }
            public string? TitleFromZnube { get; set; }
        }

        private static string NormalizeSellerSku(string sellerSku)
        {
            if (string.IsNullOrWhiteSpace(sellerSku)) return sellerSku;
            var s = sellerSku.Trim();
            if (s.Contains("#")) return s;

            if (s.Contains("!"))
            {
                var replaced = s.Replace('!', '#');
                while (replaced.Contains("##"))
                {
                    replaced = replaced.Replace("##", "#");
                }
                return replaced;
            }

            if (s.IndexOf("HST", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var parts = Regex.Split(s, "HST", RegexOptions.IgnoreCase)
                                  .Select(p => p.Trim())
                                  .Where(p => !string.IsNullOrWhiteSpace(p))
                                  .ToArray();
                if (parts.Length >= 2)
                {
                    return string.Join('#', parts);
                }
            }

            return s;
        }

        private async Task<AssignmentLookup> TryGetAssignmentBySkuAsync(string sellerSku, CancellationToken cancellationToken)
        {
            var normalizedSku = NormalizeSellerSku(sellerSku);
            var client = _httpClientFactory.CreateClient("znube");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"Omnichannel/GetStock?sku={Uri.EscapeDataString(normalizedSku)}");
            using var res = await client.SendAsync(req, cancellationToken);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new AssignmentLookup { ControlledErrorMessage = $"SKU {normalizedSku} no existe" };
            }
            if (!res.IsSuccessStatusCode)
            {
                res.EnsureSuccessStatusCode();
            }

            var body = await res.Content.ReadAsStringAsync(cancellationToken);
            var dto = JsonSerializer.Deserialize<OmnichannelResponse>(body);
            if (dto?.Data == null || dto?.Data.TotalSku == 0)
            {
                return new AssignmentLookup { ControlledErrorMessage = $"SKU {normalizedSku} no existe" };
            }

            var resources = BuildResourcesMap(dto.Data);
            var skuResourceIdsWithQty = BuildResourceIdsWithQty(dto.Data);
            var titleFromZnube = BuildTitleFromZnube(dto.Data, normalizedSku);

            // Detectar si el SKU no existe en la respuesta
            bool skuPresent = false;
            foreach (var s in dto.Data.Stock)
            {
                if (!string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase))
                {
                    skuPresent = true;
                    break;
                }
            }

            var assignment = ResolveAssignmentFromSku(resources, skuResourceIdsWithQty);
            if (!string.IsNullOrWhiteSpace(assignment))
            {
                return new AssignmentLookup { Assignment = assignment, TitleFromZnube = titleFromZnube };
            }

            if (!skuPresent)
            {
                return new AssignmentLookup { ControlledErrorMessage = $"SKU {normalizedSku} no existe", TitleFromZnube = titleFromZnube };
            }

            // Si hay ambigüedad, reintentar por productId
            string? productId = TryGetProductId(dto.Data);
            if (!string.IsNullOrWhiteSpace(productId))
            {
                var byProduct = await TryGetAssignmentByProductIdAsync(productId!, skuResourceIdsWithQty, cancellationToken);
                if (!string.IsNullOrWhiteSpace(byProduct))
                {
                    return new AssignmentLookup { Assignment = byProduct, TitleFromZnube = titleFromZnube };
                }
            }

            // Si no hay stock en ningún recurso para el SKU
            if (skuResourceIdsWithQty.Count == 0)
            {
                return new AssignmentLookup { ControlledErrorMessage = $"Sin stock para SKU {normalizedSku}", TitleFromZnube = titleFromZnube };
            }

            return new AssignmentLookup { TitleFromZnube = titleFromZnube };
        }

        private async Task<string?> TryGetAssignmentByProductIdAsync(string productId, HashSet<string> allowedResourceIds, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient("znube");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"Omnichannel/GetStock?productId={Uri.EscapeDataString(productId)}");
            using var res = await client.SendAsync(req, cancellationToken);
            if (!res.IsSuccessStatusCode)
            {
                res.EnsureSuccessStatusCode();
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

        private static Dictionary<string, (string Name, double TotalStock)> BuildResourcesMap(OmnichannelData data)
        {
            var resources = new Dictionary<string, (string Name, double TotalStock)>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in data.Resources)
            {
                resources[r.ResourceId] = (r.Name ?? string.Empty, r.TotalStock ?? 0);
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

        private static string? BuildTitleFromZnube(OmnichannelData data, string sellerSku)
        {
            if (data == null || string.IsNullOrWhiteSpace(sellerSku))
            {
                return null;
            }

            OmnichannelStockItem? target = null;
            foreach (var s in data.Stock)
            {
                if (!string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, sellerSku, StringComparison.OrdinalIgnoreCase))
                {
                    target = s;
                    break;
                }
            }

            if (target == null)
            {
                return null;
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(target.ProductId))
            {
                parts.Add(target.ProductId!);
            }

            foreach (var v in target.Variants)
            {
                string? label = null;
                foreach (var vt in data.Variants)
                {
                    if (!string.IsNullOrWhiteSpace(vt.TypeName) && string.Equals(vt.TypeName, v.VariantType, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(v.VariantId) && vt.Names != null && vt.Names.TryGetValue(v.VariantId, out var nameVal))
                        {
                            label = nameVal;
                        }
                        break;
                    }
                }
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = v.VariantId;
                }
                if (!string.IsNullOrWhiteSpace(label))
                {
                    parts.Add(label!);
                }
            }

            if (parts.Count == 0)
            {
                return null;
            }
            return string.Join(" ", parts);
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


