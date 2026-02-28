using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;

namespace meli_znube_integration.Services;

[Obsolete("V1 stock-locations logic is deprecated and kept for reference.")]
public class LegacyStockLocationProcessor
{
    private readonly IMeliApiClient _meliClient;
    private readonly StockMappingService _stockMappingService;
    private readonly IStockSyncSourceService _stockSyncSourceService;
    private readonly ILogger<LegacyStockLocationProcessor> _logger;

    public LegacyStockLocationProcessor(
        IMeliApiClient meliClient,
        StockMappingService stockMappingService,
        IStockSyncSourceService stockSyncSourceService,
        ILogger<LegacyStockLocationProcessor> logger)
    {
        _meliClient = meliClient;
        _stockMappingService = stockMappingService;
        _stockSyncSourceService = stockSyncSourceService;
        _logger = logger;
    }

    public async Task<HttpResponseData> ProcessAsync(HttpRequestData req, string? resource)
    {
        var userId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
        if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(userId))
        {
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var userProductId = StockLocationHelpers.ExtractUserProductId(resource);
        if (string.IsNullOrWhiteSpace(userProductId))
        {
            _logger.LogWarning("No se pudo extraer user_product_id de: {Resource}", resource);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var search = await _meliClient.SearchItemsAsync(long.Parse(userId), new MeliItemSearchQuery { UserProductId = userProductId });
        var itemId = search?.Results?.FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(itemId))
        {
            _logger.LogInformation("No se encontró Item ID para user_product_id: {UserProductId}. Ignorando.", userProductId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

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
                sku = StockLocationHelpers.ExtractSku(item, variation);
            }
        }
        else
        {
            sku = StockLocationHelpers.ExtractSku(item, null);
        }

        if (string.IsNullOrWhiteSpace(sku))
        {
            _logger.LogWarning("No se pudo extraer SKU para Item: {ItemId}, UserProduct: {UserProductId}", itemId, userProductId);
            return req.CreateResponse(HttpStatusCode.OK);
        }

        var sourceStock = await _meliClient.GetUserProductStockAsync(userProductId);
        if (sourceStock == null)
        {
            _logger.LogWarning("No se pudo obtener stock del source: {UserProductId}", userProductId);
            return req.CreateResponse(HttpStatusCode.OK);
        }
        var sourceQuantity = sourceStock.Value.Quantity;

        string? targetUserProductId = await _stockMappingService.GetTargetUserProductIdAsync(sku!);

        if (string.IsNullOrWhiteSpace(targetUserProductId))
        {
            var skuSearch = await _meliClient.SearchItemsAsync(long.Parse(userId), new MeliItemSearchQuery { SellerSku = sku! });
            var targetItemId = skuSearch?.Results?.FirstOrDefault()?.Id;
            if (!string.IsNullOrWhiteSpace(targetItemId))
            {
                var targetItems = await _meliClient.GetItemsAsync([targetItemId!]);
                var targetItem = targetItems.FirstOrDefault();
                if (targetItem != null && string.Equals(targetItem.Shipping?.LogisticType, "fulfillment", StringComparison.OrdinalIgnoreCase))
                {
                    if (targetItem.Variations != null && targetItem.Variations.Count > 0)
                    {
                        foreach (var v in targetItem.Variations)
                        {
                            var vSku = StockLocationHelpers.ExtractSku(targetItem, v);
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
}
