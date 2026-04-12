using meli_znube_integration.Models.Dtos;

namespace meli_znube_integration.Services;

public sealed class FullRuleDiscoveryRunInfo
{
    public string RunId { get; init; } = "";
    public string Mode { get; init; } = "manual";
    public string Status { get; init; } = "running";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public class FullRuleDiscoveryStateService
{
    private sealed class RunState
    {
        public string RunId { get; init; } = "";
        public string Mode { get; init; } = "manual";
        public string Status { get; set; } = "running";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public CancellationTokenSource Cancellation { get; init; } = new();
    }

    private readonly object _sync = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(45);
    private RunState? _current;
    private FullRuleDiscoveryResultResponse? _lastResult;

    public FullRuleDiscoveryStatusResponse GetStatus()
    {
        lock (_sync)
        {
            CleanupExpiredLocked();
            if (_current == null)
            {
                return new FullRuleDiscoveryStatusResponse
                {
                    IsRunning = false,
                    LastResult = _lastResult
                };
            }

            return new FullRuleDiscoveryStatusResponse
            {
                IsRunning = IsActive(_current),
                RunId = _current.RunId,
                Mode = _current.Mode,
                Status = _current.Status,
                StartedAt = _current.StartedAt,
                UpdatedAt = _current.UpdatedAt,
                LastResult = _lastResult
            };
        }
    }

    public FullRuleDiscoveryRunInfo? TryStartRun(string mode)
    {
        lock (_sync)
        {
            CleanupExpiredLocked();
            if (_current != null && IsActive(_current))
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var run = new RunState
            {
                RunId = Guid.NewGuid().ToString("N"),
                Mode = mode,
                Status = "running",
                StartedAt = now,
                UpdatedAt = now,
                ExpiresAt = now.Add(_ttl),
                Cancellation = new CancellationTokenSource()
            };
            _current = run;
            return ToInfo(run);
        }
    }

    public void RefreshHeartbeat(string runId)
    {
        lock (_sync)
        {
            if (_current == null || !string.Equals(_current.RunId, runId, StringComparison.Ordinal))
                return;
            if (!IsActive(_current))
                return;
            var now = DateTimeOffset.UtcNow;
            _current.UpdatedAt = now;
            _current.ExpiresAt = now.Add(_ttl);
        }
    }

    public bool RequestCancel(string? runId, out string? currentRunId)
    {
        lock (_sync)
        {
            currentRunId = _current?.RunId;
            if (_current == null || !IsActive(_current))
                return false;
            if (!string.IsNullOrWhiteSpace(runId) && !string.Equals(runId, _current.RunId, StringComparison.Ordinal))
                return false;

            _current.Status = "cancelled";
            _current.UpdatedAt = DateTimeOffset.UtcNow;
            _current.Cancellation.Cancel();
            return true;
        }
    }

    public CancellationToken GetCancellationToken(string runId)
    {
        lock (_sync)
        {
            if (_current == null || !string.Equals(_current.RunId, runId, StringComparison.Ordinal))
                return CancellationToken.None;
            return _current.Cancellation.Token;
        }
    }

    public void FinalizeSuccess(string runId, FullRuleDiscoveryResultResponse result)
    {
        lock (_sync)
        {
            if (_current == null || !string.Equals(_current.RunId, runId, StringComparison.Ordinal))
                return;
            _current.Status = "completed";
            _current.UpdatedAt = DateTimeOffset.UtcNow;
            _lastResult = result;
            _current = null;
        }
    }

    public void FinalizeFailure(string runId, string message)
    {
        lock (_sync)
        {
            if (_current == null || !string.Equals(_current.RunId, runId, StringComparison.Ordinal))
                return;
            var now = DateTimeOffset.UtcNow;
            _current.Status = "failed";
            _current.UpdatedAt = now;
            _lastResult = new FullRuleDiscoveryResultResponse
            {
                RunId = runId,
                Mode = _current.Mode,
                Status = "failed",
                StartedAt = _current.StartedAt,
                CompletedAt = now,
                Message = message
            };
            _current = null;
        }
    }

    public void FinalizeCancelled(string runId, string? message = null)
    {
        lock (_sync)
        {
            if (_current == null || !string.Equals(_current.RunId, runId, StringComparison.Ordinal))
                return;
            var now = DateTimeOffset.UtcNow;
            _current.Status = "cancelled";
            _current.UpdatedAt = now;
            _lastResult = new FullRuleDiscoveryResultResponse
            {
                RunId = runId,
                Mode = _current.Mode,
                Status = "cancelled",
                StartedAt = _current.StartedAt,
                CompletedAt = now,
                Message = message
            };
            _current = null;
        }
    }

    public FullRuleDiscoveryResultResponse? GetLastResult()
    {
        lock (_sync)
        {
            return _lastResult;
        }
    }

    public FullRuleDiscoveryRunInfo? GetCurrentRun()
    {
        lock (_sync)
        {
            CleanupExpiredLocked();
            if (_current == null) return null;
            return ToInfo(_current);
        }
    }

    private static bool IsActive(RunState run)
    {
        return !string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase);
    }

    private void CleanupExpiredLocked()
    {
        if (_current == null) return;
        if (!IsActive(_current)) return;
        var now = DateTimeOffset.UtcNow;
        if (_current.ExpiresAt <= now)
        {
            _lastResult = new FullRuleDiscoveryResultResponse
            {
                RunId = _current.RunId,
                Mode = _current.Mode,
                Status = "failed",
                StartedAt = _current.StartedAt,
                CompletedAt = now,
                Message = "Lock expirado."
            };
            _current = null;
        }
    }

    private static FullRuleDiscoveryRunInfo ToInfo(RunState run)
    {
        return new FullRuleDiscoveryRunInfo
        {
            RunId = run.RunId,
            Mode = run.Mode,
            Status = run.Status,
            StartedAt = run.StartedAt,
            UpdatedAt = run.UpdatedAt
        };
    }
}
