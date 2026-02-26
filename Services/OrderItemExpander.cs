using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

public class OrderItemExpander : IOrderItemExpander
{
    private readonly IZnubeApiClient _znubeApiClient;
    private readonly IMeliApiClient _meliApiClient;
    private readonly IOrderItemRuleResolver _ruleResolver;
    private readonly ILogger<OrderItemExpander> _logger;

    public OrderItemExpander(
        IZnubeApiClient znubeApiClient,
        IMeliApiClient meliApiClient,
        IOrderItemRuleResolver ruleResolver,
        ILogger<OrderItemExpander> logger)
    {
        _znubeApiClient = znubeApiClient;
        _meliApiClient = meliApiClient;
        _ruleResolver = ruleResolver;
        _logger = logger;
    }

    public async Task<List<OrderItemResolved>?> ExpandItemsAsync(IEnumerable<MeliOrderItem> items, CancellationToken cancellationToken = default)
    {
        var resolved = new List<OrderItemResolved>();
        if (items == null) return resolved;

        var znubeSkuCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (item == null) continue;
            var qty = item.Quantity <= 0 ? 1 : item.Quantity;
            var sku = item.SellerSku?.Trim();
            StockRuleDto? rule = null;

            if (!string.IsNullOrWhiteSpace(item.ItemId))
            {
                rule = await _ruleResolver.GetRuleAsync(item.ItemId!, cancellationToken);
            }

            if ((rule is null ||
                string.Equals(rule.RuleType, StockRuleTypes.Full, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(sku))
            {
                if (!znubeSkuCache.TryGetValue(sku, out var skuExists))
                {
                    var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sku);
                    var stock = await _znubeApiClient.GetStockBySkuAsync(normalizedSku, cancellationToken);
                    skuExists = stock?.Data != null && stock.Data.TotalSku > 0;
                    znubeSkuCache[sku] = skuExists;
                }

                if (skuExists)
                {
                    resolved.Add(new OrderItemResolved
                    {
                        Sku = sku,
                        Quantity = qty,
                        ProductLabel = item.Title,
                        OrderItemId = item.ItemId,
                        RuleType = StockRuleTypes.Full
                    });
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(item.ItemId))
            {
                _logger.LogWarning("Orden sin ItemId para resolver regla. Cancelando nota.");
                return null;
            }

            if (rule == null)
            {
                if (!string.IsNullOrWhiteSpace(sku))
                {
                    _logger.LogInformation("No hay regla para ItemId {ItemId}. Marcando item sin asignacion.", item.ItemId);
                    resolved.Add(new OrderItemResolved
                    {
                        Sku = sku,
                        Quantity = qty,
                        ProductLabel = item.Title,
                        OrderItemId = item.ItemId,
                        RuleType = StockRuleTypes.Full
                    });
                    continue;
                }

                _logger.LogInformation("No hay regla para ItemId {ItemId}. Cancelando nota.", item.ItemId);
                return null;
            }

            if (string.Equals(rule.RuleType, StockRuleTypes.Pack, StringComparison.OrdinalIgnoreCase))
            {
                var mapping = ResolveMapping(rule, item);
                if (mapping == null)
                {
                    _logger.LogInformation("No hay mapping para ItemId {ItemId}. Marcando item sin asignacion.", item.ItemId);
                    resolved.Add(new OrderItemResolved
                    {
                        Sku = sku ?? "",
                        Quantity = qty,
                        ProductLabel = item.Title,
                        OrderItemId = item.ItemId,
                        RuleType = StockRuleTypes.Full
                    });
                    continue;
                }

                if (string.Equals(mapping.Strategy, "Dynamic_Size", StringComparison.OrdinalIgnoreCase))
                {
                    var dynamicResolved = await BuildDynamicSizeResolvedAsync(rule, mapping, item, qty, cancellationToken);
                    if (dynamicResolved == null || dynamicResolved.Count == 0)
                        return null;
                    resolved.AddRange(dynamicResolved);
                    continue;
                }
                if (!string.Equals(mapping.Strategy, "Explicit", StringComparison.OrdinalIgnoreCase))
                    return null;

                if (mapping.SourceMatches == null || mapping.SourceMatches.Count != 1)
                    return null;

                var source = mapping.SourceMatches[0];
                if (string.IsNullOrWhiteSpace(source.SourceSku))
                    return null;

                var packQty = source.Quantity > 0 ? source.Quantity : 1;
                resolved.Add(new OrderItemResolved
                {
                    Sku = source.SourceSku,
                    Quantity = qty * packQty,
                    ProductLabel = item.Title,
                    OrderItemId = item.ItemId,
                    RuleType = StockRuleTypes.Pack,
                    SourceItemId = source.SourceItemId
                });
                continue;
            }

            if (string.Equals(rule.RuleType, StockRuleTypes.Combo, StringComparison.OrdinalIgnoreCase))
            {
                var mapping = ResolveMapping(rule, item);
                List<RuleSourceMatchDto>? matches = null;
                if (mapping != null && mapping.SourceMatches != null && mapping.SourceMatches.Count > 0)
                    matches = mapping.SourceMatches;
                else
                    matches = await BuildComboFallbackMatchesAsync(rule, cancellationToken);

                if (matches == null || matches.Count == 0)
                {
                    _logger.LogInformation("No hay matches para ItemId {ItemId}. Marcando item sin asignacion.", item.ItemId);
                    resolved.Add(new OrderItemResolved
                    {
                        Sku = sku ?? "",
                        Quantity = qty,
                        ProductLabel = item.Title,
                        OrderItemId = item.ItemId,
                        RuleType = StockRuleTypes.Full
                    });
                    continue;
                }

                foreach (var match in matches)
                {
                    if (string.IsNullOrWhiteSpace(match.SourceSku))
                        return null;
                    var comboQty = match.Quantity > 0 ? match.Quantity : 1;
                    resolved.Add(new OrderItemResolved
                    {
                        Sku = match.SourceSku,
                        Quantity = qty * comboQty,
                        ProductLabel = item.Title,
                        OrderItemId = item.ItemId,
                        RuleType = StockRuleTypes.Combo,
                        SourceItemId = match.SourceItemId
                    });
                }
                continue;
            }

            return null;
        }

        return resolved;
    }

    private async Task<List<OrderItemResolved>?> BuildDynamicSizeResolvedAsync(
        StockRuleDto rule,
        VariantMappingDto mapping,
        MeliOrderItem item,
        int orderQty,
        CancellationToken cancellationToken)
    {
        var targetSize = ExtractSizeFromSku(item.SellerSku) ?? mapping.MatchSize;
        if (string.IsNullOrWhiteSpace(targetSize))
            return null;

        var packQty = mapping.PackQuantity ?? rule.DefaultPackQuantity;
        if (packQty <= 0) packQty = 1;
        var totalUnits = Math.Max(1, packQty * Math.Max(1, orderQty));

        var sourceItemId = ResolveSourceItemId(rule, mapping);
        if (string.IsNullOrWhiteSpace(sourceItemId))
            return null;

        var sellerId = EnvVars.GetString(EnvVars.Keys.MeliSellerId);
        if (string.IsNullOrWhiteSpace(sellerId))
            return null;

        var resolvedSourceItemId = await ResolveItemIdAsync(sourceItemId!, sellerId, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedSourceItemId))
            return null;

        var sourceItems = await _meliApiClient.GetItemsAsync(new[] { resolvedSourceItemId! }, cancellationToken);
        var sourceItem = sourceItems?.FirstOrDefault();
        if (sourceItem == null)
            return null;

        var seedSku = sourceItem.Variations.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.SellerSku))?.SellerSku
                      ?? sourceItem.SellerSku;
        if (string.IsNullOrWhiteSpace(seedSku))
            return null;

        var productId = ZnubeLogicExtensions.GetProductIdFromSku(ZnubeLogicExtensions.NormalizeSellerSku(seedSku));
        if (string.IsNullOrWhiteSpace(productId))
            return null;

        var stock = await _znubeApiClient.GetStockByProductIdAsync(productId, cancellationToken);
        if (stock?.Data == null || stock.Data.Stock == null || stock.Data.Stock.Count == 0)
            return null;

        var resources = ZnubeLogicExtensions.BuildResourcesMap(stock.Data);

        var variants = new List<VariantStockInfo>();
        foreach (var s in stock.Data.Stock)
        {
            if (string.IsNullOrWhiteSpace(s.Sku)) continue;
            var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(s.Sku);
            var size = ExtractSizeFromSku(normalizedSku);
            if (string.IsNullOrWhiteSpace(size) || !string.Equals(size, targetSize, StringComparison.OrdinalIgnoreCase))
                continue;

            int depositoQty = 0;
            int otherQty = 0;
            foreach (var st in s.Stock ?? [])
            {
                if (st.Quantity <= 0.0 || string.IsNullOrWhiteSpace(st.ResourceId)) continue;
                if (resources.TryGetValue(st.ResourceId, out var res))
                {
                    var qty = (int)Math.Floor(st.Quantity);
                    if (qty <= 0) continue;
                    if (ZnubeLogicExtensions.IsDepositoName(res.Name))
                        depositoQty += qty;
                    else
                        otherQty += qty;
                }
            }

            var total = depositoQty + otherQty;
            if (total > 0)
                variants.Add(new VariantStockInfo { Sku = normalizedSku, DepositoQty = depositoQty, OtherQty = otherQty, TotalQty = total });
        }

        if (variants.Count == 0)
            return null;

        var selected = new List<string>();
        var selectedCountBySku = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var depositoVariants = variants
            .Where(v => v.DepositoQty > 0)
            .OrderByDescending(v => v.DepositoQty)
            .ThenBy(v => v.Sku, StringComparer.Ordinal)
            .ToList();

        foreach (var v in depositoVariants)
        {
            if (selected.Count >= totalUnits) break;
            AddSelection(selected, selectedCountBySku, v.Sku);
        }

        var otherVariants = variants
            .Where(v => v.OtherQty > 0 && !selectedCountBySku.ContainsKey(v.Sku))
            .OrderByDescending(v => v.OtherQty)
            .ThenBy(v => v.Sku, StringComparer.Ordinal)
            .ToList();

        foreach (var v in otherVariants)
        {
            if (selected.Count >= totalUnits) break;
            AddSelection(selected, selectedCountBySku, v.Sku);
        }

        while (selected.Count < totalUnits)
        {
            var best = variants
                .Select(v => new { v.Sku, Remaining = v.TotalQty - (selectedCountBySku.TryGetValue(v.Sku, out var c) ? c : 0) })
                .Where(x => x.Remaining > 0)
                .OrderByDescending(x => x.Remaining)
                .ThenBy(x => x.Sku, StringComparer.Ordinal)
                .FirstOrDefault();

            if (best == null)
                break;

            AddSelection(selected, selectedCountBySku, best.Sku);
        }

        if (selected.Count < totalUnits)
            _logger.LogWarning("PACK DynamicSize con stock insuficiente para {TotalUnits} unidades. Se encontraron {Selected}.", totalUnits, selected.Count);

        var results = new List<OrderItemResolved>();
        foreach (var kvp in selectedCountBySku)
        {
            results.Add(new OrderItemResolved
            {
                Sku = kvp.Key,
                Quantity = kvp.Value,
                ProductLabel = kvp.Key,
                OrderItemId = item.ItemId,
                RuleType = StockRuleTypes.Pack,
                SourceItemId = resolvedSourceItemId
            });
        }

        return results;
    }

    private static void AddSelection(List<string> selected, Dictionary<string, int> selectedCountBySku, string sku)
    {
        selected.Add(sku);
        if (!selectedCountBySku.ContainsKey(sku))
            selectedCountBySku[sku] = 1;
        else
            selectedCountBySku[sku] += 1;
    }

    private static string? ExtractSizeFromSku(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku) || !ZnubeLogicExtensions.IsValidSKU(sku)) return null;
        var parts = sku.Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        return parts[^1].Trim();
    }

    private static string? ResolveSourceItemId(StockRuleDto rule, VariantMappingDto mapping)
    {
        var source = mapping.SourceMatches?.FirstOrDefault()?.SourceItemId;
        if (!string.IsNullOrWhiteSpace(source)) return source;
        var component = rule.Components?.FirstOrDefault()?.SourceItemId;
        return component;
    }

    private sealed class VariantStockInfo
    {
        public string Sku { get; set; } = string.Empty;
        public int DepositoQty { get; set; }
        public int OtherQty { get; set; }
        public int TotalQty { get; set; }
    }

    private static VariantMappingDto? ResolveMapping(StockRuleDto rule, MeliOrderItem item)
    {
        if (rule.Mappings == null || rule.Mappings.Count == 0) return null;
        var targetSku = item.UserProductId ?? item.SellerSku;
        if (!string.IsNullOrWhiteSpace(targetSku))
            return rule.Mappings.FirstOrDefault(m => string.Equals(m.TargetVariantId, targetSku, StringComparison.OrdinalIgnoreCase));

        return rule.Mappings.Count == 1 ? rule.Mappings[0] : null;
    }

    private async Task<List<RuleSourceMatchDto>?> BuildComboFallbackMatchesAsync(StockRuleDto rule, CancellationToken cancellationToken)
    {
        if (rule.Components == null || rule.Components.Count == 0)
            return null;

        var sellerId = EnvVars.GetString(EnvVars.Keys.MeliSellerId);
        if (string.IsNullOrWhiteSpace(sellerId))
            return null;

        var resolvedIds = new List<string>();
        var componentByResolvedId = new Dictionary<string, RuleComponentDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in rule.Components)
        {
            var resolvedId = await ResolveItemIdAsync(component.SourceItemId, sellerId, cancellationToken);
            if (string.IsNullOrWhiteSpace(resolvedId))
                return null;

            resolvedIds.Add(resolvedId!);
            componentByResolvedId[resolvedId!] = component;
        }

        var items = await _meliApiClient.GetItemsAsync(resolvedIds, cancellationToken);
        if (items == null || items.Count == 0)
            return null;

        var matches = new List<RuleSourceMatchDto>();
        foreach (var resolvedId in resolvedIds)
        {
            var item = items.FirstOrDefault(i => string.Equals(i.Id, resolvedId, StringComparison.OrdinalIgnoreCase));
            if (item == null) return null;
            var variations = item.Variations ?? new List<MeliVariation>();
            if (variations.Count != 1) return null;

            var singleVar = variations[0];
            if (string.IsNullOrWhiteSpace(singleVar.SellerSku)) return null;

            var component = componentByResolvedId[resolvedId];
            matches.Add(new RuleSourceMatchDto
            {
                SourceItemId = item.Id,
                SourceVariantId = !string.IsNullOrWhiteSpace(singleVar.UserProductId) ? singleVar.UserProductId : singleVar.Id.ToString(),
                SourceSku = singleVar.SellerSku!,
                Quantity = component.Quantity
            });
        }

        return matches;
    }

    private async Task<string?> ResolveItemIdAsync(string sourceItemId, string sellerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceItemId)) return null;
        if (sourceItemId.StartsWith(MeliConstants.ItemIdPrefixMla, StringComparison.OrdinalIgnoreCase))
            return sourceItemId;

        var search = await _meliApiClient.SearchItemsAsync(long.Parse(sellerId), new MeliItemSearchQuery { UserProductId = sourceItemId }, cancellationToken);
        return search?.Results?.FirstOrDefault()?.Id;
    }
}
