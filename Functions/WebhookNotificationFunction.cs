using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Infrastructure;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using meli_znube_integration.Services.Calculators;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace meli_znube_integration.Functions;

public class WebhookNotificationFunction
{
    private readonly PackProcessor _processor;
    private readonly IMeliApiClient _meliClient;
    private readonly StockMappingService _stockMappingService;
    private readonly StockRuleService _stockRuleService;
    private readonly StockCalculatorFactory _calculatorFactory;
    private readonly IStockSyncSourceService _stockSyncSourceService;
    private readonly IOrderExecutionStore _orderExecutionStore;
    private readonly ILogger<WebhookNotificationFunction> _logger;
    private readonly IConfiguration _configuration;

    public WebhookNotificationFunction(
        PackProcessor processor,
        IMeliApiClient meliClient,
        StockMappingService stockMappingService,
        StockRuleService stockRuleService,
        StockCalculatorFactory calculatorFactory,
        IStockSyncSourceService stockSyncSourceService,
        IOrderExecutionStore orderExecutionStore,
        ILogger<WebhookNotificationFunction> logger,
        IConfiguration configuration)
    {
        _processor = processor;
        _meliClient = meliClient;
        _stockMappingService = stockMappingService;
        _stockRuleService = stockRuleService;
        _calculatorFactory = calculatorFactory;
        _stockSyncSourceService = stockSyncSourceService;
        _orderExecutionStore = orderExecutionStore;
        _logger = logger;
        _configuration = configuration;
    }

    [Function("WebhookNotificacion")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook/notification")] HttpRequestData req)
    {
        var apiKey = req.Query["api-key"];
        var configuredKey = _configuration["WebhookApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey) || !string.Equals(apiKey, configuredKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("Intento de acceso no autorizado al webhook. API Key inválida o ausente.");
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }
        string? topic = null;
        string? resource = null;

        try
        {
            using (var doc = await JsonDocument.ParseAsync(req.Body))
            {
                if (doc.RootElement.TryGetProperty("topic", out var topicProp) && topicProp.ValueKind == JsonValueKind.String)
                {
                    topic = topicProp.GetString();
                }

                if (doc.RootElement.TryGetProperty("resource", out var resourceProp) && resourceProp.ValueKind == JsonValueKind.String)
                {
                    resource = resourceProp.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(topic))
            {
                _logger.LogWarning("Webhook recibido sin tópico.");
                return req.CreateResponse(HttpStatusCode.OK);
            }

            switch (topic)
            {
                case "orders_v2":
                    return await ProcessOrderNotification(req, resource);
                case "stock-location":
                    return await ProcessStockNotification(req, resource);
                default:
                    _logger.LogInformation("Tópico no manejado: {Topic}. Resource: {Resource}", topic, resource);
                    return req.CreateResponse(HttpStatusCode.OK);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando webhook. Topic: {Topic}, Resource: {Resource}", topic, resource);
            throw;
        }
    }

    private async Task<HttpResponseData> ProcessStockNotification(HttpRequestData req, string? resource)
    {
        bool useV2 = EnvVars.GetBool(EnvVars.Keys.UseV2StockLogic, false);
        if (useV2)
        {
            return await ProcessStockNotificationV2(req, resource);
        }

        // Resource format: /user-products/{user_product_id}/stock
        var userId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
        if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(userId))
        {
             return req.CreateResponse(HttpStatusCode.OK);
        }

        var userProductId = ExtractUserProductId(resource);
        if (string.IsNullOrWhiteSpace(userProductId))
        {
            _logger.LogWarning("No se pudo extraer user_product_id de: {Resource}", resource);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // 1. Parent Discovery
        var search = await _meliClient.SearchItemsAsync(long.Parse(userId), new MeliItemSearchQuery { UserProductId = userProductId });
        var itemId = search?.Results?.FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(itemId))
        {
            _logger.LogInformation("No se encontró Item ID para user_product_id: {UserProductId}. Ignorando.", userProductId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // 2. Source Validation & SKU Extraction
        var items = await _meliClient.GetItemsAsync([itemId!]);
        var item = items.FirstOrDefault();
        if (item == null)
        {
             return req.CreateResponse(HttpStatusCode.OK);
        }

        if (string.Equals(item.Shipping?.LogisticType, "fulfillment", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Ignorando notificación de stock para item FULL: {ItemId}", itemId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        string? sku = null;
        if (item.Variations != null && item.Variations.Count > 0)
        {
            var variation = item.Variations.FirstOrDefault(v => v.UserProductId == userProductId);
            if (variation != null)
            {
                sku = ExtractSku(item, variation);
            }
        }
        else
        {
            // Simple item check (though user_product_id usually implies variations or specific stock locations, simple items also have it)
            // If user_product_id matches item's user_product_id (if available in model) or we assume simple item.
            // For safety, let's try to extract SKU from item directly if no variation matched or exists.
             sku = ExtractSku(item, null);
        }

        if (string.IsNullOrWhiteSpace(sku))
        {
            _logger.LogWarning("No se pudo extraer SKU para Item: {ItemId}, UserProduct: {UserProductId}", itemId, userProductId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // 3. Reliable Source Stock Fetch
        var sourceStock = await _meliClient.GetUserProductStockAsync(userProductId);
        if (sourceStock == null)
        {
             _logger.LogWarning("No se pudo obtener stock del source: {UserProductId}", userProductId);
             return req.CreateResponse(HttpStatusCode.OK);
        }
        var sourceQuantity = sourceStock.Value.Quantity;

        // 4. Target Discovery
        string? targetUserProductId = await _stockMappingService.GetTargetUserProductIdAsync(sku!);
        
        if (string.IsNullOrWhiteSpace(targetUserProductId))
        {
            // Slow Path
            var skuSearch = await _meliClient.SearchItemsAsync(long.Parse(userId), new MeliItemSearchQuery { SellerSku = sku! });
            var targetItemId = skuSearch?.Results?.FirstOrDefault()?.Id;
            if (!string.IsNullOrWhiteSpace(targetItemId))
            {
                 var targetItems = await _meliClient.GetItemsAsync([targetItemId!]);
                 var targetItem = targetItems.FirstOrDefault();
                 if (targetItem != null && string.Equals(targetItem.Shipping?.LogisticType, "fulfillment", StringComparison.OrdinalIgnoreCase))
                 {
                     // Find the variation/UPID for this SKU in the target item
                     if (targetItem.Variations != null && targetItem.Variations.Count > 0)
                     {
                         // Need to find variation with same SKU
                         foreach(var v in targetItem.Variations)
                         {
                             var vSku = ExtractSku(targetItem, v);
                             if (string.Equals(vSku, sku, StringComparison.OrdinalIgnoreCase))
                             {
                                 targetUserProductId = v.UserProductId;
                                 break;
                             }
                         }
                     }
                     else
                     {
                         targetUserProductId = targetItem.UserProductId;
                     }
                 }
            }
        }

        if (string.IsNullOrWhiteSpace(targetUserProductId))
        {
             _logger.LogInformation("No se encontró target FULL para SKU: {Sku}", sku);
             return req.CreateResponse(HttpStatusCode.OK);
        }

        // 4b. Resolve target item for Anti-FULL guard (skip FULL-only; allow hybrid). Spec 03.
        MeliItem? targetItemForGuard = null;
        var targetSearch = await _meliClient.SearchItemsAsync(long.Parse(userId), new MeliItemSearchQuery { UserProductId = targetUserProductId });
        var targetItemIdForGuard = targetSearch?.Results?.FirstOrDefault()?.Id;
        if (!string.IsNullOrWhiteSpace(targetItemIdForGuard))
        {
            var targetItemsForGuard = await _meliClient.GetItemsAsync([targetItemIdForGuard!]);
            targetItemForGuard = targetItemsForGuard?.FirstOrDefault();
        }
        if (targetItemForGuard != null && await _stockSyncSourceService.ShouldSkipFulfillmentTargetAsync(targetItemForGuard))
        {
            _logger.LogDebug("V1: Skipping FULL-only target for SKU {Sku} (no selling_address).", sku);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // 5. Synchronization
        var targetStock = await _meliClient.GetUserProductStockAsync(targetUserProductId!);
        if (targetStock == null)
        {
             _logger.LogWarning("No se pudo leer stock del target: {TargetUserProductId}", targetUserProductId);
             return req.CreateResponse(HttpStatusCode.OK);
        }

        if (targetStock.Value.Quantity != sourceQuantity)
        {
            _logger.LogInformation("Sincronizando SKU {Sku}: {TargetQuantity} -> {SourceQuantity}", sku, targetStock.Value.Quantity, sourceQuantity);
            var success = await _meliClient.UpdateUserProductStockAsync(targetUserProductId!, sourceQuantity, targetStock.Value.Version);
            if (!success)
            {
                _logger.LogWarning("Conflicto de concurrencia al actualizar SKU {Sku}. Se reintentará en el próximo evento.", sku);
            }
        }
        else
        {
            _logger.LogInformation("Stock sincronizado para SKU {Sku}. Cantidad: {Quantity}", sku, sourceQuantity);
        }

        return req.CreateResponse(HttpStatusCode.OK);
    }

    private static string? ExtractSku(MeliItem item, MeliVariation? variation)
    {
        if (variation != null)
        {
            if (!string.IsNullOrWhiteSpace(variation.SellerSku)) return variation.SellerSku.ToUpper();

            var skuAttr = variation.Attributes.FirstOrDefault(a => a.Id == "SELLER_SKU");
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName.ToUpper();

            if (!string.IsNullOrWhiteSpace(variation.SellerCustomField)) return variation.SellerCustomField.ToUpper();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(item.SellerSku)) return item.SellerSku.ToUpper(); // Assuming SellerSku exists on Item model, if not check attributes/custom field

            var skuAttr = item.Attributes.FirstOrDefault(a => a.Id == "SELLER_SKU");
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName.ToUpper();
            
            if (!string.IsNullOrWhiteSpace(item.SellerCustomField)) return item.SellerCustomField.ToUpper();
        }

        return null;
    }

    private static string ExtractUserProductId(string path)
    {
        // /user-products/{user_product_id}/stock
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // parts: user-products, {id}, stock
        if (parts.Length >= 2 && string.Equals(parts[0], "user-products", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1];
        }
        return string.Empty;
    }

    private async Task<HttpResponseData> ProcessOrderNotification(HttpRequestData req, string? resource)
    {
        var (isValid, orderId) = WebhookOrderResourceHelper.TryParseOrderIdFromResource(resource);
        if (!isValid || string.IsNullOrWhiteSpace(orderId))
        {
            if (!string.IsNullOrWhiteSpace(resource))
                _logger.LogDebug("webhook ignorado: resource sin orderId válido: {Resource}", resource);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Resolve execution key: PackId (group) or OrderId (single). Spec 02.
        // If GetOrderAsync returns null (order not found), executionKey = orderId; ProcessAsync returns (null,null); we still MarkDoneAsync to avoid stuck executions.
        var orderDto = await _meliClient.GetOrderAsync(orderId);
        var order = orderDto?.ToOrder();
        var executionKey = !string.IsNullOrWhiteSpace(order?.PackId) ? order.PackId : orderId;

        var dryRun = EnvVars.GetBool(EnvVars.Keys.DryRun, false);
        if (!dryRun && !await _orderExecutionStore.TryStartExecutionAsync(executionKey))
        {
            _logger.LogDebug("Order/Pack {Key} already locked or done. Returning 200 OK.", executionKey);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        try
        {
            var (orderIdWritten, noteText) = await _processor.ProcessAsync(orderId);
            _logger.LogInformation("Nota: {NoteText}", noteText);

            var res = req.CreateResponse(HttpStatusCode.OK);
            if (!string.IsNullOrWhiteSpace(noteText))
            {
                await res.WriteStringAsync(noteText, Encoding.UTF8);
            }
            return res;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando orden {OrderId}", orderId);
            throw;
        }
        finally
        {
            await _orderExecutionStore.MarkDoneAsync(executionKey);
        }
    }

    private async Task<HttpResponseData> ProcessStockNotificationV2(HttpRequestData req, string? resource)
    {
        _logger.LogInformation("Executing V2 logic for stock notification. Resource: {Resource}", resource);

        if (string.IsNullOrWhiteSpace(resource))
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Step 1: Parse Resource — webhook sends user_product_id (variant), not item_id (MLA...)
        var userProductId = ExtractUserProductId(resource);
        if (string.IsNullOrWhiteSpace(userProductId))
        {
            _logger.LogWarning("V2: No se pudo extraer user_product_id de: {Resource}", resource);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Step 2: Resolve UserProductId → ItemId (index is keyed by ItemId, not by variant UserProductId)
        var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
        string? sourceItemId = null;
        try
        {
            var search = await _meliClient.SearchItemsAsync(long.Parse(sellerId), new MeliItemSearchQuery { UserProductId = userProductId });
            sourceItemId = search?.Results?.FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "V2: No se pudo resolver UserProductId {UserProductId} a ItemId.", userProductId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        if (string.IsNullOrWhiteSpace(sourceItemId))
        {
            _logger.LogDebug("V2: UserProductId {UserProductId} no resolvió a ningún ItemId (ítem eliminado o inaccesible).", userProductId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Step 3: Lookup Affected Rules (Reverse Index — PartitionKey = sourceItemId = ItemId)
        var affectedIndexes = await _stockRuleService.GetAffectedRulesBySourceAsync(sourceItemId);

        if (affectedIndexes == null || affectedIndexes.Count == 0)
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        _logger.LogInformation("V2: Source UserProductId {UserProductId} → ItemId {ItemId} affects {Count} rules.", userProductId, sourceItemId, affectedIndexes.Count);

        // Step 4: Iterate & Update Targets
        foreach (var index in affectedIndexes)
        {
            var targetItemId = index.RowKey; // Target is RK in Index

            try 
            {
                // Fetch Full Rule Definition
                var rule = await _stockRuleService.GetRuleAsync(sellerId, targetItemId);

                if (rule == null)
                {
                    _logger.LogWarning("V2: Rule definition not found for Target {TargetId} (Seller {SellerId})", targetItemId, sellerId);
                    continue;
                }

                // rule is now StockRuleDto
                var components = rule.Components;
                if (components == null || components.Count == 0) continue;

                // PREPARE DATA FOR CALCULATOR
                // 1. Fetch Source Items (Full MeliItem objects needed for Calculator)
                var sourceItems = new List<MeliItem>();
                foreach (var comp in components)
                {
                    string itemId = comp.SourceItemId;
                    if (!itemId.StartsWith("MLA", StringComparison.OrdinalIgnoreCase)) 
                    {
                        var upSearch = await _meliClient.SearchItemsAsync(long.Parse(sellerId), new MeliItemSearchQuery { UserProductId = comp.SourceItemId });
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
                    _logger.LogWarning("V2: Could not fetch source items for Target {TargetId}.", targetItemId);
                    continue;
                }

                // 2. Overwrite quantities with Znube stock (source of truth). Webhook: SKU for FULL/Combo, ProductId for PACK. Spec 03.
                await _stockSyncSourceService.EnrichSourceItemsWithZnubeStockAsync(sourceItems, rule.RuleType, fromWorker: false, req.FunctionContext.CancellationToken);

                // 3. Fetch Target Item
                string finalTargetItemId = targetItemId;
                if (!finalTargetItemId.StartsWith("MLA", StringComparison.OrdinalIgnoreCase))
                {
                    var upSearch = await _meliClient.SearchItemsAsync(long.Parse(sellerId), new MeliItemSearchQuery { UserProductId = finalTargetItemId });
                    var resolvedId = upSearch?.Results?.FirstOrDefault()?.Id;
                    if (!string.IsNullOrWhiteSpace(resolvedId)) finalTargetItemId = resolvedId;
                }

                var targetItemsToCheck = await _meliClient.GetItemsAsync(new[] { finalTargetItemId });
                var targetItem = targetItemsToCheck?.FirstOrDefault();

                if (targetItem == null)
                {
                    _logger.LogWarning("V2: Could not fetch Target Item {TargetId}.", targetItemId);
                    continue;
                }

                // 4. Anti-FULL guard. Spec 03: skip fulfillment unless hybrid (has selling_address).
                if (await _stockSyncSourceService.ShouldSkipFulfillmentTargetAsync(targetItem))
                {
                    _logger.LogDebug("V2: Skipping FULL-only target {TargetId} (no selling_address).", targetItemId);
                    continue;
                }

                // 5. CALCULATE (Engine calculators use Znube-derived quantities from enriched sourceItems)
                var calculator = _calculatorFactory.GetCalculator(rule.RuleType);
                var updates = await calculator.CalculateStockAsync(rule, targetItem, sourceItems);

                // 6. UPDATE (selling_address only; MeliApiClient uses type/selling_address). Spec 03.
                foreach (var update in updates)
                {
                    var currentStock = await _meliClient.GetUserProductStockAsync(update.TargetVariantId);
                    if (currentStock != null && currentStock.Value.Quantity != update.NewQuantity)
                    {
                        _logger.LogInformation("V2: Updating Target Variant {TargetVariantId}: {OldQty} -> {NewQty}", update.TargetVariantId, currentStock.Value.Quantity, update.NewQuantity);
                        await _meliClient.UpdateUserProductStockAsync(update.TargetVariantId, update.NewQuantity, currentStock.Value.Version);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "V2: Error processing target {TargetId}", targetItemId);
            }
        }

        return req.CreateResponse(HttpStatusCode.OK);
    }
}


