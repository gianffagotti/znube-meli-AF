using Azure;
using Azure.Data.Tables;

namespace meli_znube_integration.Models;

public class StockRuleEntity : ITableEntity
{
    // PartitionKey: MotherUserProductId (The source variant ID)
    public string PartitionKey { get; set; } = default!;

    // RowKey: ChildUserProductId (The target variant ID)
    public string RowKey { get; set; } = default!;

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Config Properties
    public string Type { get; set; } = "FULL"; // "FULL" or "PACK"
    public int PackQuantity { get; set; } = 1;

    // Info Properties (for UI cache)
    public string MotherItemId { get; set; } = default!;
    public string MotherSku { get; set; } = default!;
    public string? MotherTitle { get; set; }
    public string? MotherThumbnail { get; set; }

    public string ChildItemId { get; set; } = default!;
    public string ChildSku { get; set; } = default!;
    public string ChildTitle { get; set; } = default!;
}
