using Azure.Storage.Blobs;
using meli_znube_integration.Common;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace meli_znube_integration.Services;

public class StockMappingService
{
    private readonly IConfiguration _configuration;
    private readonly BlobContainerClient _containerClient;

    public StockMappingService(IConfiguration configuration)
    {
        _configuration = configuration;
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        _containerClient = new BlobContainerClient(connectionString, "stock-mappings");
    }

    public async Task<string?> GetTargetUserProductIdAsync(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;

        var shardKey = GetShardKey(sku);
        var fileName = $"{shardKey}.json";
        var blobClient = _containerClient.GetBlobClient(fileName);

        if (!await blobClient.ExistsAsync()) return null;

        using var stream = await blobClient.OpenReadAsync();
        var mapping = await JsonSerializer.DeserializeAsync<Dictionary<string, StockMappingEntry>>(stream);

        if (mapping != null && mapping.TryGetValue(sku, out var entry))
        {
            return entry.Full?.UserProductId;
        }

        return null;
    }

    public async Task PersistMappingsAsync(Dictionary<string, StockMappingEntry> mapping, string containerName)
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
