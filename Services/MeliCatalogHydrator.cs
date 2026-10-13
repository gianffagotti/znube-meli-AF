using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

/// <summary>
/// Legacy items stay as the MLA. A User Products family becomes one container whose id is the family id
/// and whose variations are the sibling publications.
/// </summary>
public sealed class MeliCatalogHydrator : IMeliCatalogHydrator
{
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

        if (item.Variations is { Count: > 0 } && string.IsNullOrWhiteSpace(item.FamilyId))
            return item;

        try
        {
            var familyId = await ResolveFamilyIdAsync(item, ct);
            if (string.IsNullOrWhiteSpace(familyId))
                return EnsureLegacyVariation(item);

            if (string.Equals(item.Id, familyId, StringComparison.Ordinal) && item.Variations is { Count: > 0 })
                return item;

            if (!long.TryParse(sellerId, out var sellerIdLong))
            {
                _logger.LogWarning("Invalid sellerId '{SellerId}' for family hydration of item {ItemId}.", sellerId, item.Id);
                return EnsureLegacyVariation(item);
            }

            var siblings = await LoadFamilySiblingsAsync(item, familyId, sellerIdLong, ct);
            if (siblings.Count == 0)
                return EnsureLegacyVariation(item);

            ApplyFamilyContainer(item, familyId, siblings);
            return item;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hydrate user-product family for item {ItemId}. Falling back to a single variation.", item.Id);
            return EnsureLegacyVariation(item);
        }
    }

    /// <inheritdoc />
    public async Task<MeliItem?> FetchAndHydrateByIdAsync(string itemOrFamilyId, string sellerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemOrFamilyId))
            return null;

        var id = itemOrFamilyId.Trim();

        if (MeliItemIdClassifier.IsUserProductId(id))
        {
            if (!long.TryParse(sellerId, out var sellerLong))
                return null;

            var search = await _meliApiClient.SearchItemsAsync(
                sellerLong,
                new MeliItemSearchQuery
                {
                    UserProductId = id.ToUpperInvariant(),
                    Status = MeliConstants.FamilySiblingItemStatuses
                },
                ct);
            var mlaId = search?.Results?.FirstOrDefault()?.Id;
            if (string.IsNullOrWhiteSpace(mlaId))
                return null;

            var found = (await _meliApiClient.GetItemsAsync([mlaId], ct))?.FirstOrDefault();
            return found == null ? null : await HydrateAsync(found, sellerId, ct);
        }

        if (MeliItemIdClassifier.TryNormalizeItemId(id, out var normalizedMla))
        {
            var found = (await _meliApiClient.GetItemsAsync([normalizedMla], ct))?.FirstOrDefault();
            return found == null ? null : await HydrateAsync(found, sellerId, ct);
        }

        if (MeliItemIdClassifier.IsFamilyId(id))
            return await BuildContainerFromFamilyIdAsync(id, sellerId, ct);

        return null;
    }

    private async Task<MeliItem?> BuildContainerFromFamilyIdAsync(string familyId, string sellerId, CancellationToken ct)
    {
        if (!long.TryParse(sellerId, out var sellerLong))
            return null;

        var siblings = await LoadFamilySiblingsAsync(new MeliItem { FamilyId = familyId }, familyId, sellerLong, ct);
        if (siblings.Count == 0)
            return null;

        var seed = siblings[0];
        var shell = new MeliItem
        {
            Id = seed.Id,
            Title = seed.Title,
            FamilyId = familyId,
            FamilyName = siblings.Select(s => s.FamilyName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? seed.FamilyName,
            Thumbnail = seed.Thumbnail,
            Shipping = seed.Shipping,
            UserProductId = seed.UserProductId,
            SellerSku = seed.SellerSku,
            AvailableQuantity = seed.AvailableQuantity
        };
        ApplyFamilyContainer(shell, familyId, siblings);
        return shell;
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
                Status = MeliConstants.FamilySiblingItemStatuses
            },
            ct);

        var siblingIds = search?.Results?
            .Select(r => r.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (MeliItemIdClassifier.TryNormalizeItemId(item.Id ?? string.Empty, out _)
            && !string.IsNullOrWhiteSpace(item.Id)
            && !siblingIds.Any(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)))
        {
            siblingIds.Add(item.Id);
        }

        if (siblingIds.Count == 0)
            return [];

        return await _meliApiClient.GetItemsAsync(siblingIds, ct) ?? [];
    }

    private static void ApplyFamilyContainer(MeliItem item, string familyId, List<MeliItem> siblings)
    {
        item.FamilyId = familyId;
        item.Id = familyId;
        item.Title = !string.IsNullOrWhiteSpace(item.FamilyName) ? item.FamilyName : item.Title;
        item.Variations = siblings.Select(sibling => BuildSiblingVariation(sibling, item.FamilyName)).ToList();
    }

    private static MeliItem EnsureLegacyVariation(MeliItem item)
    {
        if (item.Variations is { Count: > 0 })
            return item;

        item.Variations = [BuildAlwaysOne(item)];
        return item;
    }

    private static MeliVariation BuildSiblingVariation(MeliItem sibling, string? familyName)
    {
        var attributes = CopyAttributes(sibling.Attributes);
        EnsureSellerSku(attributes, sibling.SellerSku, sibling.SellerCustomField);
        EnsureVariantTitle(attributes, sibling.Title, familyName);
        return new MeliVariation
        {
            Id = MeliItemIdClassifier.ParseMlaNumericId(sibling.Id),
            UserProductId = sibling.UserProductId ?? string.Empty,
            AvailableQuantity = sibling.AvailableQuantity,
            Attributes = attributes,
            LogisticType = sibling.Shipping?.LogisticType
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
            Attributes = attributes,
            LogisticType = item.Shipping?.LogisticType
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

    private static void EnsureVariantTitle(List<MeliAttribute> attributes, string? siblingTitle, string? familyName)
    {
        var hasDifferentiator = attributes.Any(a =>
            !string.IsNullOrWhiteSpace(a.ValueName)
            && !string.IsNullOrWhiteSpace(a.Id)
            && (a.Id.Contains("COLOR", StringComparison.OrdinalIgnoreCase)
                || a.Id.Contains("SIZE", StringComparison.OrdinalIgnoreCase)));
        if (hasDifferentiator) return;

        var title = siblingTitle?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;
        if (string.Equals(title, familyName?.Trim(), StringComparison.Ordinal)) return;

        attributes.Add(new MeliAttribute
        {
            Id = MeliConstants.VariantTitleAttributeId,
            ValueName = title
        });
    }
}
