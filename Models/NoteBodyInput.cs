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
    /// <summary>When true, add "(P)" in the note (order has items from a PACK rule).</summary>
    public bool HasPack { get; set; }
    /// <summary>When true, add "(C)" in the note (order has items from a COMBO rule).</summary>
    public bool HasCombo { get; set; }
}
