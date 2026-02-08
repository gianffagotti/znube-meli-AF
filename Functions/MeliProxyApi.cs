using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Functions;

public class MeliProxyApi
{
    private readonly IMeliApiClient _meliClient;
    private readonly ILogger<MeliProxyApi> _logger;

    public MeliProxyApi(IMeliApiClient meliClient, ILogger<MeliProxyApi> logger)
    {
        _meliClient = meliClient;
        _logger = logger;
    }

    [Function("MeliProxySearch")]
    public async Task<HttpResponseData> Search(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "meli-proxy/search")] HttpRequestData req)
    {
        try
        {
            var queryDictionary = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var query = queryDictionary["q"];

            if (string.IsNullOrWhiteSpace(query))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            var sellerIdStr = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
            var sellerId = long.Parse(sellerIdStr);
            var cleanQuery = query.Trim();

            // Service-layer logic: if MLA item ID, return it
            if (Regex.IsMatch(cleanQuery, @"^MLA\d+$", RegexOptions.IgnoreCase))
            {
                var single = await _meliClient.GetItemsAsync([cleanQuery.ToUpperInvariant()]);
                var dto = single?.FirstOrDefault();
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(dto != null ? new List<MeliProxyItemDto> { MapToDto(dto) } : new List<MeliProxyItemDto>());
                return response;
            }

            var qSearch = await _meliClient.SearchItemsAsync(sellerId, new MeliItemSearchQuery { Query = cleanQuery });
            var skuSearch = await _meliClient.SearchItemsAsync(sellerId, new MeliItemSearchQuery { SellerSku = cleanQuery });
            var ids = (qSearch?.Results?.Select(r => r.Id).Where(id => !string.IsNullOrWhiteSpace(id)) ?? Array.Empty<string?>())
                .Concat(skuSearch?.Results?.Select(r => r.Id).Where(id => !string.IsNullOrWhiteSpace(id)) ?? Array.Empty<string?>())
                .Distinct()
                .Cast<string>()
                .ToList();

            if (ids.Count == 0)
            {
                var empty = req.CreateResponse(HttpStatusCode.OK);
                await empty.WriteAsJsonAsync(new List<MeliItem>());
                return empty;
            }

            var items = await _meliClient.GetItemsAsync(ids);
            var dtos = items.Select(MapToDto).ToList();

            var response2 = req.CreateResponse(HttpStatusCode.OK);
            await response2.WriteAsJsonAsync(dtos);
            return response2;
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
        string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            var items = await _meliClient.GetItemsAsync([id]);
            var item = items.FirstOrDefault();

            if (item == null)
            {
                return req.CreateResponse(HttpStatusCode.NotFound);
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(MapToDto(item));
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

    private static MeliProxyItemDto MapToDto(MeliItem item)
    {
        return new MeliProxyItemDto
        {
            Id = item.Id,
            Title = item.Title,
            Thumbnail = item.Thumbnail,
            Variations = [.. item.Variations.Select(v => new MeliProxyVariationDto
            {
                Id = v.Id,
                UserProductId = v.UserProductId,
                Sku = v.Attributes.FirstOrDefault(a => a.Id == "SELLER_SKU")?.ValueName,
                Description = BuildDescription(v.Attributes)
            }).OrderBy(v => v.Sku)]
        };
    }

    private static string BuildDescription(List<MeliAttribute> attributes)
    {
        var relevantAttributes = attributes
            .Where(a => !string.IsNullOrEmpty(a.ValueName) && 
                        (a.Id.Contains("_COLOR", StringComparison.OrdinalIgnoreCase) || 
                         a.Id.Contains("_SIZE", StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.ValueName);
            
        return string.Join(" - ", relevantAttributes);
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
