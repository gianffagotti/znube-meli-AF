using System.Net;
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
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "rules")] HttpRequestData req)
    {
        try
        {
            var rules = await _service.GetAllRulesAsync();

            var groups = rules
                .GroupBy(r => r.MotherItemId)
                .Select(g =>
                {
                    var first = g.First();
                    return new StockRuleGroupDto
                    {
                        MotherItemId = g.Key,
                        MotherSku = first.MotherSku,
                        MotherTitle = first.MotherTitle,
                        MotherThumbnail = first.MotherThumbnail,
                        Rules = g.Select(r => new StockRuleItemDto
                        {
                            MotherUserProductId = r.PartitionKey,
                            ChildUserProductId = r.RowKey,
                            Type = r.Type,
                            PackQuantity = r.PackQuantity,
                            ChildItemId = r.ChildItemId,
                            ChildSku = r.ChildSku,
                            ChildTitle = r.ChildTitle
                        }).ToList()
                    };
                })
                .ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(groups);
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

    [Function("UpsertStockRules")]
    public async Task<HttpResponseData> UpsertRules(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "rules")] HttpRequestData req)
    {
        try
        {
            var groupDto = await req.ReadFromJsonAsync<StockRuleGroupDto>();
            if (groupDto == null || groupDto.Rules == null)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            foreach (var ruleDto in groupDto.Rules)
            {
                var entity = new StockRuleEntity
                {
                    PartitionKey = ruleDto.MotherUserProductId,
                    RowKey = ruleDto.ChildUserProductId,
                    Type = ruleDto.Type,
                    PackQuantity = ruleDto.PackQuantity,
                    MotherItemId = groupDto.MotherItemId,
                    MotherSku = groupDto.MotherSku,
                    MotherTitle = groupDto.MotherTitle,
                    MotherThumbnail = groupDto.MotherThumbnail,
                    ChildItemId = ruleDto.ChildItemId,
                    ChildSku = ruleDto.ChildSku,
                    ChildTitle = ruleDto.ChildTitle
                };

                await _service.UpsertRuleAsync(entity);
            }

            return req.CreateResponse(HttpStatusCode.OK);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting stock rules");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("Internal Server Error");
            return response;
        }
    }

    [Function("DeleteStockRule")]
    public async Task<HttpResponseData> DeleteRule(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "rules/{motherUpid}/{childUpid}")] HttpRequestData req,
        string motherUpid,
        string childUpid)
    {
        try
        {
            await _service.DeleteRuleAsync(motherUpid, childUpid);
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
