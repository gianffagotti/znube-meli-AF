namespace meli_znube_integration.Models.Dtos;

public class FullRuleDiscoveryQueueMessage
{
    public string RunId { get; set; } = "";
    public string Mode { get; set; } = "manual";
    public DateTimeOffset RequestedAtUtc { get; set; }
}

public class FullRuleDiscoveryStartResponse
{
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "running";
    public string Mode { get; set; } = "manual";
    public string StatusUrl { get; set; } = "";
}

public class FullRuleDiscoveryCancelResponse
{
    public bool Cancelled { get; set; }
    public string? RunId { get; set; }
    public string? Message { get; set; }
}

public class FullRuleDiscoveryResultResponse
{
    public string? RunId { get; set; }
    public string? Mode { get; set; }
    public string Status { get; set; } = "";
    public int Processed { get; set; }
    public int Created { get; set; }
    public int Incomplete { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Message { get; set; }
}

public class FullRuleDiscoveryStatusResponse
{
    public bool IsRunning { get; set; }
    public string? RunId { get; set; }
    public string? Mode { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public FullRuleDiscoveryResultResponse? LastResult { get; set; }
}
