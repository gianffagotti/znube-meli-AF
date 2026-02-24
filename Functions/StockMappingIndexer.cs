using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace meli_znube_integration.Functions;

public class StockMappingIndexer
{
    private readonly ILogger<StockMappingIndexer> _logger;
    private readonly IMeliApiClient _meliClient;
    private readonly StockMappingService _stockMappingService;

    public StockMappingIndexer(ILogger<StockMappingIndexer> logger, IMeliApiClient meliClient, StockMappingService stockMappingService)
    {
        _logger = logger;
        _meliClient = meliClient;
        _stockMappingService = stockMappingService;
    }

    [Function("StockMappingIndexer")]
    public async Task Run([TimerTrigger("0 0 4 * * *")] TimerInfo myTimer)
    {
        _logger.LogInformation("\u001b[32mStockMappingIndexer executed at: {DateTime.Now}.\u001b[0m", DateTime.Now);

        if (!EnvVars.GetBool(EnvVars.Keys.EnableJobIndexer, true))
        {
            _logger.LogWarning("Job 'StockMappingIndexer' is disabled via configuration.");
            return;
        }

        try
        {
            // 0. Autenticación
            var userId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);

            // 1. Estrategia de Obtención de Datos (Crawler) & 2. Lógica de Negocio (Procesamiento)
            // Usamos ConcurrentDictionary para thread-safety durante el procesamiento paralelo
            var mapping = new ConcurrentDictionary<string, StockMappingEntry>();

            string? scrollId = null;
            bool hasMore = true;
            int totalProcessed = 0;

            _logger.LogInformation("Iniciando escaneo y procesamiento de items...");

            // Configurar opciones de paralelismo
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 2 };

            while (hasMore)
            {
                MeliScanResponseDto? scanResult = null;
                try
                {
                    scanResult = await _meliClient.ScanItemsAsync(long.Parse(userId), scrollId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning items page. Aborting scan.");
                    hasMore = false;
                    break;
                }

                if (scanResult?.Results == null || scanResult.Results.Count == 0)
                {
                    hasMore = false;
                    break;
                }

                scrollId = scanResult.ScrollId;

                // Hidratación por lotes (10 items - reducido para evitar timeouts)
                var chunks = scanResult.Results.Chunk(10);

                await Parallel.ForEachAsync(chunks, parallelOptions, async (chunk, ct) =>
                {
                    try
                    {
                        var itemsDetails = await _meliClient.GetItemsAsync(chunk);
                        foreach (var item in itemsDetails)
                        {
                            ProcessItem(item, mapping);
                        }
                        Interlocked.Add(ref totalProcessed, itemsDetails.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error procesando lote de items. Iniciando fallback uno a uno.");

                        // Fallback: Procesar uno a uno
                        foreach (var itemId in chunk)
                        {
                            try
                            {
                                var singleItemDetails = await _meliClient.GetItemsAsync([itemId]);
                                if (singleItemDetails != null && singleItemDetails.Count != 0)
                                {
                                    ProcessItem(singleItemDetails.First(), mapping);
                                    Interlocked.Increment(ref totalProcessed);
                                }
                            }
                            catch (Exception innerEx)
                            {
                                _logger.LogError(innerEx, "Error procesando item individual {itemId} en fallback.", itemId);
                            }
                        }
                    }
                });
            }

            _logger.LogInformation("Escaneo finalizado. Total items procesados: {totalProcessed}", totalProcessed);

            // Filtrar SKUs incompletos (deben tener FULL y FLEX)
            var finalMapping = mapping.Where(x => x.Value.Full != null && x.Value.Flex != null)
                                      .ToDictionary(x => x.Key, x => x.Value);

            _logger.LogInformation("Mapeo construido. SKUs completos encontrados: {finalMapping.Count}", finalMapping.Count);

            // 3. Estrategia de Persistencia (Azure Blob Storage + Sharding)
            await PersistMappingsAsync(finalMapping, "stock-mappings");

            // 4. Guardar SKUs que están en FULL pero no en FLEX
            var missingFlexMapping = mapping.Where(x => x.Value.Full != null && x.Value.Flex == null)
                                            .ToDictionary(x => x.Key, x => x.Value);

            _logger.LogInformation("Guardando SKUs con FULL pero sin FLEX: {missingFlexMapping.Count}", missingFlexMapping.Count);

            await PersistMappingsAsync(missingFlexMapping, "stock-mappings-missing-flex");

            _logger.LogInformation("\u001b[32mStockMappingIndexer finalizado exitosamente.\u001b[0m");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error crítico en StockMappingIndexer.");
            throw;
        }
    }

    private static void ProcessItem(MeliItem item, ConcurrentDictionary<string, StockMappingEntry> mapping)
    {
        // Identificar Logística
        var logisticType = item.Shipping?.LogisticType;
        bool isFull = string.Equals(logisticType, "fulfillment", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(logisticType)) return;

        // Iterar variaciones si existen, sino usar el item base
        if (item.Variations != null && item.Variations.Count != 0)
        {
            foreach (var variation in item.Variations)
            {
                var sku = ExtractSku(item, variation);
                if (!string.IsNullOrWhiteSpace(sku))
                {
                    AddToMapping(mapping, sku!, item.Id, variation.UserProductId, variation.Id.ToString(), logisticType!, isFull);
                }
            }
        }
        else
        {
            var sku = ExtractSku(item, null);
            if (!string.IsNullOrWhiteSpace(sku))
            {
                AddToMapping(mapping, sku!, item.Id, item.UserProductId, null, logisticType!, isFull);
            }
        }
    }

    private static string? ExtractSku(MeliItem item, MeliVariation? variation)
    {
        if (variation != null)
        {
            // Intento 1: variation.seller_custom_field (Proxy de seller_sku)
            // Intento 2: Attributes (id === 'SELLER_SKU')

            var skuAttr = variation.Attributes.FirstOrDefault(a => a.Id == MeliConstants.SellerSkuAttributeId);
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName.ToUpper();

            if (!string.IsNullOrWhiteSpace(variation.SellerCustomField)) return variation.SellerCustomField.ToUpper();
        }
        else
        {
            // Item simple
            if (!string.IsNullOrWhiteSpace(item.SellerCustomField)) return item.SellerCustomField.ToUpper();

            var skuAttr = item.Attributes.FirstOrDefault(a => a.Id == MeliConstants.SellerSkuAttributeId);
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName.ToUpper();
        }

        return null;
    }

    private static void AddToMapping(ConcurrentDictionary<string, StockMappingEntry> mapping, string sku, string id, string userProductId, string? variationId, string logisticType, bool isFull)
    {
        mapping.AddOrUpdate(sku,
            // Add Factory
            (key) =>
            {
                var entry = new StockMappingEntry();
                var node = new StockNode { ItemId = id, UserProductId = userProductId, VariationId = variationId, Logistic = logisticType };
                if (isFull) entry.Full = node;
                else entry.Flex = node;
                return entry;
            },
            // Update Factory
            (key, existingEntry) =>
            {
                var node = new StockNode { ItemId = id, UserProductId = userProductId, VariationId = variationId, Logistic = logisticType };

                // Usamos lock para asegurar atomicidad en la actualización de las propiedades del objeto entry
                // Aunque ConcurrentDictionary es thread-safe para agregar/reemplazar claves, modificar el objeto valor requiere sincronización si no es inmutable.
                lock (existingEntry)
                {
                    if (isFull) existingEntry.Full = node;
                    else existingEntry.Flex = node;
                }
                return existingEntry;
            });
    }

    private async Task PersistMappingsAsync(Dictionary<string, StockMappingEntry> mapping, string containerName)
    {
        var serviceMapping = mapping.ToDictionary(
            kvp => kvp.Key,
            kvp => new Services.StockMappingEntry
            {
                Full = kvp.Value.Full == null ? null : new Services.StockNode
                {
                    ItemId = kvp.Value.Full.ItemId,
                    UserProductId = kvp.Value.Full.UserProductId,
                    VariationId = kvp.Value.Full.VariationId,
                    Logistic = kvp.Value.Full.Logistic
                },
                Flex = kvp.Value.Flex == null ? null : new Services.StockNode
                {
                    ItemId = kvp.Value.Flex.ItemId,
                    UserProductId = kvp.Value.Flex.UserProductId,
                    VariationId = kvp.Value.Flex.VariationId,
                    Logistic = kvp.Value.Flex.Logistic
                }
            });

        await _stockMappingService.PersistMappingsAsync(serviceMapping, containerName);
    }

    private static char GetShardKey(string sku)
    {
        if (string.IsNullOrEmpty(sku)) return '_';
        return char.ToUpperInvariant(sku[0]);
    }

    // Internal classes kept for compilation compatibility with existing code in this file
    // In a full refactor, we would update the whole file to use the service types directly
    public class StockMappingEntry
    {
        public StockNode? Full { get; set; }
        public StockNode? Flex { get; set; }
    }

    public class StockNode
    {
        public string ItemId { get; set; } = string.Empty;
        public string UserProductId { get; set; } = string.Empty;
        public string? VariationId { get; set; }
        public string Logistic { get; set; } = string.Empty;
    }
}
