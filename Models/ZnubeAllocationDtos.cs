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
