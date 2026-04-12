using Azure;
using Azure.Data.Tables;

namespace meli_znube_integration.Models;

public class StockSourceIndexEntity : ITableEntity
{
    // PartitionKey: SourceItemId (The ID of the component/input item)
    public string PartitionKey { get; set; } = default!;

    // RowKey: TargetItemId (The ID of the rule that uses this source)
    public string RowKey { get; set; } = default!;

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string RuleType { get; set; } = default!;
}
