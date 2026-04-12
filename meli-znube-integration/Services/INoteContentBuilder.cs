using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

/// <summary>
/// Pure note content building (no I/O). Spec 02. Testable without MELI/Znube.
/// </summary>
public interface INoteContentBuilder
{
    /// <summary>Builds the note body from pre-resolved data (allocations, zone, TOC flag).</summary>
    string BuildBody(NoteBodyInput input);

    /// <summary>Applies [A] prefix and strict truncation pipeline; returns final note (max 300 chars).</summary>
    string BuildFinalNote(string? body);
}
