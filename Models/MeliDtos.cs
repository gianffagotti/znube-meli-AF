using System.Text.Json.Serialization;

namespace meli_znube_integration.Models;

public class MeliItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("user_product_id")]
    public string UserProductId { get; set; } = string.Empty;

    [JsonPropertyName("seller_custom_field")]
    public string? SellerCustomField { get; set; }

    [JsonPropertyName("shipping")]
    public MeliShipping? Shipping { get; set; }

    [JsonPropertyName("attributes")]
    public List<MeliAttribute> Attributes { get; set; } = [];

    [JsonPropertyName("variations")]
    public List<MeliVariation> Variations { get; set; } = [];

    [JsonPropertyName("available_quantity")]
    public int AvailableQuantity { get; set; }
}

public class MeliShipping
{
    [JsonPropertyName("logistic_type")]
    public string? LogisticType { get; set; }
}

public class MeliAttribute
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("value_name")]
    public string? ValueName { get; set; }
}

public class MeliVariation
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("seller_custom_field")]
    public string? SellerCustomField { get; set; }

    [JsonPropertyName("user_product_id")]
    public string UserProductId { get; set; } = string.Empty;
    
    [JsonPropertyName("attributes")]
    public List<MeliAttribute> Attributes { get; set; } = [];

    [JsonPropertyName("available_quantity")]
    public int AvailableQuantity { get; set; }
}

public class MeliScanResponse
{
    [JsonPropertyName("results")]
    public List<string> Results { get; set; } = [];

    [JsonPropertyName("scroll_id")]
    public string? ScrollId { get; set; }
}

public class MeliUserProductStockResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("locations")]
    public List<MeliStockLocation> Locations { get; set; } = [];
}

public class MeliStockLocation
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}
