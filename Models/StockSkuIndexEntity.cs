using Azure;
using Azure.Data.Tables;
using meli_znube_integration.Common;

namespace meli_znube_integration.Models;

/// <summary>
/// Index for FULL rules: lookup by SKU to get target item IDs.
/// PartitionKey = normalized SKU, RowKey = TargetItemId.
/// </summary>
public class StockSkuIndexEntity : ITableEntity
{
    /// <summary>PartitionKey: normalized SKU (e.g. upper case, trimmed).</summary>
    public string PartitionKey { get; set; } = default!;

    /// <summary>RowKey: TargetItemId (the publication updated by the FULL rule).</summary>
    public string RowKey { get; set; } = default!;

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>Always FULL for this index.</summary>
    public string RuleType { get; set; } = StockRuleTypes.Full;
}
