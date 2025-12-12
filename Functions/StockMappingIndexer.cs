using Azure.Storage.Blobs;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace meli_znube_integration.Functions;

public class StockMappingIndexer
{
    private readonly ILogger<StockMappingIndexer> _logger;
    private readonly MeliClient _meliClient;
    private readonly MeliAuth _meliAuth;
    private readonly IConfiguration _configuration;

    public StockMappingIndexer(ILogger<StockMappingIndexer> logger, MeliClient meliClient, MeliAuth meliAuth, IConfiguration configuration)
    {
        _logger = logger;
        _meliClient = meliClient;
        _meliAuth = meliAuth;
        _configuration = configuration;
    }

    [Function("StockMappingIndexer")]
    public async Task Run([TimerTrigger("0 0 4 * * *", RunOnStartup = true)] TimerInfo myTimer)
    {
        _logger.LogInformation("\u001b[32mStockMappingIndexer executed at: {DateTime.Now}.\u001b[0m", DateTime.Now);

        try
        {
            // 0. Autenticación
            var userId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);

            // 1. Estrategia de Obtención de Datos (Crawler) & 2. Lógica de Negocio (Procesamiento)
            // Usamos ConcurrentDictionary para thread-safety durante el procesamiento paralelo
            var mapping = new System.Collections.Concurrent.ConcurrentDictionary<string, StockMappingEntry>();

            string? scrollId = null;
            bool hasMore = true;
            int totalProcessed = 0;

            _logger.LogInformation("Iniciando escaneo y procesamiento de items...");

            // Configurar opciones de paralelismo
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 2 };

            while (hasMore)
            {
                MeliScanResponse? scanResult = null;
                try
                {
                    scanResult = await _meliClient.ScanItemsAsync(userId, scrollId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning items page. Aborting scan.");
                    hasMore = false;
                    break;
                }

                if (scanResult == null || scanResult.Results == null || scanResult.Results.Count == 0)
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

            var skuAttr = variation.Attributes.FirstOrDefault(a => a.Id == "SELLER_SKU");
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName;

            if (!string.IsNullOrWhiteSpace(variation.SellerCustomField)) return variation.SellerCustomField;
        }
        else
        {
            // Item simple
            if (!string.IsNullOrWhiteSpace(item.SellerCustomField)) return item.SellerCustomField;

            var skuAttr = item.Attributes.FirstOrDefault(a => a.Id == "SELLER_SKU");
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName;
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
        var containerClient = new BlobContainerClient(_configuration[EnvVars.Keys.AzureStorageConnectionString], containerName);
        await containerClient.CreateIfNotExistsAsync();

        var shards = mapping.GroupBy(x => GetShardKey(x.Key));

        JsonSerializerOptions options = new() { WriteIndented = true };
        var tasks = shards.Select(async shard =>
        {
            var fileName = $"{shard.Key}.json";
            var blobClient = containerClient.GetBlobClient(fileName);
            var content = shard.ToDictionary(x => x.Key, x => x.Value);

            using var ms = new MemoryStream();
            await JsonSerializer.SerializeAsync(ms, content, options);
            ms.Position = 0;
            await blobClient.UploadAsync(ms, overwrite: true);
        });

        await Task.WhenAll(tasks);
    }

    private static char GetShardKey(string sku)
    {
        if (string.IsNullOrEmpty(sku)) return '_';
        return char.ToUpperInvariant(sku[0]);
    }

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
