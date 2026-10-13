using System.Net;
using System.Text.Json.Serialization;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Functions;

public class MeliProxyApi
{
    private readonly IMeliApiClient _meliClient;
    private readonly IMeliCatalogHydrator _hydrator;
    private readonly ILogger<MeliProxyApi> _logger;

    public MeliProxyApi(IMeliApiClient meliClient, IMeliCatalogHydrator hydrator, ILogger<MeliProxyApi> logger)
    {
        _meliClient = meliClient;
        _hydrator = hydrator;
        _logger = logger;
    }

    [Function("MeliProxySearch")]
    public async Task<HttpResponseData> Search(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "meli-proxy/search")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var queryDictionary = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var query = queryDictionary["q"];

            if (string.IsNullOrWhiteSpace(query))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            var dtos = await SearchCoreAsync(query, ct);
            if (dtos == null)
                return req.CreateResponse(HttpStatusCode.BadRequest);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(dtos);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in MeliProxySearch");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("Internal Server Error");
            return response;
        }
    }

    [Function("MeliProxyGetItem")]
    public async Task<HttpResponseData> GetItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "meli-proxy/items/{id}")] HttpRequestData req,
        string id,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            var dto = await GetItemCoreAsync(id, ct);
            if (dto == null)
                return req.CreateResponse(HttpStatusCode.NotFound);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(dto);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in MeliProxyGetItem");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("Internal Server Error");
            return response;
        }
    }

    internal async Task<MeliProxyItemDto?> GetItemCoreAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
        var item = await _hydrator.FetchAndHydrateByIdAsync(id, sellerId, ct);
        if (item == null) return null;
        return MapToDto(EnsureAtLeastOneVariation(item));
    }

    internal async Task<List<MeliProxyItemDto>?> SearchCoreAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var cleanQuery = query.Trim();
        if (cleanQuery.StartsWith('#'))
            cleanQuery = cleanQuery[1..].Trim();
        if (string.IsNullOrWhiteSpace(cleanQuery)) return null;

        var sellerId = long.Parse(EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId));
        var sellerIdStr = sellerId.ToString();
        List<MeliItem>? candidates = null;

        if (cleanQuery.StartsWith("MLAU", StringComparison.OrdinalIgnoreCase))
        {
            candidates = await LoadByUserProductIdsAsync(sellerId, cleanQuery.ToUpperInvariant(), ct);
        }
        else if (MeliItemIdClassifier.TryNormalizeItemId(cleanQuery, out var mlaId))
        {
            var single = await _meliClient.GetItemsAsync([mlaId], ct);
            if (single is { Count: > 0 })
                candidates = single;
        }
        else if (MeliItemIdClassifier.IsFamilyId(cleanQuery))
        {
            var userProductIds = await _meliClient.GetUserProductFamilyAsync(cleanQuery, ct);
            if (userProductIds.Count == 0)
                return [];

            candidates = await LoadByUserProductIdsAsync(sellerId, string.Join(",", userProductIds), ct);
        }

        if (candidates == null)
        {
            var skuSearch = _meliClient.SearchItemsAsync(sellerId, new MeliItemSearchQuery { SellerSku = cleanQuery, Status = "active" }, ct);
            var textSearch = _meliClient.SearchItemsAsync(sellerId, new MeliItemSearchQuery { Query = cleanQuery, Status = "active" }, ct);
            await Task.WhenAll(skuSearch, textSearch);

            var ids = CollectIds(skuSearch.Result)
                .Concat(CollectIds(textSearch.Result))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0)
                return [];

            candidates = await _meliClient.GetItemsAsync(ids, ct);
        }

        var hydrated = await GroupAndHydrateAsync(candidates, sellerIdStr, ct);
        return hydrated.Select(MapToDto).ToList();
    }

    private async Task<List<MeliItem>> GroupAndHydrateAsync(List<MeliItem> items, string sellerId, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.FamilyId) || string.IsNullOrWhiteSpace(item.UserProductId))
                continue;

            var userProduct = await _meliClient.GetUserProductAsync(item.UserProductId, ct);
            if (!string.IsNullOrWhiteSpace(userProduct?.FamilyId))
                item.FamilyId = userProduct.FamilyId;
        }

        var result = new List<MeliItem>();
        foreach (var item in items.Where(i => string.IsNullOrWhiteSpace(i.FamilyId)))
        {
            result.Add(EnsureAtLeastOneVariation(await _hydrator.HydrateAsync(item, sellerId, ct)));
        }

        foreach (var group in items.Where(i => !string.IsNullOrWhiteSpace(i.FamilyId)).GroupBy(i => i.FamilyId))
        {
            var hydrated = await _hydrator.HydrateAsync(group.First(), sellerId, ct);
            result.Add(EnsureAtLeastOneVariation(hydrated));
        }

        return result;
    }

    private async Task<List<MeliItem>> LoadByUserProductIdsAsync(long sellerId, string userProductIds, CancellationToken ct)
    {
        var search = await _meliClient.SearchItemsAsync(
            sellerId,
            new MeliItemSearchQuery { UserProductId = userProductIds, Status = "active" },
            ct);
        var ids = CollectIds(search);
        if (ids.Count == 0) return [];
        return await _meliClient.GetItemsAsync(ids, ct);
    }

    private static List<string> CollectIds(MeliSearchResponseDto? search)
    {
        return search?.Results?
            .Select(r => r.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    private static MeliItem EnsureAtLeastOneVariation(MeliItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.FamilyId) || item.Variations is { Count: > 0 })
            return item;

        item.Variations =
        [
            new MeliVariation
            {
                Id = 0,
                UserProductId = string.IsNullOrWhiteSpace(item.UserProductId) ? item.Id : item.UserProductId,
                AvailableQuantity = item.AvailableQuantity,
                Attributes =
                [
                    new MeliAttribute
                    {
                        Id = MeliConstants.SellerSkuAttributeId,
                        ValueName = StockLocationHelpers.ResolveRootSellerSku(item)
                    }
                ]
            }
        ];
        return item;
    }

    private static MeliProxyItemDto MapToDto(MeliItem item)
    {
        return new MeliProxyItemDto
        {
            Id = item.Id,
            Title = item.Title,
            Thumbnail = item.Thumbnail,
            FamilyId = string.IsNullOrWhiteSpace(item.FamilyId) ? null : item.FamilyId,
            FamilyName = item.FamilyName,
            UserProductId = string.IsNullOrWhiteSpace(item.UserProductId) ? null : item.UserProductId,
            SellerSku = ResolveListedSku(item),
            Shipping = item.Shipping,
            Variations = [.. item.Variations.Select(v => new MeliProxyVariationDto
            {
                Id = v.Id,
                UserProductId = ResolveVariationUserProductId(v),
                Sku = v.Attributes.FirstOrDefault(a => a.Id == MeliConstants.SellerSkuAttributeId)?.ValueName,
                Description = BuildDescription([..v.Attributes, ..v.AttributesCombinations])
            }).OrderBy(v => v.Sku)]
        };
    }

    private static string? ResolveListedSku(MeliItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.SellerSku))
            return item.SellerSku;

        var fromAttribute = item.Attributes?
            .FirstOrDefault(a => string.Equals(a.Id, MeliConstants.SellerSkuAttributeId, StringComparison.OrdinalIgnoreCase))
            ?.ValueName;
        if (!string.IsNullOrWhiteSpace(fromAttribute))
            return fromAttribute;

        return string.IsNullOrWhiteSpace(item.SellerCustomField) ? null : item.SellerCustomField;
    }

    private static string ResolveVariationUserProductId(MeliVariation variation)
    {
        if (!string.IsNullOrWhiteSpace(variation.UserProductId))
            return variation.UserProductId;
        return variation.Id > 0 ? variation.Id.ToString() : string.Empty;
    }

    private static string BuildDescription(List<MeliAttribute> attributes)
    {
        var relevantAttributes = attributes
            .Where(a => !string.IsNullOrEmpty(a.ValueName) && !string.IsNullOrEmpty(a.Id) &&
                        (a.Id.Contains("COLOR", StringComparison.OrdinalIgnoreCase) ||
                         a.Id.Contains("SIZE", StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.ValueName)
            .ToList();

        if (relevantAttributes.Count > 0)
            return string.Join(" - ", relevantAttributes);

        return attributes
            .FirstOrDefault(a => string.Equals(a.Id, MeliConstants.VariantTitleAttributeId, StringComparison.OrdinalIgnoreCase))
            ?.ValueName ?? string.Empty;
    }
}

public class MeliProxyItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("variations")]
    public List<MeliProxyVariationDto> Variations { get; set; } = new();

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("family_id")]
    public string? FamilyId { get; set; }

    [JsonPropertyName("family_name")]
    public string? FamilyName { get; set; }

    [JsonPropertyName("user_product_id")]
    public string? UserProductId { get; set; }

    [JsonPropertyName("seller_sku")]
    public string? SellerSku { get; set; }

    [JsonPropertyName("shipping")]
    public MeliShipping? Shipping { get; set; }
}

public class MeliProxyVariationDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("user_product_id")]
    public string UserProductId { get; set; } = string.Empty;

    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
