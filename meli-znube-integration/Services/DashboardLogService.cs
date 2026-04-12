using Azure.Data.Tables;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using System.Text.Json;

namespace meli_znube_integration.Services;

public class DashboardLogService : IDashboardLogService
{
    private readonly TableClient _tableClient;

    public DashboardLogService()
    {
        var connectionString = EnvVars.GetRequiredString(EnvVars.Keys.AzureStorageConnectionString);
        var tableName = EnvVars.GetString(EnvVars.Keys.DashboardLogsTableName, "DashboardLogs");
        _tableClient = new TableClient(connectionString, tableName);
        _tableClient.CreateIfNotExists();
    }

    /// <inheritdoc />
    public async Task AppendLogAsync(string severity, string category, string message, string? detailsJson = null, IEnumerable<string>? entityIds = null, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var partitionKey = now.ToString("yyyy-MM-dd");
        var rowKey = $"{now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";

        var entity = new DashboardLogEntity
        {
            PartitionKey = partitionKey,
            RowKey = rowKey,
            Severity = severity ?? "Info",
            Category = category ?? "",
            Message = message ?? "",
            Details = detailsJson,
            EntityIds = entityIds != null ? JsonSerializer.Serialize(entityIds.ToList()) : null,
            IsRead = false,
            Timestamp = now
        };

        await _tableClient.AddEntityAsync(entity, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardLogEntry>> GetLogsAsync(string date, string? severity = null, string? category = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(date)) return Array.Empty<DashboardLogEntry>();

        var partitionKey = date.Replace("'", "''");
        var filter = $"PartitionKey eq '{partitionKey}'";
        if (!string.IsNullOrWhiteSpace(severity))
            filter += $" and Severity eq '{severity.Replace("'", "''")}'";
        if (!string.IsNullOrWhiteSpace(category))
            filter += $" and Category eq '{category.Replace("'", "''")}'";

        var list = new List<DashboardLogEntry>();
        var query = _tableClient.QueryAsync<DashboardLogEntity>(filter: filter, cancellationToken: cancellationToken);
        await foreach (var e in query.WithCancellation(cancellationToken))
        {
            list.Add(MapToEntry(e));
        }
        // Newest first (RowKey is sortable timestamp_guid)
        list.Sort((a, b) => string.CompareOrdinal(b.RowKey, a.RowKey));
        return list;
    }

    /// <inheritdoc />
    public async Task MarkAsReadAsync(string partitionKey, string rowKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partitionKey) || string.IsNullOrWhiteSpace(rowKey)) return;

        try
        {
            var response = await _tableClient.GetEntityAsync<DashboardLogEntity>(partitionKey, rowKey, cancellationToken: cancellationToken);
            var entity = response.Value;
            entity.IsRead = true;
            await _tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, cancellationToken);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Log not found, ignore
        }
    }

    private static DashboardLogEntry MapToEntry(DashboardLogEntity e)
    {
        IReadOnlyList<string> entityIds = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(e.EntityIds))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(e.EntityIds);
                if (parsed != null) entityIds = parsed;
            }
            catch { /* ignore */ }
        }
        return new DashboardLogEntry
        {
            PartitionKey = e.PartitionKey,
            RowKey = e.RowKey,
            Severity = e.Severity,
            Category = e.Category,
            Message = e.Message,
            Details = e.Details,
            EntityIds = entityIds,
            IsRead = e.IsRead,
            Timestamp = e.Timestamp
        };
    }
}
