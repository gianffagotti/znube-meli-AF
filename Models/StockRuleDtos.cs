using System.Text.Json.Serialization;

namespace meli_znube_integration.Models;

public class StockRuleGroupDto
{
    public string MotherItemId { get; set; } = default!;
    public string MotherSku { get; set; } = default!;
    public string? MotherTitle { get; set; }
    public string? MotherThumbnail { get; set; }
    public List<StockRuleItemDto> Rules { get; set; } = new();
}

public class StockRuleItemDto
{
    public string MotherUserProductId { get; set; } = default!;
    public string ChildUserProductId { get; set; } = default!;
    public string Type { get; set; } = "FULL";
    public int PackQuantity { get; set; } = 1;
    public string ChildItemId { get; set; } = default!;
    public string ChildSku { get; set; } = default!;
    public string ChildTitle { get; set; } = default!;
}
