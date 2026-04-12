using System.Text.Json;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services.Calculators;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

public class StockLocationProcessor
{
    private readonly StockRuleService _stockRuleService;
    private readonly IMeliApiClient _meliClient;
    private readonly IStockSyncSourceService _stockSyncSourceService;
    private readonly StockCalculatorFactory _calculatorFactory;
    private readonly IDashboardLogService _dashboardLogService;
    private readonly ILogger<StockLocationProcessor> _logger;

    public StockLocationProcessor(
        StockRuleService stockRuleService,
        IMeliApiClient meliClient,
        IStockSyncSourceService stockSyncSourceService,
        StockCalculatorFactory calculatorFactory,
        IDashboardLogService dashboardLogService,
        ILogger<StockLocationProcessor> logger)
    {
        _stockRuleService = stockRuleService;
        _meliClient = meliClient;
        _stockSyncSourceService = stockSyncSourceService;
        _calculatorFactory = calculatorFactory;
        _dashboardLogService = dashboardLogService;
        _logger = logger;
    }

    public async Task ProcessAsync(string resource, CancellationToken ct)
    {
        if (!EnvVars.GetBool(EnvVars.Keys.EnableWebhookStock, false))
        {
            _logger.LogWarning("EnableWebhookStock disabled; stock-locations webhook ignored.");
            return;
        }

        _logger.LogInformation("Executing logic for stock notification. Resource: {Resource}", resource);

        if (string.IsNullOrWhiteSpace(resource))
        {
            return;
        }

        var userProductId = StockLocationHelpers.ExtractUserProductId(resource);
        if (string.IsNullOrWhiteSpace(userProductId))
        {
            _logger.LogWarning("No se pudo extraer user_product_id de: {Resource}", resource);
            return;
        }

        var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
        string? sourceItemId = null;
        MeliItem? sourceItem = null;
        string? sku = null;
        try
        {
            var search = await _meliClient.SearchItemsAsync(long.Parse(sellerId), new MeliItemSearchQuery { UserProductId = userProductId }, ct);
            sourceItemId = search?.Results?.FirstOrDefault()?.Id;
            if (!string.IsNullOrWhiteSpace(sourceItemId))
            {
                var items = await _meliClient.GetItemsAsync([sourceItemId], ct);
                sourceItem = items?.FirstOrDefault();
                if (sourceItem != null)
                {
                    var variation = sourceItem.Variations?.FirstOrDefault(v => v.UserProductId == userProductId);
                    sku = StockLocationHelpers.ExtractSku(sourceItem, variation);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo resolver UserProductId {UserProductId} a ItemId.", userProductId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(sku))
        {
            var fullRules = await _stockRuleService.GetFullRulesBySkuAsync(sellerId, sku);
            if (fullRules != null && fullRules.Count > 0)
            {
                _logger.LogInformation("SKU {Sku} affects {Count} FULL rule(s).", sku, fullRules.Count);
                foreach (var rule in fullRules)
                {
                    try
                    {
                        var sourceItems = FullRuleSourceItemsHelper.BuildSyntheticSourceItemsForFullRule(rule, sku);
                        if (sourceItems.Count == 0) continue;

                        await _stockSyncSourceService.EnrichSourceItemsWithZnubeStockAsync(sourceItems, StockRuleTypes.Full, fromWorker: false, ct);

                        var finalTargetItemId = await ResolveTargetItemIdAsync(sellerId, rule.TargetItemId, ct);
                        if (string.IsNullOrWhiteSpace(finalTargetItemId)) continue;
                        var targetItemsList = await _meliClient.GetItemsAsync(new[] { finalTargetItemId }, ct);
                        var targetItem = targetItemsList?.FirstOrDefault();
                        if (targetItem == null) continue;
                        if (await _stockSyncSourceService.ShouldSkipFulfillmentTargetAsync(targetItem, ct)) continue;

                        var calculator = _calculatorFactory.GetCalculator(rule.RuleType);
                        var updates = await calculator.CalculateStockAsync(rule, targetItem, sourceItems);
                        foreach (var update in updates)
                        {
                            await ApplyUpdateWithLogsAsync(rule, targetItem, finalTargetItemId, update, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing FULL rule for target {TargetId}", rule.TargetItemId);
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(sourceItemId))
        {
            return;
        }

        var affectedIndexes = await _stockRuleService.GetAffectedRulesBySourceAsync(sourceItemId);
        if (affectedIndexes == null || affectedIndexes.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Source ItemId {ItemId} affects {Count} PACK/COMBO rule(s).", sourceItemId, affectedIndexes.Count);

        foreach (var index in affectedIndexes)
        {
            var targetItemId = index.RowKey;
            try
            {
                var rule = await _stockRuleService.GetRuleAsync(sellerId, targetItemId);
                if (rule == null) continue;

                rule.Mappings = [.. rule.Mappings
                    .Where(m =>
                            m.SourceMatches.Any(sm => sm.SourceVariantId == userProductId) ||
                            (m.MatchSize != null && m.MatchSize.Equals(PackStockCalculator.ParseSizeFromSku(sku), StringComparison.OrdinalIgnoreCase)))];

                var components = rule.Components;
                if (components == null || components.Count == 0) continue;

                var itemsId = components
                    .Select(c => c.SourceItemId)
                    .Where(id => !string.IsNullOrWhiteSpace(id));
                var items = await _meliClient.GetItemsAsync(itemsId, ct);

                var mappingsSourceMatches = rule.Mappings.SelectMany(m => m.SourceMatches).Select(sm => sm.SourceVariantId);
                var mappingsSizeMatches = rule.Mappings.Where(m => m.MatchSize is not null).Select(m => m.MatchSize!);
                var sourceItems = items.Select(i =>
                {
                    i.Variations = i.Variations?
                        .Where(v => mappingsSourceMatches.Any(msm => msm.Equals(v.UserProductId, StringComparison.OrdinalIgnoreCase)) ||
                                    mappingsSizeMatches.Any(msm => msm.Equals(PackStockCalculator.ParseSizeFromSku(v.SellerSku), StringComparison.OrdinalIgnoreCase)))
                        .ToList() ?? [];
                    return i;
                }).ToList();

                if (sourceItems.Count == 0) continue;

                await _stockSyncSourceService.EnrichSourceItemsWithZnubeStockAsync(sourceItems, rule.RuleType, fromWorker: false, ct);

                var finalTargetItemId = await ResolveTargetItemIdAsync(sellerId, targetItemId, ct);
                if (string.IsNullOrWhiteSpace(finalTargetItemId)) continue;
                var targetItemsToCheck = await _meliClient.GetItemsAsync(new[] { finalTargetItemId }, ct);
                var targetItem = targetItemsToCheck?.FirstOrDefault();
                if (targetItem == null) continue;
                if (await _stockSyncSourceService.ShouldSkipFulfillmentTargetAsync(targetItem, ct)) continue;

                List<VariantStockUpdate> updates;
                try
                {
                    var calculator = _calculatorFactory.GetCalculator(rule.RuleType);
                    updates = await calculator.CalculateStockAsync(rule, targetItem, sourceItems);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error calculating stock for target {TargetId}", targetItemId);
                    if (string.Equals(rule.RuleType, StockRuleTypes.Pack, StringComparison.OrdinalIgnoreCase))
                    {
                        await LogPackErrorAsync(ex, targetItemId, null, sku, null, null, "calculate", ct);
                    }
                    continue;
                }

                foreach (var update in updates)
                {
                    await ApplyUpdateWithLogsAsync(rule, targetItem, finalTargetItemId, update, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing target {TargetId}", targetItemId);
            }
        }
    }

    private async Task ApplyUpdateWithLogsAsync(StockRuleDto rule, MeliItem targetItem, string targetItemId, VariantStockUpdate update, CancellationToken ct)
    {
        var currentStock = await _meliClient.GetUserProductStockAsync(update.TargetVariantId, ct);
        if (currentStock == null || currentStock.Value.Quantity == update.NewQuantity)
        {
            return;
        }

        var dryRun = EnvVars.GetBool(EnvVars.Keys.DryRun, false);
        if (dryRun)
        {
            _logger.LogInformation(
                "DRY_RUN: would update Target Variant {TargetVariantId}: {OldQty} -> {NewQty}",
                update.TargetVariantId,
                currentStock.Value.Quantity,
                update.NewQuantity);
            return;
        }

        try
        {
            var success = await _meliClient.UpdateUserProductStockAsync(update.TargetVariantId, update.NewQuantity, currentStock.Value.Version, ct);
            if (!success)
            {
                _logger.LogWarning("Conflict updating Target Variant {TargetVariantId}.", update.TargetVariantId);
                if (string.Equals(rule.RuleType, StockRuleTypes.Pack, StringComparison.OrdinalIgnoreCase))
                {
                    await LogPackErrorAsync(null, targetItemId, update.TargetVariantId, ResolveTargetSku(targetItem, update.TargetVariantId), currentStock.Value.Quantity, update.NewQuantity, "update_conflict", ct);
                }
                return;
            }

            _logger.LogInformation("Updating Target Variant {TargetVariantId}: {OldQty} -> {NewQty}", update.TargetVariantId, currentStock.Value.Quantity, update.NewQuantity);
            await LogStockUpdateInfoAsync(targetItemId, update.TargetVariantId, ResolveTargetSku(targetItem, update.TargetVariantId), currentStock.Value.Quantity, update.NewQuantity, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating Target Variant {TargetVariantId}", update.TargetVariantId);
            if (string.Equals(rule.RuleType, StockRuleTypes.Pack, StringComparison.OrdinalIgnoreCase))
            {
                await LogPackErrorAsync(ex, targetItemId, update.TargetVariantId, ResolveTargetSku(targetItem, update.TargetVariantId), currentStock.Value.Quantity, update.NewQuantity, "update_exception", ct);
            }
        }
    }

    private async Task LogStockUpdateInfoAsync(string targetItemId, string targetVariantId, string? sku, int oldQty, int newQty, CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            targetItemId,
            targetVariantId,
            sku,
            oldQty,
            newQty
        });

        await _dashboardLogService.AppendLogAsync(
            severity: "Info",
            category: "StockSyncWebhook",
            message: $"Stock actualizado para publicación {targetItemId}, SKU {sku}.",
            detailsJson: details,
            entityIds: new[] { targetItemId, targetVariantId },
            cancellationToken: ct);
    }

    private async Task LogPackErrorAsync(Exception? ex, string targetItemId, string? targetVariantId, string? sku, int? oldQty, int? newQty, string stage, CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            targetItemId,
            targetVariantId,
            sku,
            oldQty,
            newQty,
            stage,
            exception = ex?.ToString()
        });

        await _dashboardLogService.AppendLogAsync(
            severity: "Error",
            category: "StockSyncWebhook",
            message: $"Error actualizando stock de PACK para publicación {targetItemId}, SKU {sku}.",
            detailsJson: details,
            entityIds: targetVariantId != null ? new[] { targetItemId, targetVariantId } : new[] { targetItemId },
            cancellationToken: ct);
    }

    private static string? ResolveTargetSku(MeliItem targetItem, string targetVariantId)
    {
        var variation = targetItem.Variations?.FirstOrDefault(v =>
            string.Equals(v.UserProductId, targetVariantId, StringComparison.OrdinalIgnoreCase));
        return StockLocationHelpers.ExtractSku(targetItem, variation);
    }

    private async Task<string?> ResolveTargetItemIdAsync(string sellerId, string targetItemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetItemId)) return null;
        if (targetItemId.StartsWith(MeliConstants.ItemIdPrefixMla, StringComparison.OrdinalIgnoreCase)) return targetItemId;
        var upSearch = await _meliClient.SearchItemsAsync(long.Parse(sellerId), new MeliItemSearchQuery { UserProductId = targetItemId }, ct);
        return upSearch?.Results?.FirstOrDefault()?.Id;
    }
}
