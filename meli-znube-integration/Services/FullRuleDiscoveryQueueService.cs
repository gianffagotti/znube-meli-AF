using Azure.Storage.Queues;
using meli_znube_integration.Common;
using meli_znube_integration.Models.Dtos;
using System.Text.Json;

namespace meli_znube_integration.Services;

public class FullRuleDiscoveryQueueService
{
    private readonly QueueClient _queueClient;

    public FullRuleDiscoveryQueueService()
    {
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        var queueName = EnvVars.GetRequiredString(EnvVars.Keys.FullRuleDiscoveryQueueName);

        var options = new QueueClientOptions
        {
            MessageEncoding = QueueMessageEncoding.Base64
        };

        _queueClient = new QueueClient(connectionString, queueName, options);
        _queueClient.CreateIfNotExists();
    }

    public async Task EnqueueAsync(FullRuleDiscoveryQueueMessage message, CancellationToken cancellationToken = default)
    {
        if (message == null) return;
        var json = JsonSerializer.Serialize(message);
        await _queueClient.SendMessageAsync(json, cancellationToken);
    }
}
