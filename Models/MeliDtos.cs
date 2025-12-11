using System.Text.Json.Serialization;

namespace meli_znube_integration.Models;

public class MeliItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("user_product_id")]
    public string UserProductId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("seller_custom_field")]
    public string? SellerCustomField { get; set; }

    [JsonPropertyName("shipping")]
    public MeliShipping? Shipping { get; set; }

    [JsonPropertyName("attributes")]
    public List<MeliAttribute> Attributes { get; set; } = new();

    [JsonPropertyName("variations")]
    public List<MeliVariation> Variations { get; set; } = new();
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

    [JsonPropertyName("attribute_combinations")]
    public List<MeliAttribute> AttributeCombinations { get; set; } = new();
    
    [JsonPropertyName("attributes")]
    public List<MeliAttribute> Attributes { get; set; } = new();
}

public class MeliScanResponse
{
    [JsonPropertyName("results")]
    public List<string> Results { get; set; } = new();

    [JsonPropertyName("scroll_id")]
    public string? ScrollId { get; set; }
}
