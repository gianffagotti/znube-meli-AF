using System.Text.Json.Serialization;
using meli_znube_integration.Common;

namespace meli_znube_integration.Models;

public class StockRuleDto
{
    [JsonPropertyName("targetItemId")]
    public string TargetItemId { get; set; } = default!;

    [JsonPropertyName("targetTitle")]
    public string TargetTitle { get; set; } = default!;

    [JsonPropertyName("targetThumbnail")]
    public string? TargetThumbnail { get; set; }
    
    [JsonPropertyName("targetSku")]
    public string TargetSku { get; set; } = default!;

    [JsonPropertyName("ruleType")]
    public string RuleType { get; set; } = StockRuleTypes.Full;

    /// <summary>FULL rules: true if one or more variant SKUs were not found in Znube at save time.</summary>
    [JsonPropertyName("isIncomplete")]
    public bool IsIncomplete { get; set; }

    /// <summary>PACK rules: default pack size for fallbacks. Spec V2.</summary>
    [JsonPropertyName("defaultPackQuantity")]
    public int DefaultPackQuantity { get; set; } = 1;

    [JsonPropertyName("components")]
    public List<RuleComponentDto> Components { get; set; } = [];

    [JsonPropertyName("mappings")]
    public List<VariantMappingDto> Mappings { get; set; } = [];
}

public class RuleComponentDto
{
    [JsonPropertyName("sourceItemId")]
    public string SourceItemId { get; set; } = default!;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; } = 1;
}

public class VariantMappingDto
{
    [JsonPropertyName("targetVariantId")]
    public string TargetVariantId { get; set; } = default!;
    
    [JsonPropertyName("targetSku")]
    public string TargetSku { get; set; } = default!;

    /// <summary>Optional per-variant pack size override. Null = use rule DefaultPackQuantity.</summary>
    [JsonPropertyName("packQuantity")]
    public int? PackQuantity { get; set; }

    /// <summary>Strategy: "Explicit" (use SourceMatches) or "DynamicSize" (pool by MatchSize).</summary>
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "Explicit";

    /// <summary>Used when Strategy == "DynamicSize". e.g. "M", "L", "42".</summary>
    [JsonPropertyName("matchSize")]
    public string? MatchSize { get; set; }

    [JsonPropertyName("sourceMatches")]
    public List<RuleSourceMatchDto> SourceMatches { get; set; } = new();
}

public class RuleSourceMatchDto
{
    [JsonPropertyName("sourceItemId")]
    public string SourceItemId { get; set; } = default!;
    
    [JsonPropertyName("sourceVariantId")]
    public string SourceVariantId { get; set; } = default!;
    
    [JsonPropertyName("sourceSku")]
    public string SourceSku { get; set; } = default!;
    
    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}