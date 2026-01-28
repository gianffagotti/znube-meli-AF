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

    [JsonPropertyName("ruleType")]
    public string RuleType { get; set; } = "FULL"; // "FULL", "PACK", "COMBO"

    [JsonPropertyName("components")]
    public List<RuleComponentDto> Components { get; set; } = new();

    // Optional: For mapping logic if needed in frontend
    [JsonPropertyName("mapping")]
    public Dictionary<string, string>? Mapping { get; set; }
}

public class RuleComponentDto
{
    [JsonPropertyName("sourceItemId")]
    public string SourceItemId { get; set; } = default!;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; } = 1;
}

