using System.Diagnostics;
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

    private static object ErrorBody(string message) => new { message, traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString() };

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
            var dtos = await _service.GetRulesBySellerAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(dtos);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stock rules");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(ErrorBody("Error getting stock rules"));
            return response;
        }
    }

    [Function("GetStockRule")]
    public async Task<HttpResponseData> GetRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rules/{targetItemId}")] HttpRequestData req,
        string targetItemId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetItemId))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
            var rule = await _service.GetRuleAsync(sellerId, targetItemId);

            if (rule == null)
            {
                return req.CreateResponse(HttpStatusCode.NotFound);
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(rule);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting stock rule for {TargetItemId}", targetItemId);
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(ErrorBody("Error getting stock rule"));
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

            if (ruleType.Equals("PACK", StringComparison.OrdinalIgnoreCase) || ruleType.Equals("COMBO", StringComparison.OrdinalIgnoreCase))
            {
                if (ruleDto.Components == null || ruleDto.Components.Count == 0)
                {
                    _logger.LogWarning("UpsertStockRule: PACK y COMBO requieren al menos un componente.");
                    var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badResponse.WriteAsJsonAsync(new { message = "PACK and COMBO rules require at least one component in 'components'." });
                    return badResponse;
                }
            }

            await _service.SaveRuleAsync(ruleDto);

            var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);
            var persisted = await _service.GetRuleAsync(sellerId, ruleDto.TargetItemId);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(persisted ?? ruleDto);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting stock rule");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(ErrorBody("Error upserting stock rule"));
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
            await response.WriteAsJsonAsync(ErrorBody("Error deleting stock rule"));
            return response;
        }
    }
}