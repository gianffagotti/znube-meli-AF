using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Canonical;

namespace meli_znube_integration.Services;

/// <summary>
/// Anti-Corruption Layer implementation: converts a raw <see cref="MeliItem"/> DTO
/// into the domain-stable <see cref="CanonicalItem"/>.
///
/// Handles two MELI item schemas:
///   - Branch A (with variations): <c>MeliItem.Variations</c> is non-empty.
///     Each <see cref="MeliVariation"/> carries its own SKU (via the SELLER_SKU attribute
///     already computed in <see cref="MeliVariation.SellerSku"/>) and available quantity.
///   - Branch B (root-SKU / no variations): <c>MeliItem.Variations</c> is empty or null.
///     SKU and stock are copied to the root <see cref="CanonicalItem"/> and also to exactly
///     one synthetic <see cref="CanonicalVariant"/> (<c>VariantId = item.Id</c>).
///
/// This class performs no I/O. Design decisions: §D2 (service layer), §D4 (single normalizer).
/// Spec: meli-item-normalizer.
/// </summary>
public sealed class MeliItemNormalizer : IMeliItemNormalizer
{
    /// <inheritdoc />
    public CanonicalItem Normalize(MeliItem item)
    {
        // Task 3.5 — guard against null input
        ArgumentNullException.ThrowIfNull(item);

        // Task 3.4 — LogisticType is common to both branches
        var logisticType = item.Shipping?.LogisticType;

        // Task 3.2 — Branch A: item with variations (Legacy / Catalogue schema)
        if (item.Variations is { Count: > 0 })
        {
            var variants = item.Variations
                .Select(v => new CanonicalVariant
                {
                    // VariantId: prefer UserProductId; fall back to numeric Id as string
                    VariantId = !string.IsNullOrWhiteSpace(v.UserProductId)
                        ? v.UserProductId
                        : v.Id.ToString(),

                    // SellerSku: MeliVariation.SellerSku is already a computed property
                    // that reads the SELLER_SKU attribute (MeliConstants.SellerSkuAttributeId).
                    // Returns null when the attribute is absent.
                    SellerSku = v.SellerSku,

                    AvailableQuantity = v.AvailableQuantity
                })
                .ToList();

            return new CanonicalItem
            {
                Id = item.Id,
                Title = item.Title,
                SellerSku = null,         // SKU lives on individual variants in this schema
                AvailableQuantity = 0,
                LogisticType = logisticType,
                Variations = variants
            };
        }

        // Task 3.3 — Branch B: item without variations (root-SKU schema).
        // Always emit exactly one synthetic CanonicalVariant so calculators and
        // the frontend mapping table can iterate Variations uniformly.
        var sku = StockLocationHelpers.ResolveRootSellerSku(item);
        return new CanonicalItem
        {
            Id = item.Id,
            Title = item.Title,
            SellerSku = sku,
            AvailableQuantity = item.AvailableQuantity,
            LogisticType = logisticType,
            Variations =
            [
                new CanonicalVariant
                {
                    VariantId = item.Id,
                    SellerSku = sku,
                    AvailableQuantity = item.AvailableQuantity
                }
            ]
        };
    }
}
