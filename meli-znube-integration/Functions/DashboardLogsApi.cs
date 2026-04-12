using System.Diagnostics;
using System.Net;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Functions;

public class DashboardLogsApi
{
    private readonly IDashboardLogService _dashboardLogService;
    private readonly ILogger<DashboardLogsApi> _logger;

    private static object ErrorBody(string message) => new { message, traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString() };

    public DashboardLogsApi(IDashboardLogService dashboardLogService, ILogger<DashboardLogsApi> logger)
    {
        _dashboardLogService = dashboardLogService;
        _logger = logger;
    }

    [Function("GetDashboardLogs")]
    public async Task<HttpResponseData> GetLogs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/logs")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var date = query["date"];
            if (string.IsNullOrWhiteSpace(date))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { message = "Query parameter 'date' (yyyy-MM-dd) is required." });
                return bad;
            }
            var severity = query["severity"];
            var category = query["category"];

            var logs = await _dashboardLogService.GetLogsAsync(date!, severity, category);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(logs);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard logs");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(ErrorBody("Error getting dashboard logs"));
            return response;
        }
    }

    [Function("MarkDashboardLogRead")]
    public async Task<HttpResponseData> MarkAsRead(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "dashboard/logs/{partitionKey}/{rowKey}/read")] HttpRequestData req,
        string partitionKey,
        string rowKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(partitionKey) || string.IsNullOrWhiteSpace(rowKey))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }
            await _dashboardLogService.MarkAsReadAsync(partitionKey, rowKey);
            return req.CreateResponse(HttpStatusCode.OK);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking dashboard log as read");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(ErrorBody("Error marking log as read"));
            return response;
        }
    }
}
