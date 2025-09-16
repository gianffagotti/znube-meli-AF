using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace meli_znube_integration.Api;

public class ZnubeClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ZnubeClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IEnumerable<string>> GetAssignmentsForOrderAsync(MeliOrder order)
    {
        var results = new List<string>();
        if (order == null || order.Items == null || order.Items.Count == 0)
        {
            return results;
        }

        // Deduplicar SKUs y resolver en paralelo para acelerar respuesta
        var skuSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in order.Items)
        {
            if (!string.IsNullOrWhiteSpace(it.SellerSku))
            {
                skuSet.Add(it.SellerSku!.Trim());
            }
        }

        var lookupTasks = new Dictionary<string, Task<AssignmentLookup>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in skuSet)
        {
            lookupTasks[sku] = TryGetAssignmentBySkuAsync(sku);
        }

        // Esperar todas las consultas de SKU en paralelo
        await Task.WhenAll(lookupTasks.Values);

        var lookupBySku = new Dictionary<string, AssignmentLookup>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in lookupTasks)
        {
            // Si alguna falla por excepción, devolvemos un objeto con error controlado genérico
            AssignmentLookup value;
            try
            {
                value = kvp.Value.Result;
            }
            catch (Exception ex)
            {
                value = new AssignmentLookup { ControlledErrorMessage = $"Fallo consulta SKU {kvp.Key}: {ex.Message}" };
            }
            lookupBySku[kvp.Key] = value;
        }

        // Componer resultados por ítem manteniendo el orden original
        foreach (var item in order.Items)
        {
            string label = "Sin asignación";
            AssignmentLookup? lookup = null;
            if (!string.IsNullOrWhiteSpace(item.SellerSku) && lookupBySku.TryGetValue(item.SellerSku!, out var lkp))
            {
                lookup = lkp;
                // Construir asignación teniendo en cuenta cantidades parciales por recurso
                if (lookup.ResourceStocks != null && lookup.ResourceStocks.Count > 0 && item.Quantity > 0)
                {
                    int remaining = item.Quantity;
                    var segments = new List<(string ResourceName, int Qty)>();

                    // Priorizar Depósito primero, luego el resto por cantidad descendente
                    var ordered = lookup.ResourceStocks
                        .OrderByDescending(rs => IsDepositoName(rs.ResourceName))
                        .ThenByDescending(rs => rs.QuantityForSku)
                        .ToList();

                    foreach (var rs in ordered)
                    {
                        if (remaining <= 0) break;
                        var available = rs.QuantityForSku > 0 ? (int)Math.Floor(rs.QuantityForSku) : 0;
                        if (available <= 0) continue;
                        var take = Math.Min(remaining, available);
                        if (take > 0)
                        {
                            segments.Add((rs.ResourceName, take));
                            remaining -= take;
                        }
                    }

                    if (segments.Count > 0)
                    {
                        label = string.Join(" + ", segments.Select(s => $"{s.ResourceName} x{s.Qty}"));
                        if (remaining > 0)
                        {
                            // Indicar remanente sin stock asignable
                            label += $" + Sin stock x{remaining}";
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(lookup.ControlledErrorMessage))
                    {
                        // Diferenciar entre SKU inexistente y sin stock
                        if (lookup.SkuNotFound)
                        {
                            label = "Sin asignación";
                        }
                        else if (lookup.NoStockForSku || lookup.SkuFound)
                        {
                            label = "Sin stock";
                        }
                        else
                        {
                            label = $"ERROR: {lookup.ControlledErrorMessage}";
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(lookup.Assignment))
                {
                    // Fallback heredado si no hay detalle por recurso
                    label = lookup.Assignment!;
                }
                else if (!string.IsNullOrWhiteSpace(lookup.ControlledErrorMessage))
                {
                    if (lookup.SkuNotFound)
                    {
                        label = "Sin asignación";
                    }
                    else if (lookup.NoStockForSku || lookup.SkuFound)
                    {
                        label = "Sin stock";
                    }
                    else
                    {
                        label = $"ERROR: {lookup.ControlledErrorMessage}";
                    }
                }
            }
            var titleToShow = !string.IsNullOrWhiteSpace(lookup?.TitleFromZnube)
                ? lookup!.TitleFromZnube!
                : item.Title;
            results.Add($"{titleToShow} → {label}");
        }

        return results;
    }

    public sealed class AllocationEntry
    {
        public string ProductLabel { get; set; } = string.Empty;
        public string AssignmentName { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    public async Task<List<AllocationEntry>> GetAllocationsForOrderAsync(MeliOrder order)
    {
        var allocations = new List<AllocationEntry>();
        if (order == null || order.Items == null || order.Items.Count == 0)
        {
            return allocations;
        }

        // Deduplicar SKUs y resolver en paralelo para acelerar respuesta
        var skuSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in order.Items)
        {
            if (!string.IsNullOrWhiteSpace(it.SellerSku))
            {
                skuSet.Add(it.SellerSku!.Trim());
            }
        }

        var lookupTasks = new Dictionary<string, Task<AssignmentLookup>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in skuSet)
        {
            lookupTasks[sku] = TryGetAssignmentBySkuAsync(sku);
        }

        await Task.WhenAll(lookupTasks.Values);

        var lookupBySku = new Dictionary<string, AssignmentLookup>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in lookupTasks)
        {
            AssignmentLookup value;
            try
            {
                value = kvp.Value.Result;
            }
            catch (Exception ex)
            {
                value = new AssignmentLookup { ControlledErrorMessage = $"Fallo consulta SKU {kvp.Key}: {ex.Message}" };
            }
            lookupBySku[kvp.Key] = value;
        }

        // Asignar por ítem manteniendo orden original, dividiendo cantidad en recursos
        foreach (var item in order.Items)
        {
            if (item == null) continue;
            var productLabel = item.Title;
            AssignmentLookup? lookup = null;
            if (!string.IsNullOrWhiteSpace(item.SellerSku) && lookupBySku.TryGetValue(item.SellerSku!, out var lkp))
            {
                lookup = lkp;
                if (!string.IsNullOrWhiteSpace(lookup.TitleFromZnube))
                {
                    productLabel = lookup.TitleFromZnube!;
                }
            }

            int remaining = Math.Max(0, item.Quantity);

            if (lookup != null && lookup.ResourceStocks != null && lookup.ResourceStocks.Count > 0 && remaining > 0)
            {
                var ordered = lookup.ResourceStocks
                    .OrderByDescending(rs => IsDepositoName(rs.ResourceName))
                    .ThenByDescending(rs => rs.QuantityForSku)
                    .ToList();

                foreach (var rs in ordered)
                {
                    if (remaining <= 0) break;
                    var available = rs.QuantityForSku > 0 ? (int)Math.Floor(rs.QuantityForSku) : 0;
                    if (available <= 0) continue;
                    var take = Math.Min(remaining, available);
                    if (take > 0)
                    {
                        allocations.Add(new AllocationEntry
                        {
                            ProductLabel = productLabel,
                            AssignmentName = rs.ResourceName,
                            Quantity = take
                        });
                        remaining -= take;
                    }
                }
            }

            if (remaining > 0)
            {
                // Fallback: distinguir sin asignación (SKU no existe) vs sin stock (SKU existe)
                string assignment;
                if (lookup != null)
                {
                    if (lookup.SkuNotFound)
                    {
                        assignment = "Sin asignación";
                    }
                    else if (lookup.NoStockForSku || lookup.SkuFound)
                    {
                        assignment = "Sin stock";
                    }
                    else if (!string.IsNullOrWhiteSpace(lookup.Assignment))
                    {
                        assignment = lookup.Assignment!;
                    }
                    else
                    {
                        assignment = "Sin asignación";
                    }
                }
                else
                {
                    assignment = "Sin asignación";
                }
                allocations.Add(new AllocationEntry
                {
                    ProductLabel = productLabel,
                    AssignmentName = assignment,
                    Quantity = remaining
                });
            }
        }

        return allocations;
    }

    private sealed class AssignmentLookup
    {
        public string? Assignment { get; set; }
        public string? ControlledErrorMessage { get; set; }
        public string? TitleFromZnube { get; set; }
        public List<ResourceSkuStock> ResourceStocks { get; set; } = new List<ResourceSkuStock>();
        public bool SkuNotFound { get; set; }
        public bool NoStockForSku { get; set; }
        public bool SkuFound { get; set; }
    }

    private sealed class ResourceSkuStock
    {
        public string ResourceId { get; set; } = string.Empty;
        public string ResourceName { get; set; } = string.Empty;
        public double QuantityForSku { get; set; }
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

    private async Task<AssignmentLookup> TryGetAssignmentBySkuAsync(string sellerSku)
    {
        var normalizedSku = NormalizeSellerSku(sellerSku);
        var client = _httpClientFactory.CreateClient("znube");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"Omnichannel/GetStock?sku={Uri.EscapeDataString(normalizedSku)}");
        using var res = await client.SendAsync(req);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new AssignmentLookup { ControlledErrorMessage = $"SKU {normalizedSku} no existe", SkuNotFound = true };
        }
        if (!res.IsSuccessStatusCode)
        {
            res.EnsureSuccessStatusCode();
        }

        var body = await res.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<OmnichannelResponse>(body);
        if (dto?.Data == null || dto?.Data.TotalSku == 0)
        {
            return new AssignmentLookup { ControlledErrorMessage = $"SKU {normalizedSku} no existe", SkuNotFound = true };
        }

        var resources = BuildResourcesMap(dto!.Data);
        var skuResourceIdsWithQty = BuildResourceIdsWithQty(dto.Data);
        var titleFromZnube = BuildTitleFromZnube(dto.Data, normalizedSku);

        // Detalle por recurso del SKU para poder asignar cantidades parciales
        var resourceStocks = new List<ResourceSkuStock>();
        OmnichannelStockItem? skuItem = null;
        foreach (var s in dto.Data.Stock)
        {
            if (!string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase))
            {
                skuItem = s;
                break;
            }
        }
        if (skuItem != null)
        {
            foreach (var st in skuItem.Stock)
            {
                if (st.Quantity > 0.0 && !string.IsNullOrWhiteSpace(st.ResourceId))
                {
                    if (resources.TryGetValue(st.ResourceId, out var info))
                    {
                        resourceStocks.Add(new ResourceSkuStock
                        {
                            ResourceId = st.ResourceId,
                            ResourceName = info.Name ?? string.Empty,
                            QuantityForSku = st.Quantity
                        });
                    }
                }
            }
        }

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
            return new AssignmentLookup { Assignment = assignment, TitleFromZnube = titleFromZnube, ResourceStocks = resourceStocks, SkuFound = true };
        }

        if (!skuPresent)
        {
            return new AssignmentLookup { ControlledErrorMessage = $"SKU {normalizedSku} no existe", TitleFromZnube = titleFromZnube, ResourceStocks = resourceStocks, SkuNotFound = true };
        }

        // Si hay ambigüedad, reintentar por productId
        string? productId = TryGetProductId(dto.Data);
        if (!string.IsNullOrWhiteSpace(productId))
        {
            var byProduct = await TryGetAssignmentByProductIdAsync(productId!, skuResourceIdsWithQty);
            if (!string.IsNullOrWhiteSpace(byProduct))
            {
                return new AssignmentLookup { Assignment = byProduct, TitleFromZnube = titleFromZnube, ResourceStocks = resourceStocks, SkuFound = true };
            }
        }

        // Si no hay stock en ningún recurso para el SKU
        if (skuResourceIdsWithQty.Count == 0)
        {
            return new AssignmentLookup { ControlledErrorMessage = $"Sin stock para SKU {normalizedSku}", TitleFromZnube = titleFromZnube, ResourceStocks = resourceStocks, NoStockForSku = true, SkuFound = true };
        }

        return new AssignmentLookup { TitleFromZnube = titleFromZnube, ResourceStocks = resourceStocks, SkuFound = skuPresent };
    }

    private async Task<string?> TryGetAssignmentByProductIdAsync(string productId, HashSet<string> allowedResourceIds)
    {
        var client = _httpClientFactory.CreateClient("znube");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"Omnichannel/GetStock?productId={Uri.EscapeDataString(productId)}");
        using var res = await client.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            res.EnsureSuccessStatusCode();
        }

        var body = await res.Content.ReadAsStringAsync();
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
                    if (string.Equals(vt.TypeName, "TALLE", StringComparison.OrdinalIgnoreCase))
                    {
                        label = v.VariantId;
                    }
                    else if (!string.IsNullOrWhiteSpace(v.VariantId) && vt.Names != null && vt.Names.TryGetValue(v.VariantId, out var nameVal))
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


