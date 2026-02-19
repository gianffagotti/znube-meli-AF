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
    private readonly IStockSyncSourceService _stockSyncSourceService;
    private readonly StockCalculatorFactory _calculatorFactory;
    private readonly ILogger<StockSyncWorkerV2> _logger;

    public StockSyncWorkerV2(
        StockRuleService stockRuleService,
        IMeliApiClient meliClient,
        IStockSyncSourceService stockSyncSourceService,
        StockCalculatorFactory calculatorFactory,
        ILogger<StockSyncWorkerV2> logger)
    {
        _stockRuleService = stockRuleService;
        _meliClient = meliClient;
        _stockSyncSourceService = stockSyncSourceService;
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

        var rules = await _stockRuleService.GetRulesBySellerAsync();
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

                // 2. Overwrite quantities with Znube stock (source of truth). Worker always uses ProductId strategy. Spec 03.
                await _stockSyncSourceService.EnrichSourceItemsWithZnubeStockAsync(sourceItems, rule.RuleType, fromWorker: true, ct);

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

                // 4. Anti-FULL guard. Spec 03: skip fulfillment unless hybrid (has selling_address).
                if (await _stockSyncSourceService.ShouldSkipFulfillmentTargetAsync(targetItem, ct))
                {
                    _logger.LogInformation("Skipping FULL-only item {TargetItemId} (no selling_address).", targetItemId);
                    return;
                }

                // 5. Calculate using Engine (Phase 2 calculators)
                var calculator = _calculatorFactory.GetCalculator(rule.RuleType);
                var updates = await calculator.CalculateStockAsync(rule, targetItem, sourceItems);

                // 6. Update MELI (selling_address only; MeliApiClient already uses type/selling_address). Spec 03.
                foreach (var update in updates)
                {
                    var currentStock = await _meliClient.GetUserProductStockAsync(update.TargetVariantId);
                    if (currentStock != null && currentStock.Value.Quantity != update.NewQuantity)
                    {
                        _logger.LogInformation("Updating Target Variant {TargetVariantId}: {OldQty} -> {NewQty}", update.TargetVariantId, currentStock.Value.Quantity, update.NewQuantity);
                        await _meliClient.UpdateUserProductStockAsync(update.TargetVariantId, update.NewQuantity, currentStock.Value.Version, ct);
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
}
