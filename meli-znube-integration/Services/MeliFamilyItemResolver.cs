using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

/// <summary>
/// Normalizes a <see cref="MeliItem"/> for the UI so the classic model (nested variations) and the new
/// User Products Families model coexist transparently. Family items (no variations + family_id) are expanded:
/// each sibling MLAU becomes a variation carrying its MLAU as user_product_id plus its resolved SKU/attributes.
/// </summary>
public interface IMeliUiItemNormalizer
{
    Task<MeliItem> NormalizeAsync(MeliItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a family id directly into a synthesised <see cref="MeliItem"/> shell whose variations are all
    /// active FULL+FLEX siblings. Returns null when no eligible siblings exist or on unrecoverable failure.
    /// </summary>
    Task<MeliItem?> SearchByFamilyIdAsync(string familyId, CancellationToken cancellationToken = default);
}

public class MeliFamilyItemResolver : IMeliUiItemNormalizer
{
    private readonly IMeliApiClient _meli;
    private readonly ILogger<MeliFamilyItemResolver> _logger;

    public MeliFamilyItemResolver(IMeliApiClient meli, ILogger<MeliFamilyItemResolver> logger)
    {
        _meli = meli ?? throw new ArgumentNullException(nameof(meli));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Minimum number of consecutive digits for a search input to be treated as a family id.</summary>
    public const int FamilyIdMinDigits = 10;

    public async Task<MeliItem?> SearchByFamilyIdAsync(string familyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(familyId)) return null;
        try
        {
            var family = await _meli.GetUserProductsFamilyAsync(familyId.Trim(), cancellationToken);
            var mlauIds = family?.UserProductsIds?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (mlauIds.Count == 0) return null;

            var sellerId = long.Parse(EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId));
            var eligibleSiblings = new List<(string Mlau, MeliItem Item)>();

            foreach (var mlau in mlauIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var siblingItem = await ResolveSiblingItemAsync(sellerId, mlau, cancellationToken);
                if (siblingItem is null || !siblingItem.IsEligibleForUi()) continue;
                eligibleSiblings.Add((mlau, siblingItem));
            }

            if (eligibleSiblings.Count == 0) return null;

            // Base shell: first eligible sibling provides id/title/thumbnail/shipping.
            // FamilyName (when present on any sibling) is used as the representative title.
            var (_, baseItem) = eligibleSiblings[0];
            var familyName = eligibleSiblings.Select(s => s.Item.FamilyName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            var shell = new MeliItem
            {
                Id = baseItem.Id,
                Title = familyName ?? baseItem.Title,
                Thumbnail = baseItem.Thumbnail,
                Shipping = baseItem.Shipping,
                Status = baseItem.Status,
                FamilyId = familyId,
                FamilyName = familyName,
                Attributes = baseItem.Attributes,
            };

            // Variations: one per eligible sibling (including the base sibling itself).
            shell.Variations = eligibleSiblings.Select(s =>
            {
                var sku = ExtractSku(s.Item, s.Mlau);
                return new MeliVariation
                {
                    Id = 0,
                    UserProductId = s.Mlau,
                    Attributes = BuildAttributes(s.Item, s.Mlau, sku),
                };
            }).ToList();

            return shell;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SearchByFamilyIdAsync failed for family {FamilyId}. Returning null.", familyId);
            return null;
        }
    }

    public async Task<MeliItem> NormalizeAsync(MeliItem item, CancellationToken cancellationToken = default)
    {
        if (item == null) return item!;

        // Classic model: already has variations → return as-is, no family call.
        if (item.Variations is { Count: > 0 }) return item;

        var familyId = ResolveFamilyId(item);
        if (string.IsNullOrWhiteSpace(familyId)) return item;

        try
        {
            item.Variations = await ExpandFamilyAsync(familyId!, cancellationToken);
        }
        catch (Exception ex)
        {
            // Graceful degradation: never fail the request because of family expansion.
            _logger.LogWarning(ex, "Family expansion failed for item {ItemId} (family {FamilyId}). Returning item without expanded variations.", item.Id, familyId);
        }

        // If the family has a name, prefer it as the display title.
        if (!string.IsNullOrWhiteSpace(item.FamilyName))
            item.Title = item.FamilyName;

        return item;
    }

    /// <summary>family_id from root, fallback to an attribute whose id mentions FAMILY.</summary>
    private static string? ResolveFamilyId(MeliItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.FamilyId)) return item.FamilyId;
        var attr = item.Attributes?.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.ValueName) &&
            a.Id.Contains("FAMILY", StringComparison.OrdinalIgnoreCase));
        return attr?.ValueName;
    }

    private async Task<List<MeliVariation>> ExpandFamilyAsync(string familyId, CancellationToken ct)
    {
        var family = await _meli.GetUserProductsFamilyAsync(familyId, ct);
        var mlauIds = family?.UserProductsIds?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        if (mlauIds.Count == 0) return new List<MeliVariation>();

        // SKU fallback source (batch): MLAU → user product detail. Best-effort.
        var upBySku = new Dictionary<string, MeliUserProductDto>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var up in await _meli.ResolveUserProductsAsync(mlauIds, ct))
            {
                if (!string.IsNullOrWhiteSpace(up.Id)) upBySku[up.Id!] = up;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ResolveUserProductsAsync failed for family {FamilyId}; continuing with item-search resolution.", familyId);
        }

        var sellerId = long.Parse(EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId));
        var variations = new List<MeliVariation>();
        foreach (var mlau in mlauIds)
        {
            ct.ThrowIfCancellationRequested();

            var siblingItem = await ResolveSiblingItemAsync(sellerId, mlau, ct);

            // Unbreakable filter: drop siblings that are not active FULL+FLEX.
            if (siblingItem is null || !siblingItem.IsEligibleForUi())
                continue;

            var sku = ExtractSku(siblingItem, mlau)
                      ?? (upBySku.TryGetValue(mlau, out var up) ? ExtractSku(up) : null);

            variations.Add(new MeliVariation
            {
                Id = 0,
                UserProductId = mlau,
                Attributes = BuildAttributes(siblingItem, mlau, sku),
            });
        }
        return variations;
    }

    private async Task<MeliItem?> ResolveSiblingItemAsync(long sellerId, string mlau, CancellationToken ct)
    {
        try
        {
            var search = await _meli.SearchItemsAsync(sellerId, new MeliItemSearchQuery { UserProductId = mlau }, ct);
            var siblingItemId = search?.Results?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Id))?.Id;
            if (string.IsNullOrWhiteSpace(siblingItemId)) return null;
            var items = await _meli.GetItemsAsync(new[] { siblingItemId! }, ct);
            return items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve sibling item for MLAU {Mlau}.", mlau);
            return null;
        }
    }

    /// <summary>SKU from the variation matching the MLAU, else item-level SKU sources.</summary>
    private static string? ExtractSku(MeliItem? item, string mlau)
    {
        if (item == null) return null;
        var variation = item.Variations?.FirstOrDefault(v => string.Equals(v.UserProductId, mlau, StringComparison.OrdinalIgnoreCase));
        if (variation != null)
        {
            var fromAttr = variation.Attributes?.FirstOrDefault(a => string.Equals(a.Id, MeliConstants.SellerSkuAttributeId, StringComparison.OrdinalIgnoreCase))?.ValueName;
            if (!string.IsNullOrWhiteSpace(fromAttr)) return fromAttr!.Trim();
            if (!string.IsNullOrWhiteSpace(variation.SellerSku)) return variation.SellerSku!.Trim();
            if (!string.IsNullOrWhiteSpace(variation.SellerCustomField)) return variation.SellerCustomField!.Trim();
        }
        var itemAttr = item.Attributes?.FirstOrDefault(a => string.Equals(a.Id, MeliConstants.SellerSkuAttributeId, StringComparison.OrdinalIgnoreCase))?.ValueName;
        if (!string.IsNullOrWhiteSpace(itemAttr)) return itemAttr!.Trim();
        if (!string.IsNullOrWhiteSpace(item.SellerSku)) return item.SellerSku!.Trim();
        if (!string.IsNullOrWhiteSpace(item.SellerCustomField)) return item.SellerCustomField!.Trim();
        return null;
    }

    private static string? ExtractSku(MeliUserProductDto up)
    {
        var fromAttr = up.Attributes?.FirstOrDefault(a => string.Equals(a.Id, MeliConstants.SellerSkuAttributeId, StringComparison.OrdinalIgnoreCase))?.ValueName;
        if (!string.IsNullOrWhiteSpace(fromAttr)) return fromAttr!.Trim();
        return string.IsNullOrWhiteSpace(up.SellerSku) ? null : up.SellerSku!.Trim();
    }

    /// <summary>
    /// Builds the variation attributes so the existing proxy mapping reads SKU (SELLER_SKU) and description
    /// (COLOR/SIZE) without changes. Copies the resolved sibling's attributes and ensures SELLER_SKU is present.
    /// </summary>
    private static List<MeliAttribute> BuildAttributes(MeliItem? siblingItem, string mlau, string? sku)
    {
        var attrs = new List<MeliAttribute>();
        var variation = siblingItem?.Variations?.FirstOrDefault(v => string.Equals(v.UserProductId, mlau, StringComparison.OrdinalIgnoreCase));
        if (variation != null)
        {
            if (variation.Attributes != null) attrs.AddRange(variation.Attributes);
            if (variation.AttributesCombinations != null) attrs.AddRange(variation.AttributesCombinations);
        }
        else if (siblingItem?.Attributes != null)
        {
            attrs.AddRange(siblingItem.Attributes);
        }

        var hasSku = attrs.Any(a => string.Equals(a.Id, MeliConstants.SellerSkuAttributeId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.ValueName));
        if (!hasSku && !string.IsNullOrWhiteSpace(sku))
        {
            attrs.Add(new MeliAttribute { Id = MeliConstants.SellerSkuAttributeId, ValueName = sku });
        }
        return attrs;
    }
}
