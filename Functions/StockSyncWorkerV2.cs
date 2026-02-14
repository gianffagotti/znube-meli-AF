using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using meli_znube_integration.Services.Calculators;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Functions;

public class StockSyncWorkerV2
{
    private readonly StockRuleService _stockRuleService;
    private readonly IMeliApiClient _meliClient;
    private readonly IZnubeApiClient _znubeClient;
    private readonly StockCalculatorFactory _calculatorFactory;
    private readonly ILogger<StockSyncWorkerV2> _logger;

    public StockSyncWorkerV2(
        StockRuleService stockRuleService,
        IMeliApiClient meliClient,
        IZnubeApiClient znubeClient,
        StockCalculatorFactory calculatorFactory,
        ILogger<StockSyncWorkerV2> logger)
    {
        _stockRuleService = stockRuleService;
        _meliClient = meliClient;
        _znubeClient = znubeClient;
        _calculatorFactory = calculatorFactory;
        _logger = logger;
    }

    [Function("StockSyncWorkerV2")]
    public async Task Run([TimerTrigger("0 0 5,16 * * *")] TimerInfo myTimer)
    {
        _logger.LogInformation("Starting Stock Sync V2 at: {Time}", DateTime.Now);

        if (!EnvVars.GetBool(EnvVars.Keys.EnableJobSyncV2, true))
        {
            _logger.LogWarning("Job 'StockSyncWorkerV2' is disabled via configuration.");
            return;
        }

        var rules = await _stockRuleService.GetAllRulesAsync();
        if (rules == null || rules.Count == 0)
        {
            _logger.LogInformation("No stock rules found. Exiting.");
            return;
        }

        _logger.LogInformation("Loaded {Count} rules.", rules.Count);

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 5 };
        var sellerId = long.Parse(EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId));

        await Parallel.ForEachAsync(rules, parallelOptions, async (rule, ct) =>
        {
            try
            {
                var components = rule.Components;
                var targetItemId = rule.TargetItemId;

                if (components == null || components.Count == 0)
                {
                    _logger.LogWarning("Rule for Target {TargetItemId} has no components. Skipping.", targetItemId);
                    return;
                }

                // 1. Fetch source items from MELI (structure only); quantities will come from Znube (source of truth). Spec 03.
                var sourceItems = new List<MeliItem>();
                foreach (var comp in components)
                {
                    string itemId = comp.SourceItemId;
                    if (!itemId.StartsWith("MLA", StringComparison.OrdinalIgnoreCase))
                    {
                        var upSearch = await _meliClient.SearchItemsAsync(sellerId, new MeliItemSearchQuery { UserProductId = comp.SourceItemId });
                        var resolvedId = upSearch?.Results?.FirstOrDefault()?.Id;
                        if (!string.IsNullOrWhiteSpace(resolvedId)) itemId = resolvedId;
                    }

                    if (!string.IsNullOrWhiteSpace(itemId))
                    {
                        var items = await _meliClient.GetItemsAsync(new[] { itemId });
                        if (items != null && items.Count > 0) sourceItems.AddRange(items);
                    }
                }

                if (sourceItems.Count == 0)
                {
                    _logger.LogWarning("Could not fetch source items for Target {TargetItemId}. Skipping.", targetItemId);
                    return;
                }

                // 2. Overwrite quantities with Znube stock (source of truth). Spec 03.
                await EnrichSourceItemsWithZnubeStockAsync(sourceItems, ct);

                // 3. Fetch target item
                string finalTargetItemId = targetItemId;
                if (!finalTargetItemId.StartsWith("MLA", StringComparison.OrdinalIgnoreCase))
                {
                    var upSearch = await _meliClient.SearchItemsAsync(sellerId, new MeliItemSearchQuery { UserProductId = finalTargetItemId });
                    var resolvedId = upSearch?.Results?.FirstOrDefault()?.Id;
                    if (!string.IsNullOrWhiteSpace(resolvedId)) finalTargetItemId = resolvedId;
                }

                var targetItems = await _meliClient.GetItemsAsync(new[] { finalTargetItemId });
                var targetItem = targetItems?.FirstOrDefault();

                if (targetItem == null)
                {
                    _logger.LogWarning("Could not fetch Target Item {TargetItemId}. Skipping.", targetItemId);
                    return;
                }

                // 4. Anti-FULL guard. Spec 03: do not update fulfillment items.
                if (string.Equals(targetItem.Shipping?.LogisticType, "fulfillment", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Skipping FULL item {TargetItemId}...", targetItemId);
                    return;
                }

                // 5. Calculate using Engine (Phase 2 calculators)
                var calculator = _calculatorFactory.GetCalculator(rule.RuleType);
                var updates = await calculator.CalculateStockAsync(rule, targetItem, sourceItems);

                // 6. Update MELI
                foreach (var update in updates)
                {
                    var currentStock = await _meliClient.GetUserProductStockAsync(update.TargetVariantId);
                    if (currentStock != null && currentStock.Value.Quantity != update.NewQuantity)
                    {
                        _logger.LogInformation("Updating Target Variant {TargetVariantId}: {OldQty} -> {NewQty}", update.TargetVariantId, currentStock.Value.Quantity, update.NewQuantity);
                        await _meliClient.UpdateUserProductStockAsync(update.TargetVariantId, update.NewQuantity, currentStock.Value.Version);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing rule for target {TargetItemId}", rule.TargetItemId);
            }
        });

        _logger.LogInformation("Stock Sync V2 finished at: {Time}", DateTime.Now);
    }

    /// <summary>
    /// Overwrites AvailableQuantity on each item/variation with Znube stock (source of truth). Missing/404 → 0 (fail-safe).
    /// </summary>
    private async Task EnrichSourceItemsWithZnubeStockAsync(List<MeliItem> sourceItems, CancellationToken ct)
    {
        foreach (var item in sourceItems)
        {
            if (item.Variations != null && item.Variations.Count > 0)
            {
                foreach (var variation in item.Variations)
                {
                    var sku = variation.SellerSku ?? item.SellerSku;
                    if (string.IsNullOrWhiteSpace(sku)) continue;
                    var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sku);
                    var qty = await GetZnubeQuantityBySkuAsync(normalizedSku, ct);
                    variation.AvailableQuantity = qty;
                }
            }
            else
            {
                var sku = item.SellerSku;
                if (string.IsNullOrWhiteSpace(sku)) continue;
                var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sku);
                var qty = await GetZnubeQuantityBySkuAsync(normalizedSku, ct);
                item.AvailableQuantity = qty;
            }
        }
    }

    private async Task<int> GetZnubeQuantityBySkuAsync(string sku, CancellationToken ct)
    {
        var response = await _znubeClient.GetStockBySkuAsync(sku, ct);
        if (response?.Data?.Stock == null) return 0;
        var skuItem = response.Data.Stock.FirstOrDefault(s => string.Equals(s.Sku, sku, StringComparison.OrdinalIgnoreCase));
        if (skuItem?.Stock == null) return 0;
        return (int)Math.Max(0, skuItem.Stock.Sum(d => d.Quantity));
    }
}
