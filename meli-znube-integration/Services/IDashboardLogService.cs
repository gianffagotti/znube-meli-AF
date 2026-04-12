namespace meli_znube_integration.Services;

/// <summary>Service for dashboard log entries (table: PartitionKey = date yyyy-MM-dd, RowKey = timestamp_guid).</summary>
public interface IDashboardLogService
{
    /// <summary>Appends a log entry for today's partition. RowKey is generated (timestamp_guid).</summary>
    Task AppendLogAsync(string severity, string category, string message, string? detailsJson = null, IEnumerable<string>? entityIds = null, CancellationToken cancellationToken = default);

    /// <summary>Gets log entries for the given date (yyyy-MM-dd), optionally filtered by severity and/or category.</summary>
    Task<IReadOnlyList<DashboardLogEntry>> GetLogsAsync(string date, string? severity = null, string? category = null, CancellationToken cancellationToken = default);

    /// <summary>Marks a log entry as read.</summary>
    Task MarkAsReadAsync(string partitionKey, string rowKey, CancellationToken cancellationToken = default);
}

/// <summary>DTO for a dashboard log entry (API response).</summary>
public class DashboardLogEntry
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Details { get; set; }
    public IReadOnlyList<string> EntityIds { get; set; } = Array.Empty<string>();
    public bool IsRead { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
}
