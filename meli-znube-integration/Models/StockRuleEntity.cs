using Azure;
using Azure.Data.Tables;
using System.Runtime.Serialization;
using System.Text.Json;
using meli_znube_integration.Common;

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
    public string RuleType { get; set; } = StockRuleTypes.Full;

    /// <summary>FULL rules: true if one or more variant SKUs were not found in Znube at save time.</summary>
    public bool IsIncomplete { get; set; }

    /// <summary>PACK rules: default pack size for fallbacks. Spec V2.</summary>
    public int DefaultPackQuantity { get; set; } = 1;

    // Serialized list of components. Each component has SourceItemId and Quantity
    public string ComponentsJson { get; set; } = "[]"; 
    
    // Serialized logic for variant mapping. e.g., "Black Shirt L" maps to "Combo Black L"
    public string MappingJson { get; set; } = "[]";

    // Target Info (The item being updated)
    public string TargetItemId { get; set; } = default!;
    public string TargetSku { get; set; } = default!;
    public string TargetTitle { get; set; } = default!;
    public string? TargetThumbnail { get; set; }

    [IgnoreDataMember]
    public List<RuleVariantMapping> Mappings
    {
        get => string.IsNullOrEmpty(MappingJson)
            ? new List<RuleVariantMapping>()
            : JsonSerializer.Deserialize<List<RuleVariantMapping>>(MappingJson) ?? new List<RuleVariantMapping>();
        set => MappingJson = JsonSerializer.Serialize(value);
    }
}

public class RuleVariantMapping
{
    public string TargetVariantId { get; set; } // The ML Variant ID (UserProductId)
    public string TargetSku { get; set; }
    /// <summary>Optional per-variant pack size override. Null = use rule DefaultPackQuantity.</summary>
    public int? PackQuantity { get; set; }
    /// <summary>Strategy: "Explicit" or "DynamicSize".</summary>
    public string Strategy { get; set; } = "Explicit";
    /// <summary>Used when Strategy == "DynamicSize". e.g. "M", "L", "42".</summary>
    public string? MatchSize { get; set; }
    public List<RuleSourceMatch> SourceMatches { get; set; } = new();
}

public class RuleSourceMatch
{
    public string SourceItemId { get; set; }
    public string SourceVariantId { get; set; } // The ML Variant ID
    public string SourceSku { get; set; }
    public int Quantity { get; set; } // Override quantity per variant if needed, or inherit from Component
}
