using System.Text.Json.Serialization;

namespace meli_znube_integration.Models;

public class StockRuleGroupDto
{
    [JsonPropertyName("motherItemId")]
    public string MotherItemId { get; set; } = default!;
    [JsonPropertyName("motherTitle")]
    public string? MotherTitle { get; set; }
    [JsonPropertyName("motherThumbnail")]
    public string? MotherThumbnail { get; set; }
    [JsonPropertyName("rules")]
    public List<StockRuleItemDto> Rules { get; set; } = new();
}

public class StockRuleItemDto
{
    [JsonPropertyName("motherUserProductId")]
    public string MotherUserProductId { get; set; } = default!;
    [JsonPropertyName("childUserProductId")]
    public string ChildUserProductId { get; set; } = default!;
    [JsonPropertyName("type")]
    public string Type { get; set; } = "FULL";
    [JsonPropertyName("packQuantity")]
    public int PackQuantity { get; set; } = 1;
    [JsonPropertyName("childItemId")]
    public string ChildItemId { get; set; } = default!;
    [JsonPropertyName("childTitle")]
    public string ChildTitle { get; set; } = default!;
}
