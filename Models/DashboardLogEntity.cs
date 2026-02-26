using Azure;
using Azure.Data.Tables;

namespace meli_znube_integration.Models;

/// <summary>
/// Dashboard log entry. PartitionKey = date (yyyy-MM-dd), RowKey = timestamp_guid for uniqueness and sort order.
/// </summary>
public class DashboardLogEntity : ITableEntity
{
    /// <summary>PartitionKey: date in yyyy-MM-dd format.</summary>
    public string PartitionKey { get; set; } = default!;

    /// <summary>RowKey: sortable timestamp + guid, e.g. 20250224123045123_abc123...</summary>
    public string RowKey { get; set; } = default!;

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Severity { get; set; } = "Info"; // e.g. Info, Warning, Error
    public string Category { get; set; } = "";    // e.g. FullRuleDiscovery, StockSync
    public string Message { get; set; } = "";
    /// <summary>Optional JSON details.</summary>
    public string? Details { get; set; }
    /// <summary>Optional JSON array of entity IDs, e.g. ["item1", "item2"].</summary>
    public string? EntityIds { get; set; }
    public bool IsRead { get; set; }
}
