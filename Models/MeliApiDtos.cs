using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace meli_znube_integration.Models.Dtos;

/// <summary>
/// Converts JSON number or string tokens to string. Mercado Libre API may return IDs as numbers.
/// </summary>
public class JsonStringFromNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} when parsing string.")
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}

// --- Shipment DTOs ---
public class MeliShipmentDto
{
    public long Id { get; set; }
    public string? Mode { get; set; }
    public MeliLogisticDto? Logistic { get; set; }
    [JsonIgnore]
    public string? LogisticType => Logistic?.Type;
    [JsonPropertyName("receiver_address")]
    public MeliReceiverAddressDto? ReceiverAddress { get; set; }
    public MeliDestinationDto? Destination { get; set; }
}

public class MeliLogisticDto
{
    public string? Type { get; set; }
}

public class MeliDestinationDto
{
    [JsonPropertyName("shipping_address")]
    public MeliReceiverAddressDto? ShippingAddress { get; set; }
}

public class MeliReceiverAddressDto
{
    public MeliCityDto? City { get; set; }
    [JsonPropertyName("address_line")]
    public string? AddressLine { get; set; }
}

public class MeliCityDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

// --- Order DTOs ---
public class MeliOrderDto
{
    [JsonConverter(typeof(JsonStringFromNumberConverter))]
    public string? Id { get; set; }
    [JsonPropertyName("pack_id")]
    [JsonConverter(typeof(JsonStringFromNumberConverter))]
    public string? PackId { get; set; }
    [JsonPropertyName("date_created")]
    public string? DateCreated { get; set; }
    public MeliOrderShippingDto? Shipping { get; set; }
    public MeliOrderBuyerDto? Buyer { get; set; }
    [JsonPropertyName("order_items")]
    public List<MeliOrderItemDto> OrderItems { get; set; } = new();
}

public class MeliOrderShippingDto
{
    [JsonConverter(typeof(JsonStringFromNumberConverter))]
    public string? Id { get; set; }
}

public class MeliOrderWrapperDto
{
    public MeliOrderDto? Order { get; set; }
}

public class MeliOrderBuyerDto
{
    [JsonConverter(typeof(JsonStringFromNumberConverter))]
    public string? Id { get; set; }
    public string? Nickname { get; set; }
    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }
    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }
}

public class MeliOrderItemDto
{
    public MeliItemShortDto? Item { get; set; }
    public int Quantity { get; set; }
    [JsonPropertyName("seller_sku")]
    public string? SellerSku { get; set; }
}

public class MeliItemShortDto
{
    [JsonConverter(typeof(JsonStringFromNumberConverter))]
    public string? Id { get; set; }
    public string? Title { get; set; }
    [JsonPropertyName("seller_sku")]
    public string? SellerSku { get; set; }
    [JsonPropertyName("user_product_id")]
    public string? UserProductId { get; set; }
}

// --- Pack DTO ---
public class MeliPackDto
{
    [JsonConverter(typeof(JsonStringFromNumberConverter))]
    public string? Id { get; set; }
    public List<MeliOrderDto> Orders { get; set; } = new();
}

// --- Notes DTOs ---
public class MeliNoteDto
{
    public string? Note { get; set; }
    [JsonPropertyName("date_created")]
    public string? DateCreated { get; set; }
}

public class MeliNotesResponseDto
{
    public List<MeliNoteResultDto> Results { get; set; } = new();
}

public class MeliNoteResultDto
{
    public string? Note { get; set; }
}

// --- Search/Scan DTOs ---
public class MeliSearchResponseDto
{
    public MeliPagingDto? Paging { get; set; }
    public List<MeliSearchResultDto> Results { get; set; } = new();
}

public class MeliSearchResultDto
{
    [JsonConverter(typeof(JsonStringFromNumberConverter))]
    public string? Id { get; set; }
    public string? Title { get; set; }
    [JsonPropertyName("seller_sku")]
    public string? SellerSku { get; set; }
    public string? Permalink { get; set; }
    [JsonPropertyName("pack_id")]
    [JsonConverter(typeof(JsonStringFromNumberConverter))]
    public string? PackId { get; set; }
    public MeliOrderBuyerDto? Buyer { get; set; }
}

public class MeliPagingDto
{
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
}

// Scan endpoint returns results as string IDs and scroll_id
public class MeliScanResponseDto
{
    public List<string> Results { get; set; } = new();
    [JsonPropertyName("scroll_id")]
    public string? ScrollId { get; set; }
}

// --- Stock/UserProduct DTOs ---
public class MeliUserProductStockResponseDto
{
    public List<MeliUserProductStockLocationDto> Locations { get; set; } = new();
}

public class MeliUserProductStockLocationDto
{
    public string Type { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

// --- Item (multiget) - reuse existing MeliItem from Models for full item body ---
// GetItemsAsync returns List<MeliItem> from Models.MeliItem (existing type)

// --- Search query helper ---
public class MeliItemSearchQuery
{
    public string? Query { get; set; }
    [JsonPropertyName("seller_sku")]
    public string? SellerSku { get; set; }
    [JsonPropertyName("user_product_id")]
    public string? UserProductId { get; set; }
    public string? Status { get; set; } = "active";
}
