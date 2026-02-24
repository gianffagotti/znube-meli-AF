namespace meli_znube_integration.Models;

/// <summary>
/// Single allocation line: product label, assignment (resource name or fallback text), quantity.
/// </summary>
public class ZnubeAllocationEntry
{
    public string ProductLabel { get; set; } = string.Empty;
    public string AssignmentName { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

/// <summary>
/// Result of getting allocations for orders; includes flags for pack/combo rule types (regla tipo PACK, regla tipo COMBO) for note generation.
/// </summary>
public class OrderAllocationResult
{
    public List<ZnubeAllocationEntry> Allocations { get; set; } = new();
    /// <summary>True when at least one item comes from a PACK rule (regla tipo pack).</summary>
    public bool HasPack { get; set; }
    /// <summary>True when at least one item comes from a COMBO rule (regla tipo combo).</summary>
    public bool HasCombo { get; set; }
}
