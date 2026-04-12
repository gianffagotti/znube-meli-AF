using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using meli_znube_integration.Common;

namespace meli_znube_integration.Functions;

public class MissingFlexItemsFunction
{
    private readonly ILogger<MissingFlexItemsFunction> _logger;

    public MissingFlexItemsFunction(ILogger<MissingFlexItemsFunction> logger)
    {
        _logger = logger;
    }

    [Function("GetMissingFlexItems")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        _logger.LogInformation("Processing GetMissingFlexItems request.");

        try
        {
            string connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
            string containerName = "stock-mappings-missing-flex";
            
            var containerClient = new BlobContainerClient(connectionString, containerName);

            if (!await containerClient.ExistsAsync())
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await errorResponse.WriteStringAsync($"Container '{containerName}' not found. Please check your storage configuration.");
                return errorResponse;
            }

            // Dictionary to group by ItemId: ItemId -> List of SKUs
            var groupedItems = new Dictionary<string, List<string>>();

            await foreach (var blobItem in containerClient.GetBlobsAsync())
            {
                var blobClient = containerClient.GetBlobClient(blobItem.Name);
                var downloadResult = await blobClient.DownloadContentAsync();
                var jsonContent = downloadResult.Value.Content.ToString();

                try
                {
                    var items = JsonSerializer.Deserialize<Dictionary<string, StockMappingItem>>(jsonContent);

                    if (items != null)
                    {
                        foreach (var kvp in items)
                        {
                            var sku = kvp.Key;
                            var value = kvp.Value;

                            if (value.Flex == null && value.Full != null && !string.IsNullOrEmpty(value.Full.ItemId))
                            {
                                if (!groupedItems.ContainsKey(value.Full.ItemId))
                                {
                                    groupedItems[value.Full.ItemId] = new List<string>();
                                }
                                groupedItems[value.Full.ItemId].Add(sku);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing blob {blobItem.Name}");
                    // Continue processing other blobs even if one fails
                }
            }

            var resultBuilder = new StringBuilder();
            foreach (var itemGroup in groupedItems)
            {
                resultBuilder.AppendLine(itemGroup.Key);
                foreach (var sku in itemGroup.Value)
                {
                    resultBuilder.AppendLine($"       {sku}");
                }
            }

            var resultText = resultBuilder.ToString();
            
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            response.Headers.Add("Content-Disposition", "attachment; filename=missing_flex_items.txt");
            
            await response.WriteStringAsync(resultText);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in GetMissingFlexItems");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Internal Server Error: {ex.Message}");
            return errorResponse;
        }
    }

    private class StockMappingItem
    {
        public MappingDetail? Full { get; set; }
        public MappingDetail? Flex { get; set; }
    }

    private class MappingDetail
    {
        public string? ItemId { get; set; }
        public string? UserProductId { get; set; }
        public string? VariationId { get; set; }
        public string? Logistic { get; set; }
    }
}
