using System.Net;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Functions;

public class MeliProxyApi
{
    private readonly MeliClient _meliClient;
    private readonly ILogger<MeliProxyApi> _logger;

    public MeliProxyApi(MeliClient meliClient, ILogger<MeliProxyApi> logger)
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

            var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
            
            // 1. Search for IDs
            var ids = await _meliClient.SearchItemsGeneralAsync(sellerId, query);
            
            if (ids.Count == 0)
            {
                var empty = req.CreateResponse(HttpStatusCode.OK);
                await empty.WriteAsJsonAsync(new List<MeliItem>());
                return empty;
            }

            // 2. Get Details
            var items = await _meliClient.GetItemsAsync(ids);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(items);
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
            await response.WriteAsJsonAsync(item);
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
}
