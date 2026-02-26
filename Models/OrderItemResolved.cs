namespace meli_znube_integration.Models;

public class OrderItemResolved
{
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public string? ProductLabel { get; set; }
    public string? OrderItemId { get; set; }
    public string? RuleType { get; set; }
    public string? SourceItemId { get; set; }
}
