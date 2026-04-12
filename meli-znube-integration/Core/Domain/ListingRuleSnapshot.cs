namespace meli_znube_integration.Core.Domain;

/// <summary>
/// Minimal placeholder for listing stock rules (Pack/Combo, etc.) resolved from external listings.
/// Expand with structured fields when application services need them.
/// </summary>
public sealed class ListingRuleSnapshot
{
    public string? ExternalTargetItemId { get; init; }

    public string? RuleKind { get; init; }
}
