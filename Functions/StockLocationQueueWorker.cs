using System.Text.Json;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Functions;

public class StockLocationQueueWorker
{
    private readonly StockLocationProcessor _processor;
    private readonly ILogger<StockLocationQueueWorker> _logger;

    public StockLocationQueueWorker(StockLocationProcessor processor, ILogger<StockLocationQueueWorker> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    [Function("StockLocationQueueWorker")]
    public async Task Run(
        [QueueTrigger("%STOCK_WEBHOOK_QUEUE_NAME%", Connection = "AZURE_STORAGE_CONNECTION_STRING")] string message,
        FunctionContext context)
    {
        StockLocationQueueMessage? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StockLocationQueueMessage>(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid stock queue message payload.");
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Resource))
        {
            _logger.LogWarning("Stock queue message missing resource.");
            return;
        }

        if (!string.Equals(payload.Topic, "stock-locations", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Ignoring queue message with topic {Topic}.", payload.Topic);
            return;
        }

        await _processor.ProcessAsync(payload.Resource, context.CancellationToken);
    }
}
