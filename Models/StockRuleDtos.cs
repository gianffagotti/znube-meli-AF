using System.Text.Json.Serialization;

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
    public string RuleType { get; set; } = "FULL"; // "FULL", "PACK", "COMBO"

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