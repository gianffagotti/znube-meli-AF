using System.Text.RegularExpressions;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

/// <summary>
/// Hydrates an item into variations the editor can map.
/// Legacy items (variations already present) are returned unchanged.
/// User Products with an empty variations array are expanded through the family
/// (<c>GET /sites/MLA/user-products-families/{family_id}</c>). A publication with no
/// family becomes a single synthetic variation.
/// </summary>
public sealed class MeliCatalogHydrator : IMeliCatalogHydrator
{
    private static readonly Regex MlaIdRegex = new(@"^MLA(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IMeliApiClient _meliApiClient;
    private readonly ILogger<MeliCatalogHydrator> _logger;

    public MeliCatalogHydrator(IMeliApiClient meliApiClient, ILogger<MeliCatalogHydrator> logger)
    {
        _meliApiClient = meliApiClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MeliItem> HydrateAsync(MeliItem item, string sellerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Variations is { Count: > 0 })
            return item;

        try
        {
            var familyId = await ResolveFamilyIdAsync(item, ct);
            if (!string.IsNullOrWhiteSpace(familyId))
            {
                if (!long.TryParse(sellerId, out var sellerIdLong))
                {
                    _logger.LogWarning("Invalid sellerId '{SellerId}' for family hydration of item {ItemId}.", sellerId, item.Id);
                }
                else
                {
                    var siblings = await LoadFamilySiblingsAsync(item, familyId, sellerIdLong, ct);
                    if (siblings.Count > 0)
                    {
                        item.Variations = siblings.Select(BuildSiblingVariation).ToList();
                        return item;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hydrate user-product family for item {ItemId}. Falling back to a single variation.", item.Id);
        }

        item.Variations = [BuildAlwaysOne(item)];
        return item;
    }

    private async Task<string?> ResolveFamilyIdAsync(MeliItem item, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(item.FamilyId))
            return item.FamilyId;

        if (string.IsNullOrWhiteSpace(item.UserProductId))
            return null;

        var userProduct = await _meliApiClient.GetUserProductAsync(item.UserProductId, ct);
        if (string.IsNullOrWhiteSpace(userProduct?.FamilyId))
            return null;

        item.FamilyId = userProduct.FamilyId;
        return item.FamilyId;
    }

    private async Task<List<MeliItem>> LoadFamilySiblingsAsync(MeliItem item, string familyId, long sellerId, CancellationToken ct)
    {
        var userProductIds = await _meliApiClient.GetUserProductFamilyAsync(familyId, ct);
        if (userProductIds.Count == 0)
            return [];

        var search = await _meliApiClient.SearchItemsAsync(
            sellerId,
            new MeliItemSearchQuery
            {
                UserProductId = string.Join(",", userProductIds),
                Status = "active"
            },
            ct);

        var siblingIds = search?.Results?
            .Select(r => r.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (!string.IsNullOrWhiteSpace(item.Id)
            && !siblingIds.Any(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)))
        {
            siblingIds.Add(item.Id);
        }

        if (siblingIds.Count == 0)
            return [];

        return await _meliApiClient.GetItemsAsync(siblingIds, ct) ?? [];
    }

    private static MeliVariation BuildSiblingVariation(MeliItem sibling)
    {
        var attributes = CopyAttributes(sibling.Attributes);
        EnsureSellerSku(attributes, sibling.SellerSku, sibling.SellerCustomField);
        return new MeliVariation
        {
            Id = ParseMlaNumericId(sibling.Id),
            UserProductId = sibling.UserProductId ?? string.Empty,
            AvailableQuantity = sibling.AvailableQuantity,
            Attributes = attributes
        };
    }

    private static MeliVariation BuildAlwaysOne(MeliItem item)
    {
        var attributes = CopyAttributes(item.Attributes);
        EnsureSellerSku(attributes, item.SellerSku, item.SellerCustomField);
        return new MeliVariation
        {
            Id = 0,
            UserProductId = string.IsNullOrWhiteSpace(item.UserProductId) ? item.Id : item.UserProductId,
            AvailableQuantity = item.AvailableQuantity,
            Attributes = attributes
        };
    }

    private static List<MeliAttribute> CopyAttributes(List<MeliAttribute>? source)
    {
        if (source == null || source.Count == 0) return [];
        return source.Select(a => new MeliAttribute { Id = a.Id, ValueName = a.ValueName }).ToList();
    }

    private static void EnsureSellerSku(List<MeliAttribute> attributes, string? sellerSku, string? sellerCustomField)
    {
        var hasSku = attributes.Any(a =>
            string.Equals(a.Id, MeliConstants.SellerSkuAttributeId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(a.ValueName));
        if (hasSku) return;

        var sku = !string.IsNullOrWhiteSpace(sellerSku) ? sellerSku
            : !string.IsNullOrWhiteSpace(sellerCustomField) ? sellerCustomField
            : null;
        if (sku == null) return;

        attributes.Add(new MeliAttribute
        {
            Id = MeliConstants.SellerSkuAttributeId,
            ValueName = sku
        });
    }

    private static long ParseMlaNumericId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return 0;
        var match = MlaIdRegex.Match(itemId.Trim());
        if (!match.Success) return 0;
        return long.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }
}
