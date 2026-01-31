using System.Net;
using System.Text.Json;
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
            var rules = await _service.GetRulesBySellerAsync(sellerId);

            var dtos = rules.Select(r => new StockRuleDto
            {
                TargetItemId = r.RowKey,
                TargetTitle = r.TargetTitle,
                TargetThumbnail = r.TargetThumbnail,
                RuleType = r.RuleType,
                Components = !string.IsNullOrEmpty(r.ComponentsJson)
                    ? JsonSerializer.Deserialize<List<RuleComponentDto>>(r.ComponentsJson) ?? []
                    : [],
                Mappings = !string.IsNullOrEmpty(r.MappingJson)
                    ? JsonSerializer.Deserialize<List<VariantMappingDto>>(r.MappingJson) ?? []
                    : []
            }).ToList();

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

            var sellerId = EnvVars.GetRequiredString(EnvVars.Keys.MeliSellerId);

            var entity = new StockRuleEntity
            {
                PartitionKey = sellerId,
                RowKey = ruleDto.TargetItemId,
                RuleType = ruleDto.RuleType,
                ComponentsJson = JsonSerializer.Serialize(ruleDto.Components),
                MappingJson = JsonSerializer.Serialize(ruleDto.Mappings),
                TargetItemId = ruleDto.TargetItemId,
                TargetTitle = ruleDto.TargetTitle,
                TargetThumbnail = ruleDto.TargetThumbnail
            };

            await _service.SaveRuleAsync(entity);

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