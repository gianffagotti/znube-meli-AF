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

    [JsonPropertyName("seller_sku")]
    public string? SellerSku { get; set; }

    [JsonPropertyName("shipping")]
    public MeliShipping? Shipping { get; set; }

    [JsonPropertyName("attributes")]
    public List<MeliAttribute> Attributes { get; set; } = [];

    [JsonPropertyName("variations")]
    public List<MeliVariation> Variations { get; set; } = [];

    [JsonPropertyName("available_quantity")]
    public int AvailableQuantity { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("price")]
    public decimal? Price { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("permalink")]
    public string? Permalink { get; set; }

    // --- Catalog / Family fields (MCP-documented: catalog-listing endpoint) ---
    /// <summary>True when this publication is a catalog listing. Always false for Legacy marketplace items.</summary>
    [JsonPropertyName("catalog_listing")]
    public bool CatalogListing { get; set; }

    /// <summary>Shared catalog product ID (family identifier). e.g. "MLA6005934". Null for Legacy items.</summary>
    [JsonPropertyName("catalog_product_id")]
    public string? CatalogProductId { get; set; }

    /// <summary>Relations to the original marketplace item and its variation. Populated for catalog listings created from marketplace items via optin.</summary>
    [JsonPropertyName("item_relations")]
    public List<MeliItemRelation> ItemRelations { get; set; } = [];
}

/// <summary>Relates a catalog listing to its originating marketplace item and variation. Spec: meli-catalog-dto-extensions.</summary>
public class MeliItemRelation
{
    /// <summary>ID of the original marketplace item (e.g. "MLA123456").</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Numeric ID of the source variation. Null when the original item had no variations.</summary>
    [JsonPropertyName("variation_id")]
    public long? VariationId { get; set; }

    /// <summary>Stock relation factor (typically 1).</summary>
    [JsonPropertyName("stock_relation")]
    public int StockRelation { get; set; }
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

    [JsonPropertyName("seller_sku")]
    public string? SellerSku { get => Attributes.FirstOrDefault(a => a.Id.Equals("seller_sku", StringComparison.CurrentCultureIgnoreCase))?.ValueName; }

    [JsonPropertyName("user_product_id")]
    public string UserProductId { get; set; } = string.Empty;

    [JsonPropertyName("attributes")]
    public List<MeliAttribute> Attributes { get; set; } = [];

    [JsonPropertyName("attribute_combinations")]
    public List<MeliAttribute> AttributesCombinations { get; set; } = [];

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

public class MeliOrder
{
    public List<MeliOrderItem> Items { get; set; } = [];
    public string? ShippingId { get; set; }
    public string? Id { get; set; }
    public string? PackId { get; set; }
    public DateTimeOffset? DateCreatedUtc { get; set; }
    public string? BuyerNickname { get; set; }
    public string? BuyerFirstName { get; set; }
}

public class MeliOrderItem
{
    public string Title { get; set; } = string.Empty;
    public string? SellerSku { get; set; }
    public string? ItemId { get; set; }
    public string? TargetSku { get; set; }
    public int Quantity { get; set; } = 1;
    public string? UserProductId { get; set; }
}