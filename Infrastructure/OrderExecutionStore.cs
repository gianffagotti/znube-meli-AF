using Azure;
using Azure.Data.Tables;
using meli_znube_integration.Common;

namespace meli_znube_integration.Infrastructure;

/// <summary>
/// Unified order/pack execution store using Azure Table Storage. Spec 02.
/// Handles idempotency (Already Done) and concurrency (Processing now). No Blob Storage.
/// </summary>
public class OrderExecutionStore
{
    private const string PartitionKeyValue = "OrderExec";
    private const string StatusProcessing = "Processing";
    private const string StatusDone = "Done";

    private readonly TableClient _tableClient;

    public OrderExecutionStore()
    {
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        var tableName = EnvVars.GetString(EnvVars.Keys.OrderExecutionTableName, "OrderExecution");
        _tableClient = new TableClient(connectionString, tableName);
        _tableClient.CreateIfNotExists();
    }

    /// <summary>
    /// Tries to start execution for the given key (OrderId or PackId).
    /// Returns true if this caller won the lock and should process; false if locked by another or already done.
    /// </summary>
    public async Task<bool> TryStartExecutionAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var entity = new OrderExecutionEntity
        {
            PartitionKey = PartitionKeyValue,
            RowKey = key,
            Status = StatusProcessing,
            StartedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _tableClient.AddEntityAsync(entity, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Entity already exists: another process is processing or already done
            return false;
        }
    }

    /// <summary>
    /// Marks execution as done for the given key. Call after processing (success or failure).
    /// </summary>
    public async Task MarkDoneAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        var entity = new OrderExecutionEntity
        {
            PartitionKey = PartitionKeyValue,
            RowKey = key,
            Status = StatusDone,
            StartedAt = DateTimeOffset.UtcNow
        };
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }
}

internal class OrderExecutionEntity : ITableEntity
{
    public string PartitionKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Status { get; set; } = "Processing";
    public DateTimeOffset? StartedAt { get; set; }
}
