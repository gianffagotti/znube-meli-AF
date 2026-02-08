using System.Net;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Functions;

public class StockRulesApi
{
    private readonly StockRuleService _service;
    private readonly ILogger<StockRulesApi> _logger;

    public StockRulesApi(StockRuleService service, ILogger<StockRulesApi> logger)
    {
        _service = service;
        _logger = logger;
    }

    [Function("GetStockRules")]
    public async Task<HttpResponseData> GetRules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rules")] HttpRequestData req)
    {
        try
        {
            var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
            var dtos = await _service.GetRulesBySellerAsync(sellerId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(dtos);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stock rules");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("Internal Server Error");
            return response;
        }
    }

    private static readonly HashSet<string> ValidRuleTypes = new(StringComparer.OrdinalIgnoreCase)
        { "FULL", "PACK", "COMBO" };

    [Function("UpsertStockRule")]
    public async Task<HttpResponseData> UpsertRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules")] HttpRequestData req)
    {
        try
        {
            var ruleDto = await req.ReadFromJsonAsync<StockRuleDto>();
            if (ruleDto == null || string.IsNullOrWhiteSpace(ruleDto.TargetItemId))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            var ruleType = (ruleDto.RuleType ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(ruleType) || !ValidRuleTypes.Contains(ruleType))
            {
                _logger.LogWarning("UpsertStockRule: ruleType inválido '{RuleType}'. Valores permitidos: FULL, PACK, COMBO.", ruleDto.RuleType);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new { message = "ruleType must be one of: FULL, PACK, COMBO." });
                return badResponse;
            }

            await _service.SaveRuleAsync(ruleDto);

            return req.CreateResponse(HttpStatusCode.OK);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting stock rule");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("Internal Server Error");
            return response;
        }
    }

    [Function("DeleteStockRule")]
    public async Task<HttpResponseData> DeleteRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "rules/{targetItemId}")] HttpRequestData req,
        string targetItemId)
    {
        try
        {
            var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
            await _service.DeleteRuleAsync(sellerId, targetItemId);
            return req.CreateResponse(HttpStatusCode.OK);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting stock rule");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("Internal Server Error");
            return response;
        }
    }
}