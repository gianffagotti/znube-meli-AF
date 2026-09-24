namespace meli_znube_integration.Models.Canonical;

/// <summary>
/// Canonical (domain-stable) representation of a MercadoLibre item variation.
/// Anti-Corruption Layer model — independent of MELI API schema. Spec: canonical-item.
/// </summary>
public sealed record CanonicalVariant
{
    /// <summary>
    /// UserProductId of the variation in MELI.
    /// Falls back to the numeric variation Id (as string) when UserProductId is empty.
    /// </summary>
    public required string VariantId { get; init; }

    /// <summary>
    /// Seller SKU for this variation. Extracted from the SELLER_SKU attribute.
    /// Null when the variation carries no SELLER_SKU attribute.
    /// </summary>
    public string? SellerSku { get; init; }

    /// <summary>
    /// Available stock quantity. Default 0.
    /// Populated from the MELI item DTO; overwritten by StockSyncSourceService
    /// with the authoritative Znube quantity before stock calculators run.
    /// </summary>
    public int AvailableQuantity { get; init; } = 0;
}
