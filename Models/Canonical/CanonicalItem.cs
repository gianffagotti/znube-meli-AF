namespace meli_znube_integration.Models.Canonical;

/// <summary>
/// Canonical (domain-stable) representation of a MercadoLibre item publication.
/// Anti-Corruption Layer model — decouples domain logic from the raw MELI API DTO structure
/// (Legacy / root-SKU schemas). Spec: canonical-item.
/// </summary>
public sealed record CanonicalItem
{
    /// <summary>MELI item ID (e.g. "MLA123456789").</summary>
    public required string Id { get; init; }

    /// <summary>Publication title.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Root-level seller SKU. Populated only for items without variations (root-SKU schema).
    /// Null when individual variations carry their own SKUs.
    /// </summary>
    public string? SellerSku { get; init; }

    /// <summary>
    /// Root-level available stock quantity. Populated for items without variations; 0 otherwise.
    /// Overwritten by StockSyncSourceService with Znube stock before calculators run.
    /// </summary>
    public int AvailableQuantity { get; init; } = 0;

    /// <summary>
    /// Logistic type as returned by MELI (e.g. "fulfillment", "flex", "self_service", "not_specified").
    /// Null when the MELI response omits the shipping block.
    /// </summary>
    public string? LogisticType { get; init; }

    /// <summary>
    /// Item variations. Empty list for items without variations (root-SKU schema).
    /// Never null.
    /// </summary>
    public IReadOnlyList<CanonicalVariant> Variations { get; init; } = [];
}
