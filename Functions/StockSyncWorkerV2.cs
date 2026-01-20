using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Functions;

public class StockSyncWorkerV2
{
    private readonly StockRuleService _stockRuleService;
    private readonly MeliClient _meliClient;
    private readonly ILogger<StockSyncWorkerV2> _logger;

    public StockSyncWorkerV2(StockRuleService stockRuleService, MeliClient meliClient, ILogger<StockSyncWorkerV2> logger)
    {
        _stockRuleService = stockRuleService;
        _meliClient = meliClient;
        _logger = logger;
    }

    [Function("StockSyncWorkerV2")]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo myTimer)
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

        // Step 2: Group by Mother
        var rulesByMother = rules.GroupBy(r => r.PartitionKey);

        // Step 3: Process Groups (Parallel Loop)
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 5 };
        
        await Parallel.ForEachAsync(rulesByMother, parallelOptions, async (group, ct) =>
        {
            var motherUpid = group.Key;
            try
            {
                // Step 4: Sync Logic (Per Mother)
                // Fetch Mother Stock
                var motherStock = await _meliClient.GetUserProductStockAsync(motherUpid);
                if (motherStock == null)
                {
                    _logger.LogWarning($"Could not fetch stock for Mother {motherUpid}. Skipping group.");
                    return;
                }

                int motherQuantity = motherStock.Value.Quantity;

                // Iterate Children Rules
                foreach (var rule in group)
                {
                    try
                    {
                        // Calculate Target Quantity
                        // Ensure strict type casting for the math (integers)
                        // rule.PackQuantity defaults to 1 if 0 to avoid divide by zero, though model has default 1
                        int packQty = rule.PackQuantity > 0 ? rule.PackQuantity : 1;
                        int targetQty = (int)Math.Floor((double)motherQuantity / packQty);

                        // Fetch Child Stock
                        var childUpid = rule.RowKey;
                        var childStock = await _meliClient.GetUserProductStockAsync(childUpid);

                        if (childStock == null)
                        {
                            _logger.LogWarning($"Could not fetch stock for Child {childUpid} (Mother: {motherUpid}). Skipping child.");
                            continue;
                        }

                        // Compare & Update
                        if (childStock.Value.Quantity != targetQty)
                        {
                            _logger.LogInformation($"Updating Child {rule.ChildSku} ({childUpid}): {childStock.Value.Quantity} -> {targetQty} (Mother: {rule.MotherSku} Qty: {motherQuantity})");
                            
                            var success = await _meliClient.UpdateUserProductStockAsync(childUpid, targetQty, childStock.Value.Version);
                            
                            if (!success)
                            {
                                _logger.LogWarning($"Failed to update Child {rule.ChildSku} ({childUpid}).");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing child rule {rule.RowKey} for mother {motherUpid}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing mother group {motherUpid}");
            }
        });

        _logger.LogInformation($"Stock Sync V2 finished at: {DateTime.Now}");
    }
}
