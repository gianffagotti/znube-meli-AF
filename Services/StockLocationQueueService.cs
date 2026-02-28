using Azure.Storage.Queues;
using meli_znube_integration.Common;
using meli_znube_integration.Models.Dtos;
using System.Text.Json;

namespace meli_znube_integration.Services;

public class StockLocationQueueService
{
    private readonly QueueClient _queueClient;

    public StockLocationQueueService()
    {
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        var queueName = EnvVars.GetRequiredString(EnvVars.Keys.StockWebhookQueueName);

        // AGREGAR ESTAS OPCIONES: Obliga al cliente a usar Base64
        var options = new QueueClientOptions
        {
            MessageEncoding = QueueMessageEncoding.Base64
        };

        // Pasamos las opciones al crear el QueueClient
        _queueClient = new QueueClient(connectionString, queueName, options);
        _queueClient.CreateIfNotExists();
    }

    public async Task EnqueueAsync(StockLocationQueueMessage message, CancellationToken cancellationToken = default)
    {
        if (message == null) return;
        var json = JsonSerializer.Serialize(message);

        // Ahora esto lo enviará codificado en Base64 de forma transparente
        await _queueClient.SendMessageAsync(json, cancellationToken);
    }
}