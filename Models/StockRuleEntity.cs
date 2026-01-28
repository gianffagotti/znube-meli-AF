using Azure;
using Azure.Data.Tables;

namespace meli_znube_integration.Models;

public class StockRuleEntity : ITableEntity
{
    // PartitionKey: SellerId
    public string PartitionKey { get; set; } = default!;

    // RowKey: TargetItemId (The ID of the Publication to update, e.g., the Combo ID)
    public string RowKey { get; set; } = default!;

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Config Properties
    public string RuleType { get; set; } = "FULL"; // "FULL", "PACK", "COMBO"
    
    // Serialized list of components. Each component has SourceItemId and Quantity
    public string ComponentsJson { get; set; } = "[]"; 
    
    // Serialized logic for variant mapping. e.g., "Black Shirt L" maps to "Combo Black L"
    public string MappingJson { get; set; } = "{}";

    // Target Info (The item being updated)
    public string TargetItemId { get; set; } = default!;
    public string TargetSku { get; set; } = default!;
    public string TargetTitle { get; set; } = default!;
    public string? TargetThumbnail { get; set; }
}
