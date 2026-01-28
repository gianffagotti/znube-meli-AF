using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
                var components = JsonSerializer.Deserialize<List<RuleComponentDto>>(rule.ComponentsJson);
                if (components == null || components.Count == 0)
                {
                    _logger.LogWarning($"Rule for Target {rule.RowKey} has no components. Skipping.");
                    return;
                }

                int minPotentialQty = int.MaxValue;
                bool canCalculate = true;

                // Step 3: Calculate Target Quantity based on Components
                foreach (var component in components)
                {
                    var sourceStock = await _meliClient.GetUserProductStockAsync(component.SourceItemId);
                    if (sourceStock == null)
                    {
                        _logger.LogWarning($"Could not fetch stock for Source {component.SourceItemId} (Target: {rule.RowKey}). Skipping rule.");
                        canCalculate = false;
                        break;
                    }

                    int componentQty = component.Quantity > 0 ? component.Quantity : 1;
                    int possibleQty = (int)Math.Floor((double)sourceStock.Value.Quantity / componentQty);

                    if (possibleQty < minPotentialQty)
                    {
                        minPotentialQty = possibleQty;
                    }
                }

                if (!canCalculate) return;

                int targetQty = minPotentialQty;
                if (targetQty == int.MaxValue) targetQty = 0; // Should not happen if components > 0

                // Step 4: Fetch Target Stock & Update
                var targetUpid = rule.RowKey;
                var targetStock = await _meliClient.GetUserProductStockAsync(targetUpid);

                if (targetStock == null)
                {
                    _logger.LogWarning($"Could not fetch stock for Target {targetUpid}. Skipping.");
                    return;
                }

                if (targetStock.Value.Quantity != targetQty)
                {
                    _logger.LogInformation($"Updating Target {rule.TargetSku} ({targetUpid}): {targetStock.Value.Quantity} -> {targetQty}");
                    
                    var success = await _meliClient.UpdateUserProductStockAsync(targetUpid, targetQty, targetStock.Value.Version);
                    
                    if (!success)
                    {
                        _logger.LogWarning($"Failed to update Target {rule.TargetSku} ({targetUpid}).");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing rule for target {rule.RowKey}");
            }
        });

        _logger.LogInformation($"Stock Sync V2 finished at: {DateTime.Now}");
    }
}

