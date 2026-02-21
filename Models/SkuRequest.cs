namespace meli_znube_integration.Models;

public class SkuRequest
{
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public string? ProductLabel { get; set; }
}
