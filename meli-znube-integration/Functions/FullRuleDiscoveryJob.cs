using meli_znube_integration.Common;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace meli_znube_integration.Functions;

public class FullRuleDiscoveryJob
{
    private readonly FullRuleDiscoveryService _discoveryService;
    private readonly FullRuleDiscoveryStateService _state;
    private readonly FullRuleDiscoveryQueueService _queue;
    private readonly IDashboardLogService _dashboardLogService;
    private readonly ILogger<FullRuleDiscoveryJob> _logger;

    public FullRuleDiscoveryJob(
        FullRuleDiscoveryService discoveryService,
        FullRuleDiscoveryStateService state,
        FullRuleDiscoveryQueueService queue,
        IDashboardLogService dashboardLogService,
        ILogger<FullRuleDiscoveryJob> logger)
    {
        _discoveryService = discoveryService;
        _state = state;
        _queue = queue;
        _dashboardLogService = dashboardLogService;
        _logger = logger;
    }

    [Function("FullRuleDiscoveryJob")]
    public async Task Run([TimerTrigger("0 0 4 * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Full Rule Discovery job started at {Time}.", DateTime.UtcNow);

        if (!EnvVars.GetBool(EnvVars.Keys.EnableJobFullDiscovery, true))
        {
            _logger.LogWarning("Job 'FullRuleDiscoveryJob' is disabled via configuration.");
            return;
        }

        var run = _state.TryStartRun("automatic");
        if (run == null)
        {
            var current = _state.GetCurrentRun();
            _logger.LogInformation("Full Rule Discovery already running. Mode: {Mode}.", current?.Mode);
            return;
        }

        await _dashboardLogService.AppendLogAsync(
            "Info",
            "FullRuleDiscovery",
            "Full Rule Discovery iniciado (automático).",
            JsonSerializer.Serialize(new { runId = run.RunId, mode = run.Mode }),
            null,
            CancellationToken.None);

        try
        {
            var token = _state.GetCancellationToken(run.RunId);
            var (processed, created, incomplete) = await _discoveryService.ExecuteDiscoveryAsync(
                token,
                () => _state.RefreshHeartbeat(run.RunId));

            var result = new FullRuleDiscoveryResultResponse
            {
                RunId = run.RunId,
                Mode = run.Mode,
                Status = "completed",
                Processed = processed,
                Created = created,
                Incomplete = incomplete,
                StartedAt = run.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow
            };
            _state.FinalizeSuccess(run.RunId, result);

            _logger.LogInformation("Full Rule Discovery finished. Processed: {Processed}, FULL rules created: {Created}, Incomplete: {Incomplete}.",
                processed, created, incomplete);
            await AppendSummaryLogAsync(result, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Full Rule Discovery cancelled.");
            _state.FinalizeCancelled(run.RunId, "Cancelado por usuario.");
            await _dashboardLogService.AppendLogAsync(
                "Warning",
                "FullRuleDiscovery",
                "Full Rule Discovery cancelado por usuario. No se hace rollback.",
                JsonSerializer.Serialize(new { runId = run.RunId, mode = run.Mode }),
                null,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Full Rule Discovery job failed.");
            _state.FinalizeFailure(run.RunId, ex.Message);
            await _dashboardLogService.AppendLogAsync(
                "Error",
                "FullRuleDiscovery",
                "Full Rule Discovery falló.",
                JsonSerializer.Serialize(new { runId = run.RunId, mode = run.Mode, error = ex.Message }),
                null,
                CancellationToken.None);
            throw;
        }
    }

    [Function("DiscoverFullRules")]
    public async Task<HttpResponseData> RunManual(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jobs/discover-full-rules")] HttpRequestData req)
    {
        if (!EnvVars.GetBool(EnvVars.Keys.EnableJobFullDiscovery, true))
        {
            var disabled = req.CreateResponse(HttpStatusCode.OK);
            await disabled.WriteAsJsonAsync(new { message = "Job is disabled.", run = false });
            return disabled;
        }

        var run = _state.TryStartRun("manual");
        if (run == null)
        {
            var current = _state.GetCurrentRun();
            await _dashboardLogService.AppendLogAsync(
                "Warning",
                "FullRuleDiscovery",
                "Ejecución manual bloqueada: ya hay un proceso en curso.",
                JsonSerializer.Serialize(new { runId = current?.RunId, mode = current?.Mode, status = current?.Status }),
                null,
                req.FunctionContext.CancellationToken);

            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteAsJsonAsync(new
            {
                message = "Ya existe una ejecución en curso.",
                runId = current?.RunId,
                mode = current?.Mode,
                status = current?.Status
            });
            return conflict;
        }

        await _dashboardLogService.AppendLogAsync(
            "Info",
            "FullRuleDiscovery",
            "Full Rule Discovery iniciado (manual).",
            JsonSerializer.Serialize(new { runId = run.RunId, mode = run.Mode }),
            null,
            req.FunctionContext.CancellationToken);

        await _queue.EnqueueAsync(new FullRuleDiscoveryQueueMessage
        {
            RunId = run.RunId,
            Mode = run.Mode,
            RequestedAtUtc = DateTimeOffset.UtcNow
        }, req.FunctionContext.CancellationToken);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new FullRuleDiscoveryStartResponse
        {
            RunId = run.RunId,
            Mode = run.Mode,
            Status = "running",
            StatusUrl = "/api/jobs/discover-full-rules/status"
        });
        return response;
    }

    [Function("DiscoverFullRulesStatus")]
    public async Task<HttpResponseData> GetStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs/discover-full-rules/status")] HttpRequestData req)
    {
        var status = _state.GetStatus();
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(status);
        return response;
    }

    [Function("DiscoverFullRulesCancel")]
    public async Task<HttpResponseData> Cancel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jobs/discover-full-rules/cancel")] HttpRequestData req)
    {
        var runId = req.Query["runId"];
        var cancelled = _state.RequestCancel(runId, out var currentRunId);
        if (!cancelled)
        {
            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteAsJsonAsync(new FullRuleDiscoveryCancelResponse
            {
                Cancelled = false,
                RunId = currentRunId,
                Message = "No hay ejecución activa para cancelar."
            });
            return conflict;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new FullRuleDiscoveryCancelResponse
        {
            Cancelled = true,
            RunId = currentRunId,
            Message = "Cancelación solicitada."
        });
        return response;
    }

    private async Task AppendSummaryLogAsync(FullRuleDiscoveryResultResponse result, CancellationToken ct)
    {
        var summaryMessage = "Full Rule Discovery finalizado. Publicaciones procesadas: " + result.Processed
            + ". Reglas FULL creadas: " + result.Created + ". Reglas incompletas: " + result.Incomplete + ".";
        var detailsJson = JsonSerializer.Serialize(new { processed = result.Processed, created = result.Created, incomplete = result.Incomplete });
        await _dashboardLogService.AppendLogAsync("Info", "FullRuleDiscovery", summaryMessage, detailsJson, null, ct);
    }
}
