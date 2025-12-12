using Azure.Storage.Blobs;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace meli_znube_integration.Functions;

public class StockSyncWorker
{
    private readonly ILogger<StockSyncWorker> _logger;
    private readonly MeliClient _meliClient;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _semaphore = new(20);

    public StockSyncWorker(ILogger<StockSyncWorker> logger, MeliClient meliClient, IConfiguration configuration)
    {
        _logger = logger;
        _meliClient = meliClient;
        _configuration = configuration;
    }

    [Function("StockSyncWorker")]
    public async Task Run([TimerTrigger("0 0 5,16 * * *")] TimerInfo myTimer)
    {
        _logger.LogInformation("StockSyncWorker started.");

        try
        {
            // 1. Read Mappings
            var mappings = await LoadMappingsAsync();
            _logger.LogInformation($"Loaded {mappings.Count} mappings.");

            if (mappings.Count == 0)
            {
                _logger.LogWarning("No mappings found. Exiting.");
                return;
            }

            // 2. Fetch Flex Stock (Source)
            var sourceStock = await FetchFlexStockAsync(mappings);
            _logger.LogInformation($"Fetched stock for {sourceStock.Count} flex items/variations.");

            // 3. Sync Full Stock (Target)
            await SyncFullStockAsync(mappings, sourceStock);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in StockSyncWorker");
        }

        _logger.LogInformation("StockSyncWorker finished.");
    }

    private async Task<List<StockMappingEntry>> LoadMappingsAsync()
    {
        var connString = _configuration[EnvVars.Keys.AzureStorageConnectionString];
        if (string.IsNullOrWhiteSpace(connString))
        {
            _logger.LogError("Storage connection string not found.");
            return [];
        }

        var containerClient = new BlobContainerClient(connString, "stock-mappings");
        if (!await containerClient.ExistsAsync()) return [];

        var mappings = new ConcurrentBag<StockMappingEntry>();

        await foreach (var blob in containerClient.GetBlobsAsync())
        {
            try
            {
                var blobClient = containerClient.GetBlobClient(blob.Name);
                var content = await blobClient.DownloadContentAsync();
                var dict = JsonSerializer.Deserialize<Dictionary<string, StockMappingEntry>>(content.Value.Content.ToString(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (dict != null)
                {
                    foreach (var kvp in dict)
                    {
                        kvp.Value.Sku = kvp.Key;
                        mappings.Add(kvp.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error reading blob {blob.Name}");
            }
        }

        return mappings.ToList();
    }

    private async Task<Dictionary<string, int>> FetchFlexStockAsync(List<StockMappingEntry> mappings)
    {
        var flexItemIds = mappings
            .Where(m => m.Flex != null && !string.IsNullOrWhiteSpace(m.Flex.ItemId))
            .Select(m => m.Flex!.ItemId)
            .Distinct()
            .ToList();

        var stockMap = new ConcurrentDictionary<string, int>();
        var chunks = flexItemIds.Chunk(20);

        await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (chunk, ct) =>
        {
            try
            {
                var items = await _meliClient.GetItemsAsync(chunk);
                foreach (var item in items)
                {
                    // Map Item ID -> Quantity (for simple items)
                    stockMap.TryAdd(item.Id, item.AvailableQuantity);

                    // Map Variation ID -> Quantity
                    foreach (var variation in item.Variations)
                    {
                        stockMap.TryAdd(variation.Id.ToString(), variation.AvailableQuantity);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching flex items batch");
            }
        });

        return stockMap.ToDictionary(k => k.Key, v => v.Value);
    }

    private async Task SyncFullStockAsync(List<StockMappingEntry> mappings, Dictionary<string, int> sourceStock)
    {
        var tasks = mappings.Where(m => m.Full != null && m.Flex != null).Select(async mapping =>
        {
            await _semaphore.WaitAsync();
            try
            {
                await ProcessSingleSyncAsync(mapping, sourceStock);
            }
            finally
            {
                _semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task ProcessSingleSyncAsync(StockMappingEntry mapping, Dictionary<string, int> sourceStock)
    {
        try
        {
            // Determine Source Quantity
            int flexQuantity = 0;
            string sourceKey = !string.IsNullOrWhiteSpace(mapping.Flex!.VariationId) ? mapping.Flex.VariationId! : mapping.Flex.ItemId;

            if (!sourceStock.TryGetValue(sourceKey, out flexQuantity))
            {
                return;
            }

            string userProductId = mapping.Full!.UserProductId;
            if (string.IsNullOrWhiteSpace(userProductId)) return;

            // A. Get Current Stock
            var currentStock = await _meliClient.GetUserProductStockAsync(userProductId);
            if (currentStock == null) return;

            var (fullQuantity, version) = currentStock.Value;

            // B. Compare
            if (fullQuantity == flexQuantity) return;

            // C. Update
            _logger.LogInformation($"Updating SKU {mapping.Sku} (ItemID: {mapping.Full.ItemId}, UP: {userProductId}): Flex({flexQuantity}) vs Full({fullQuantity}).");

            bool success = await _meliClient.UpdateUserProductStockAsync(userProductId, flexQuantity, version);

            if (!success)
            {
                // Retry once logic
                _logger.LogWarning($"Conflict updating {userProductId}. Retrying...");
                currentStock = await _meliClient.GetUserProductStockAsync(userProductId);
                if (currentStock != null)
                {
                    (fullQuantity, version) = currentStock.Value;
                    if (fullQuantity != flexQuantity)
                    {
                        bool retrySuccess = await _meliClient.UpdateUserProductStockAsync(userProductId, flexQuantity, version);
                        if (retrySuccess)
                        {
                            _logger.LogInformation($"Retry successful for {userProductId}.");
                        }
                        else
                        {
                            _logger.LogError($"Retry failed for {userProductId}.");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error syncing {mapping.Full?.UserProductId}");
        }
    }

    // Models matching JSON structure
    public class StockMappingEntry
    {
        public StockNode? Full { get; set; }
        public StockNode? Flex { get; set; }
        public string Sku { get; set; } = string.Empty;
    }

    public class StockNode
    {
        public string ItemId { get; set; } = string.Empty;
        public string UserProductId { get; set; } = string.Empty;
        public string? VariationId { get; set; }
        public string Logistic { get; set; } = string.Empty;
    }
}
