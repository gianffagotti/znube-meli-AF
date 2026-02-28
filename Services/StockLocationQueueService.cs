using Azure.Storage.Queues;
using System.Text.Json;
using meli_znube_integration.Common;
using meli_znube_integration.Models.Dtos;

namespace meli_znube_integration.Services;

public class StockLocationQueueService
{
    private readonly QueueClient _queueClient;

    public StockLocationQueueService()
    {
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        var queueName = EnvVars.GetRequiredString(EnvVars.Keys.StockWebhookQueueName);
        _queueClient = new QueueClient(connectionString, queueName);
        _queueClient.CreateIfNotExists();
    }

    public async Task EnqueueAsync(StockLocationQueueMessage message, CancellationToken cancellationToken = default)
    {
        if (message == null) return;
        var json = JsonSerializer.Serialize(message);
        await _queueClient.SendMessageAsync(json, cancellationToken);
    }
}
