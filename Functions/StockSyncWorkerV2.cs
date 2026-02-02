using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Services;
using meli_znube_integration.Services.Calculators;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace meli_znube_integration.Functions;

public class StockSyncWorkerV2
{
    private readonly StockRuleService _stockRuleService;
    private readonly MeliClient _meliClient;
    private readonly StockCalculatorFactory _calculatorFactory;
    private readonly ILogger<StockSyncWorkerV2> _logger;

    public StockSyncWorkerV2(
        StockRuleService stockRuleService, 
        MeliClient meliClient, 
        StockCalculatorFactory calculatorFactory,
        ILogger<StockSyncWorkerV2> logger)
    {
        _stockRuleService = stockRuleService;
        _meliClient = meliClient;
        _calculatorFactory = calculatorFactory;
        _logger = logger;
    }

    [Function("StockSyncWorkerV2")]
    public async Task Run([TimerTrigger("0 0 5,16 * * *")] TimerInfo myTimer)
    {
        _logger.LogInformation($"Starting Stock Sync V2 at: {DateTime.Now}");

        if (!EnvVars.GetBool(EnvVars.Keys.EnableJobSyncV2, true))
        {
            _logger.LogWarning("Job 'StockSyncWorkerV2' is disabled via configuration.");
            return;
        }

        // Step 1: Load Rules
        var rules = await _stockRuleService.GetAllRulesAsync();
        if (rules == null || rules.Count == 0)
        {
            _logger.LogInformation("No stock rules found. Exiting.");
            return;
        }

        _logger.LogInformation($"Loaded {rules.Count} rules.");

        // Step 2: Process Rules (Parallel Loop)
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 5 };
        
        await Parallel.ForEachAsync(rules, parallelOptions, async (rule, ct) =>
        {
            try
            {
                // 1. Identify Components
                var components = rule.Components;
                
                // rule.TargetItemId replaces rule.RowKey
                var targetItemId = rule.TargetItemId;

                if (components == null || components.Count == 0)
                {
                    _logger.LogWarning($"Rule for Target {targetItemId} has no components. Skipping.");
                    return;
                }

                // 2. Fetch Source Items
                // We need to fetch the ITEMS, not just stock, because Calculators need Variations/SKUs.
                // Component.SourceItemId might be VariantID or ItemID.
                // MeliClient.GetItemsAsync takes ItemIDs.
                // If SourceItemId is a VariantID (UserProductId), we need to find the ItemID first?
                // Or does GetItemsAsync work with UserProductId? No, usually ItemID (MLA...).
                // Issue: If we only have UserProductId (VariantID), we can't easily fetch the Item without search.
                // Assumption: SourceItemId in Component IS the ItemID (MLA...) OR we have a way to get it.
                // If the previous logic used `GetUserProductStockAsync(upid)`, it implies we have UPID.
                // But `GetItemsAsync` needs ItemID.
                
                // Let's assume for now we fetch stock using UPID for the simple check, 
                // BUT for the Calculator we need the full Item object.
                // If we don't have ItemID, we are stuck.
                // Let's assume `SourceItemId` IS the ItemID (MLA...) for the purpose of fetching full data.
                // OR we use `SearchItemByUserProductIdAsync` if it's a UPID.
                
                var sourceItems = new List<MeliItem>();
                foreach (var comp in components)
                {
                    string itemId = comp.SourceItemId;
                    if (!itemId.StartsWith("MLA", StringComparison.OrdinalIgnoreCase)) // Heuristic
                    {
                        // It's likely a UserProductId, try to resolve ItemID
                        var resolvedId = await _meliClient.SearchItemByUserProductIdAsync(EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId), comp.SourceItemId);
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
                    _logger.LogWarning($"Could not fetch source items for Target {targetItemId}. Skipping.");
                    return;
                }

                // 3. Fetch Target Item
                // We need the full Target Item to know its variants.
                // rule.TargetItemId (previously RowKey)
                string finalTargetItemId = targetItemId;
                if (!finalTargetItemId.StartsWith("MLA", StringComparison.OrdinalIgnoreCase))
                {
                     var resolvedId = await _meliClient.SearchItemByUserProductIdAsync(EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId), finalTargetItemId);
                     if (!string.IsNullOrWhiteSpace(resolvedId)) finalTargetItemId = resolvedId;
                }

                var targetItems = await _meliClient.GetItemsAsync(new[] { finalTargetItemId });
                var targetItem = targetItems?.FirstOrDefault();

                if (targetItem == null)
                {
                    _logger.LogWarning($"Could not fetch Target Item {targetItemId}. Skipping.");
                    return;
                }

                // 4. Calculate
                var calculator = _calculatorFactory.GetCalculator(rule.RuleType);
                var updates = await calculator.CalculateStockAsync(rule, targetItem, sourceItems);

                // 5. Update
                foreach (var update in updates)
                {
                    // Fetch current stock to check if update is needed (and get version)
                    var currentStock = await _meliClient.GetUserProductStockAsync(update.TargetVariantId);
                    if (currentStock != null)
                    {
                        if (currentStock.Value.Quantity != update.NewQuantity)
                        {
                            _logger.LogInformation($"Updating Target Variant {update.TargetVariantId}: {currentStock.Value.Quantity} -> {update.NewQuantity}");
                            await _meliClient.UpdateUserProductStockAsync(update.TargetVariantId, update.NewQuantity, currentStock.Value.Version);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing rule for target {rule.TargetItemId}");
            }
        });

        _logger.LogInformation($"Stock Sync V2 finished at: {DateTime.Now}");
    }
}

