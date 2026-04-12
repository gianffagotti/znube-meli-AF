using meli_znube_integration.Common;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace meli_znube_integration.Functions;

public class FullRuleDiscoveryQueueWorker
{
    private readonly FullRuleDiscoveryService _discoveryService;
    private readonly FullRuleDiscoveryStateService _state;
    private readonly IDashboardLogService _dashboardLogService;
    private readonly ILogger<FullRuleDiscoveryQueueWorker> _logger;

    public FullRuleDiscoveryQueueWorker(
        FullRuleDiscoveryService discoveryService,
        FullRuleDiscoveryStateService state,
        IDashboardLogService dashboardLogService,
        ILogger<FullRuleDiscoveryQueueWorker> logger)
    {
        _discoveryService = discoveryService;
        _state = state;
        _dashboardLogService = dashboardLogService;
        _logger = logger;
    }

    [Function("FullRuleDiscoveryQueueWorker")]
    public async Task Run(
        [QueueTrigger("%FULL_RULE_DISCOVERY_QUEUE_NAME%", Connection = "AZURE_STORAGE_CONNECTION_STRING")] string message,
        FunctionContext context)
    {
        if (!EnvVars.GetBool(EnvVars.Keys.EnableJobFullDiscovery, true))
        {
            _logger.LogWarning("Job 'FullRuleDiscoveryJob' is disabled via configuration.");
            return;
        }

        FullRuleDiscoveryQueueMessage? payload;
        try
        {
            payload = JsonSerializer.Deserialize<FullRuleDiscoveryQueueMessage>(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid Full Rule Discovery queue message payload.");
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.RunId))
        {
            _logger.LogWarning("Full Rule Discovery queue message missing runId.");
            return;
        }

        var current = _state.GetCurrentRun();
        if (current == null || !string.Equals(current?.RunId, payload.RunId, StringComparison.Ordinal))
        {
            _logger.LogInformation("Ignoring Full Rule Discovery queue message. RunId: {RunId}", payload.RunId);
            return;
        }

        try
        {
            var token = _state.GetCancellationToken(payload.RunId);
            var (processed, created, incomplete) = await _discoveryService.ExecuteDiscoveryAsync(
                token,
                () => _state.RefreshHeartbeat(payload.RunId));

            var result = new FullRuleDiscoveryResultResponse
            {
                RunId = payload.RunId,
                Mode = current?.Mode,
                Status = "completed",
                Processed = processed,
                Created = created,
                Incomplete = incomplete,
                StartedAt = current?.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow
            };
            _state.FinalizeSuccess(payload.RunId, result);

            _logger.LogInformation("Full Rule Discovery finished. Processed: {Processed}, FULL rules created: {Created}, Incomplete: {Incomplete}.",
                processed, created, incomplete);
            var summaryMessage = "Full Rule Discovery finalizado. Publicaciones procesadas: " + processed + ". Reglas FULL creadas: " + created + ". Reglas incompletas: " + incomplete + ".";
            var detailsJson = JsonSerializer.Serialize(new { processed, created, incomplete });
            await _dashboardLogService.AppendLogAsync("Info", "FullRuleDiscovery", summaryMessage, detailsJson, null, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Full Rule Discovery cancelled.");
            _state.FinalizeCancelled(payload.RunId, "Cancelado por usuario.");
            await _dashboardLogService.AppendLogAsync(
                "Warning",
                "FullRuleDiscovery",
                "Full Rule Discovery cancelado por usuario. No se hace rollback.",
                JsonSerializer.Serialize(new { runId = payload.RunId, mode = current?.Mode }),
                null,
                context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Full Rule Discovery queue execution failed.");
            _state.FinalizeFailure(payload.RunId, ex.Message);
            await _dashboardLogService.AppendLogAsync(
                "Error",
                "FullRuleDiscovery",
                "Full Rule Discovery falló.",
                JsonSerializer.Serialize(new { runId = payload.RunId, mode = current?.Mode, error = ex.Message }),
                null,
                context.CancellationToken);
            throw;
        }
    }
}
