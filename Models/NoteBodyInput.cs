namespace meli_znube_integration.Models;

/// <summary>
/// Pre-resolved data for building note body (pure input, no I/O). Spec 02.
/// </summary>
public class NoteBodyInput
{
    public List<ZnubeAllocationEntry> Allocations { get; set; } = new();
    /// <summary>Optional Flex zone line, e.g. "Zona Norte".</summary>
    public string? Zone { get; set; }
    /// <summary>When true, add "(otros en 24hs)" line (TOC).</summary>
    public bool AddToc { get; set; }
}
