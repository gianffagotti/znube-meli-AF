using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

public class ZnubeAllocationService : IZnubeAllocationService
{
    private readonly IZnubeApiClient _znubeApiClient;
    private readonly IOrderItemExpander _orderItemExpander;
    private const string PackDynamicRuleType = "PACK_DYNAMIC";

    public ZnubeAllocationService(IZnubeApiClient znubeApiClient, IOrderItemExpander orderItemExpander)
    {
        _znubeApiClient = znubeApiClient;
        _orderItemExpander = orderItemExpander;
    }

    public async Task<List<ZnubeAllocationEntry>?> GetAllocationsForOrderAsync(MeliOrder order, CancellationToken cancellationToken = default)
    {
        if (order == null || order.Items == null || order.Items.Count == 0)
            return new List<ZnubeAllocationEntry>();

        var resolved = await _orderItemExpander.ExpandItemsAsync(order.Items, cancellationToken);
        if (resolved == null)
            return null;
        var allocations = await BuildAllocationsAsync(resolved, cancellationToken);
        return allocations;
    }

    public async Task<List<ZnubeAllocationEntry>?> GetAllocationsForOrdersAsync(IEnumerable<MeliOrder> orders, CancellationToken cancellationToken = default)
    {
        if (orders == null) return new List<ZnubeAllocationEntry>();
        var orderList = orders.Where(o => o != null && o.Items != null && o.Items.Count > 0).ToList();
        if (orderList.Count == 0) return new List<ZnubeAllocationEntry>();

        var allItems = orderList.SelectMany(o => o.Items!).ToList();
        var resolved = await _orderItemExpander.ExpandItemsAsync(allItems, cancellationToken);
        if (resolved == null)
            return null;
        var allocations = await BuildAllocationsAsync(resolved, cancellationToken);
        return allocations;
    }

    private async Task<List<ZnubeAllocationEntry>> BuildAllocationsAsync(List<OrderItemResolved> resolved, CancellationToken cancellationToken)
    {
        var variantAllocations = resolved
            .Where(r => string.Equals(r.RuleType, PackDynamicRuleType, StringComparison.OrdinalIgnoreCase))
            .Select(r => new ZnubeAllocationEntry
            {
                ProductLabel = r.ProductLabel ?? r.Sku,
                AssignmentName = NoteUtils.VariantAssignment,
                Quantity = r.Quantity
            })
            .ToList();

        var skuRequests = resolved
            .Where(r => !string.Equals(r.RuleType, PackDynamicRuleType, StringComparison.OrdinalIgnoreCase))
            .Select(r => new SkuRequest { Sku = r.Sku, Quantity = r.Quantity, ProductLabel = r.ProductLabel })
            .ToList();

        var allocations = await GetAllocationsForSkusAsync(skuRequests, cancellationToken);
        if (variantAllocations.Count > 0)
            allocations.AddRange(variantAllocations);

        return allocations;
    }

    public async Task<List<ZnubeAllocationEntry>> GetAllocationsForSkusAsync(IEnumerable<SkuRequest> requests, CancellationToken cancellationToken = default)
    {
        var allocations = new List<ZnubeAllocationEntry>();
        if (requests == null) return allocations;

        var requestList = requests
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Sku) && r.Quantity > 0)
            .Select(r => new SkuRequest { Sku = r.Sku.Trim(), Quantity = r.Quantity, ProductLabel = r.ProductLabel })
            .ToList();
        if (requestList.Count == 0) return allocations;

        var skuSet = new HashSet<string>(requestList.Select(r => r.Sku), StringComparer.OrdinalIgnoreCase);

        var lookupTasks = new Dictionary<string, Task<AssignmentLookup>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in skuSet)
            lookupTasks[sku] = TryGetAssignmentBySkuAsync(sku, cancellationToken);

        await Task.WhenAll(lookupTasks.Values);

        var lookupBySku = new Dictionary<string, AssignmentLookup>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in lookupTasks)
        {
            AssignmentLookup value;
            try { value = kvp.Value.Result; }
            catch (Exception ex) { value = new AssignmentLookup { ControlledErrorMessage = $"Fallo consulta SKU {kvp.Key}: {ex.Message}" }; }
            lookupBySku[kvp.Key] = value;
        }

        var availableBySku = new Dictionary<string, List<(string ResourceName, int Available)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in lookupBySku)
        {
            var stocks = new List<(string ResourceName, int Available)>();
            if (entry.Value.ResourceStocks != null)
            {
                foreach (var rs in entry.Value.ResourceStocks)
                {
                    var available = rs.QuantityForSku > 0 ? (int)Math.Floor(rs.QuantityForSku) : 0;
                    if (available > 0) stocks.Add((rs.ResourceName, available));
                }
            }
            availableBySku[entry.Key] = stocks;
        }

        var rrIndexByProduct = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var request in requestList)
        {
            var productLabel = request.ProductLabel ?? request.Sku;
            AssignmentLookup? lookup = null;
            if (lookupBySku.TryGetValue(request.Sku, out var lkp))
            {
                lookup = lkp;
                if (string.IsNullOrWhiteSpace(request.ProductLabel) && !string.IsNullOrWhiteSpace(lookup.TitleFromZnube))
                    productLabel = lookup.TitleFromZnube!;
            }

            int remaining = Math.Max(0, request.Quantity);

            if (remaining > 0 && availableBySku.TryGetValue(request.Sku, out var globalStocks) && globalStocks != null && globalStocks.Count > 0)
            {
                var withIndex = globalStocks.Select((s, idx) => new { s.ResourceName, s.Available, Index = idx }).ToList();
                var depositoFirst = withIndex
                    .OrderByDescending(x => ZnubeLogicExtensions.IsDepositoName(x.ResourceName) && x.Available > 0)
                    .ThenByDescending(x => x.Available)
                    .ThenBy(x => x.Index)
                    .ToList();

                List<(string ResourceName, int Available)> ordered;
                bool depositoConStock = depositoFirst.Any(x => ZnubeLogicExtensions.IsDepositoName(x.ResourceName) && x.Available > 0);
                if (!depositoConStock && lookup != null)
                {
                    var noDeposito = depositoFirst.Where(x => !ZnubeLogicExtensions.IsDepositoName(x.ResourceName) && x.Available > 0).ToList();
                    if (noDeposito.Count >= 2 && !string.IsNullOrWhiteSpace(lookup.ProductId))
                    {
                        rrIndexByProduct.TryGetValue(lookup.ProductId!, out int idx);
                        idx = idx % noDeposito.Count;
                        var rotated = new List<(string ResourceName, int Available)>(noDeposito.Count);
                        rotated.AddRange(noDeposito.Skip(idx).Select(x => (x.ResourceName, x.Available)));
                        rotated.AddRange(noDeposito.Take(idx).Select(x => (x.ResourceName, x.Available)));
                        ordered = rotated;
                        rrIndexByProduct[lookup.ProductId!] = (idx + 1) % noDeposito.Count;
                    }
                    else
                        ordered = depositoFirst.Select(x => (x.ResourceName, x.Available)).ToList();
                }
                else
                    ordered = depositoFirst.Select(x => (x.ResourceName, x.Available)).ToList();

                for (int i = 0; i < ordered.Count && remaining > 0; i++)
                {
                    var resName = ordered[i].ResourceName;
                    for (int g = 0; g < globalStocks.Count && remaining > 0; g++)
                    {
                        if (!string.Equals(globalStocks[g].ResourceName, resName, StringComparison.Ordinal)) continue;
                        var available = globalStocks[g].Available;
                        if (available <= 0) break;
                        var take = Math.Min(remaining, available);
                        if (take > 0)
                        {
                            allocations.Add(new ZnubeAllocationEntry { ProductLabel = productLabel, AssignmentName = resName, Quantity = take });
                            remaining -= take;
                            globalStocks[g] = (globalStocks[g].ResourceName, globalStocks[g].Available - take);
                        }
                        break;
                    }
                }
            }

            if (remaining > 0)
            {
                string assignment = ResolveFallbackAssignment(lookup);
                allocations.Add(new ZnubeAllocationEntry { ProductLabel = productLabel, AssignmentName = assignment, Quantity = remaining });
            }
        }

        return allocations;
    }

    private static string ResolveFallbackAssignment(AssignmentLookup? lookup)
    {
        if (lookup == null) return "Sin asignación";
        if (lookup.SkuNotFound) return "Sin asignación";
        if (lookup.NoStockForSku || lookup.SkuFound) return "Sin stock";
        return !string.IsNullOrWhiteSpace(lookup.Assignment) ? lookup.Assignment! : "Sin asignación";
    }

    private async Task<AssignmentLookup> TryGetAssignmentBySkuAsync(string sellerSku, CancellationToken cancellationToken)
    {
        var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sellerSku);
        var dto = await _znubeApiClient.GetStockBySkuAsync(normalizedSku, cancellationToken);
        if (dto?.Data == null || dto.Data.TotalSku == 0)
            return new AssignmentLookup { ControlledErrorMessage = $"SKU {normalizedSku} no existe", SkuNotFound = true };

        var data = dto.Data;
        var resources = ZnubeLogicExtensions.BuildResourcesMap(data);
        var skuResourceIdsWithQty = ZnubeLogicExtensions.BuildResourceIdsWithQty(data);
        var titleFromZnube = ZnubeLogicExtensions.BuildTitleFromZnube(data, normalizedSku);
        var resourceStocks = BuildResourceStocksForSku(data, normalizedSku, resources);

        OmnichannelStockItem? skuItem = null;
        foreach (var s in data.Stock ?? [])
        {
            if (!string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase))
            {
                skuItem = s;
                break;
            }
        }
        var productIdForSku = skuItem?.ProductId;

        bool skuPresent = (data.Stock ?? []).Any(s => !string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase));

        var assignment = ZnubeLogicExtensions.ResolveAssignmentFromSku(resources, skuResourceIdsWithQty);
        if (!string.IsNullOrWhiteSpace(assignment))
            return new AssignmentLookup { Assignment = assignment, TitleFromZnube = titleFromZnube, ProductId = productIdForSku, ResourceStocks = resourceStocks, SkuFound = true };

        if (!skuPresent)
            return new AssignmentLookup { ControlledErrorMessage = $"SKU {normalizedSku} no existe", TitleFromZnube = titleFromZnube, ProductId = productIdForSku, ResourceStocks = resourceStocks, SkuNotFound = true };

        var productId = ZnubeLogicExtensions.TryGetProductId(data);
        if (!string.IsNullOrWhiteSpace(productId))
        {
            var byProduct = await TryGetAssignmentByProductIdAsync(productId!, skuResourceIdsWithQty, cancellationToken);
            if (!string.IsNullOrWhiteSpace(byProduct))
                return new AssignmentLookup { Assignment = byProduct, TitleFromZnube = titleFromZnube, ProductId = productIdForSku ?? productId, ResourceStocks = resourceStocks, SkuFound = true };
        }

        if (skuResourceIdsWithQty.Count == 0)
            return new AssignmentLookup { ControlledErrorMessage = $"Sin stock para SKU {normalizedSku}", TitleFromZnube = titleFromZnube, ProductId = productIdForSku, ResourceStocks = resourceStocks, NoStockForSku = true, SkuFound = true };

        return new AssignmentLookup { TitleFromZnube = titleFromZnube, ProductId = productIdForSku, ResourceStocks = resourceStocks, SkuFound = skuPresent };
    }

    private async Task<string?> TryGetAssignmentByProductIdAsync(string productId, HashSet<string> allowedResourceIds, CancellationToken cancellationToken)
    {
        var dto = await _znubeApiClient.GetStockByProductIdAsync(productId, cancellationToken);
        if (dto?.Data == null) return null;
        var resources = ZnubeLogicExtensions.BuildResourcesMap(dto.Data);
        return ZnubeLogicExtensions.ResolveAssignmentFromProduct(resources, allowedResourceIds);
    }

    private static List<ResourceSkuStock> BuildResourceStocksForSku(OmnichannelData data, string normalizedSku, Dictionary<string, (string Name, double TotalStock)> resources)
    {
        var resourceStocks = new List<ResourceSkuStock>();
        OmnichannelStockItem? skuItem = null;
        foreach (var s in data.Stock ?? [])
        {
            if (!string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase))
            {
                skuItem = s;
                break;
            }
        }
        if (skuItem != null)
        {
            foreach (var st in skuItem.Stock ?? [])
            {
                if (st.Quantity > 0.0 && !string.IsNullOrWhiteSpace(st.ResourceId) && resources.TryGetValue(st.ResourceId, out var info))
                    resourceStocks.Add(new ResourceSkuStock { ResourceId = st.ResourceId, ResourceName = info.Name ?? string.Empty, QuantityForSku = st.Quantity });
            }
        }
        return resourceStocks;
    }

    private sealed class AssignmentLookup
    {
        public string? Assignment { get; set; }
        public string? ControlledErrorMessage { get; set; }
        public string? TitleFromZnube { get; set; }
        public string? ProductId { get; set; }
        public List<ResourceSkuStock> ResourceStocks { get; set; } = new();
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
}
