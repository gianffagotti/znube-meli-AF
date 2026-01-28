using meli_znube_integration.Services;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace meli_znube_integration.Functions;

public class WebhookNotificationFunction
{
    private readonly PackProcessor _processor;
    private readonly MeliClient _meliClient;
    private readonly StockMappingService _stockMappingService;
    private readonly StockRuleService _stockRuleService;
    private readonly ILogger<WebhookNotificationFunction> _logger;
    private readonly IConfiguration _configuration;

    public WebhookNotificationFunction(
        PackProcessor processor,
        MeliClient meliClient,
        StockMappingService stockMappingService,
        StockRuleService stockRuleService,
        ILogger<WebhookNotificationFunction> logger,
        IConfiguration configuration)
    {
        _processor = processor;
        _meliClient = meliClient;
        _stockMappingService = stockMappingService;
        _stockRuleService = stockRuleService;
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
        var itemId = await _meliClient.SearchItemByUserProductIdAsync(userId, userProductId);
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
            var targetItemId = await _meliClient.SearchItemBySkuAsync(userId, sku!);
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
        if (string.IsNullOrWhiteSpace(resource))
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Aceptar solo recursos de órdenes
        if (resource!.IndexOf("/orders/", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Validaciones tempranas del recurso y orderId
        var orderId = ExtractLastSegment(resource!);
        if (string.IsNullOrWhiteSpace(orderId) || !orderId.Trim().All(char.IsDigit))
        {
            _logger.LogDebug("webhook ignorado: resource sin orderId válido: {Resource}", resource);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        try
        {
            var (orderIdWritten, noteText) = await _processor.ProcessAsync(orderId);
            _logger.LogInformation($"Nota: {noteText}");

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
    }

    private async Task<HttpResponseData> ProcessStockNotificationV2(HttpRequestData req, string? resource)
    {
        _logger.LogInformation("Executing V2 logic for stock notification. Resource: {Resource}", resource);

        if (string.IsNullOrWhiteSpace(resource))
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Step 1: Parse Resource (Source Item)
        var sourceItemId = ExtractUserProductId(resource);
        if (string.IsNullOrWhiteSpace(sourceItemId))
        {
            _logger.LogWarning("V2: No se pudo extraer user_product_id de: {Resource}", resource);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        // Step 2: Lookup Affected Rules (Reverse Index)
        // Note: The index returns StockSourceIndexEntity, which has RuleType but not the full rule definition.
        // We need the full rule definition to get the list of components.
        // So we need to fetch the Rule Entity using the Index info.
        // Index: PK=Source, RK=Target.
        // Rule: PK=SellerId, RK=Target.
        // We need SellerId. We can get it from EnvVars.
        
        var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
        var affectedIndexes = await _stockRuleService.GetAffectedRulesBySourceAsync(sourceItemId);

        if (affectedIndexes == null || affectedIndexes.Count == 0)
        {
            // No rules affected by this source.
            return req.CreateResponse(HttpStatusCode.OK);
        }

        _logger.LogInformation("V2: Source {SourceId} affects {Count} rules.", sourceItemId, affectedIndexes.Count);

        // Step 3: Iterate & Update Targets
        foreach (var index in affectedIndexes)
        {
            var targetItemId = index.RowKey; // Target is RK in Index

            try 
            {
                // Fetch Full Rule Definition using efficient lookup
                var rule = await _stockRuleService.GetRuleAsync(sellerId, targetItemId);

                if (rule == null)
                {
                    _logger.LogWarning("V2: Rule definition not found for Target {TargetId} (Seller {SellerId})", targetItemId, sellerId);
                    continue;
                }

                var components = JsonSerializer.Deserialize<List<RuleComponentDto>>(rule.ComponentsJson);
                if (components == null || components.Count == 0) continue;

                int minPotentialQty = int.MaxValue;
                bool canCalculate = true;

                foreach (var component in components)
                {
                    // Optimization: If component is the source that triggered this, we could use the quantity from the notification?
                    // But the notification might not have quantity.
                    // So we fetch stock.
                    var compStock = await _meliClient.GetUserProductStockAsync(component.SourceItemId);
                    if (compStock == null)
                    {
                        canCalculate = false;
                        break;
                    }

                    int componentQty = component.Quantity > 0 ? component.Quantity : 1;
                    int possibleQty = (int)Math.Floor((double)compStock.Value.Quantity / componentQty);

                    if (possibleQty < minPotentialQty) minPotentialQty = possibleQty;
                }

                if (!canCalculate) continue;

                int targetQty = minPotentialQty;
                if (targetQty == int.MaxValue) targetQty = 0;

                var targetStock = await _meliClient.GetUserProductStockAsync(targetItemId);
                if (targetStock == null) continue;

                if (targetStock.Value.Quantity != targetQty)
                {
                    _logger.LogInformation("V2: Updating Target {TargetId}: {OldQty} -> {NewQty}", targetItemId, targetStock.Value.Quantity, targetQty);
                    await _meliClient.UpdateUserProductStockAsync(targetItemId, targetQty, targetStock.Value.Version);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "V2: Error processing target {TargetId}", targetItemId);
            }
        }

        return req.CreateResponse(HttpStatusCode.OK);
    }

    private static string ExtractLastSegment(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[parts.Length - 1] : string.Empty;
    }
}


