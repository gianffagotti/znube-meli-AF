using meli_znube_integration.Common;
using meli_znube_integration.Models;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

/// <summary>
/// Pure note content builder. Prefix [A], budget 296. Strict truncation: TOC → Zona → Comprimir → Truncar. Spec 02.
/// </summary>
public class NoteContentBuilder : INoteContentBuilder
{
    private const int MaxNoteLength = 300;
    /// <summary>Prefix [A] + space = 4 chars. Spec 02. Usable body budget = 296.</summary>
    private const string NotePrefix = $"{NoteUtils.AutoTag} ";
    private readonly int UsableBudget = MaxNoteLength - NotePrefix.Length;
    /// <summary>Spec 02: asignaciones separadas por " / ", resto con espacios (una sola línea para ML).</summary>
    private const string AssignmentSeparator = " / ";
    /// <summary>TOC (otros en 24hs). Usado al agregar y al eliminar en el pipeline.</summary>
    private const string TocLine = "(TOC)";
    private const string PackTag = "(P)";
    private const string ComboTag = "(C)";
    private const int MaxDetailedProducts = 9;

    private readonly ILogger<NoteContentBuilder>? _logger;

    public NoteContentBuilder(ILogger<NoteContentBuilder>? logger = null)
    {
        _logger = logger;
    }

    public string BuildBody(NoteBodyInput input)
    {
        if (input?.Allocations == null || input.Allocations.Count == 0)
            return string.Empty;

        var lines = BuildGroupedLines(input.Allocations);
        if (!string.IsNullOrWhiteSpace(input.Zone))
            lines.Add($"({input.Zone.Trim()})");
        if (input.AddToc)
            lines.Add(TocLine);

        var body = string.Join("\n", lines);
        var packComboSuffix = BuildPackComboSuffix(input.HasPack, input.HasCombo);
        if (!string.IsNullOrEmpty(packComboSuffix))
            body = body + packComboSuffix;
        return body;
    }

    /// <summary>Spec 02: sufijos PACK/COMBO → " (P)", " (C)" o " (P) (C)".</summary>
    private static string BuildPackComboSuffix(bool hasPack, bool hasCombo)
    {
        var parts = new List<string>();
        if (hasPack) parts.Add(PackTag);
        if (hasCombo) parts.Add(ComboTag);
        return parts.Count == 0 ? string.Empty : " " + string.Join(" ", parts);
    }

    public string BuildFinalNote(string? body)
    {
        var text = Compact(body ?? string.Empty);
        if (string.IsNullOrEmpty(text))
            return NotePrefix.TrimEnd();

        // Spec 02: pipeline sobre líneas (1) TOC, (2) Zona, (3) Comprimir; luego smart truncate a 296
        var (assignmentLines, trailerLines) = ParseLines(text);
        var bodyDisplay = BuildDisplayBody(assignmentLines, trailerLines);
        bodyDisplay = ApplyStrictTruncationPipeline(assignmentLines, trailerLines, bodyDisplay);

        var truncated = SmartTruncate(bodyDisplay, UsableBudget);
        var result = NotePrefix + truncated;
        if (result.Length > MaxNoteLength)
            result = NotePrefix + SmartTruncate(truncated, MaxNoteLength - NotePrefix.Length);
        return result.Length > MaxNoteLength ? result.Substring(0, MaxNoteLength) : result;
    }

    /// <summary>Separa líneas en asignaciones (no empiezan con '(') y trailer (zona, TOC, P/C).</summary>
    private static (List<string> assignmentLines, List<string> trailerLines) ParseLines(string text)
    {
        var lines = text.Split('\n').Select(l => (l ?? string.Empty).Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var assignmentLines = new List<string>();
        var trailerLines = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("(", StringComparison.Ordinal))
                trailerLines.Add(line);
            else
                assignmentLines.Add(line);
        }
        return (assignmentLines, trailerLines);
    }

    /// <summary>Spec 02: una sola línea para ML — asignaciones con " / ", resto con espacio.</summary>
    private static string BuildDisplayBody(List<string> assignmentLines, List<string> trailerLines)
    {
        var body = string.Join(AssignmentSeparator, assignmentLines);
        if (trailerLines.Count > 0)
            body = body + " " + string.Join(" ", trailerLines);
        return body;
    }

    /// <summary>Spec 02: (1) quitar TOC, (2) quitar zona, (3) quitar asignaciones desde el final hasta ≤ 296.</summary>
    private string ApplyStrictTruncationPipeline(List<string> assignmentLines, List<string> trailerLines, string currentDisplay)
    {
        if (currentDisplay.Length <= UsableBudget) return currentDisplay;

        // (1) Eliminar tag TOC (quitar solo el token, no la línea si tiene también (P)/(C))
        for (int i = 0; i < trailerLines.Count; i++)
        {
            var line = trailerLines[i];
            if (!line.Contains(TocLine, StringComparison.Ordinal)) continue;
            var removed = line.Replace(TocLine, "").Trim();
            while (removed.Contains("  ", StringComparison.Ordinal))
                removed = removed.Replace("  ", " ", StringComparison.Ordinal);
            if (string.IsNullOrEmpty(removed))
                trailerLines.RemoveAt(i);
            else
                trailerLines[i] = removed;
            currentDisplay = BuildDisplayBody(assignmentLines, trailerLines);
            if (currentDisplay.Length <= UsableBudget) return currentDisplay;
            break;
        }

        // (2) Eliminar zona — línea que es un solo token (xxx), no (P)/(C)/(TOC); no tocar "(Villa Martelli) (P)"
        var zoneIdx = trailerLines.FindIndex(l =>
        {
            if (!l.StartsWith("(", StringComparison.Ordinal) || !l.EndsWith(")", StringComparison.Ordinal) || l.Length <= 2) return false;
            if (l.Equals(PackTag, StringComparison.Ordinal) || l.Equals(ComboTag, StringComparison.Ordinal) || l.Equals(TocLine, StringComparison.Ordinal)) return false;
            var firstClose = l.IndexOf(')');
            if (firstClose < 0 || firstClose != l.Length - 1) return false;
            return true;
        });
        if (zoneIdx >= 0)
        {
            trailerLines.RemoveAt(zoneIdx);
            currentDisplay = BuildDisplayBody(assignmentLines, trailerLines);
            if (currentDisplay.Length <= UsableBudget) return currentDisplay;
        }

        // (3) Comprimir asignaciones — quitar líneas desde el final
        while (assignmentLines.Count > 1 && currentDisplay.Length > UsableBudget)
        {
            assignmentLines.RemoveAt(assignmentLines.Count - 1);
            currentDisplay = BuildDisplayBody(assignmentLines, trailerLines);
        }

        return currentDisplay;
    }

    private static string SmartTruncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return string.Empty;
        if (text.Length <= max) return text;
        var cut = text.Substring(0, max);
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > max / 2)
            return cut.Substring(0, lastSpace);
        return cut;
    }

    /// <summary>Spec 02: compactar cuerpo (trim por línea, quitar vacías).</summary>
    private static string Compact(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Split('\n').Select(l => (l ?? string.Empty).Trim()).Where(l => !string.IsNullOrWhiteSpace(l));
        return string.Join("\n", lines);
    }

    private static List<string> BuildGroupedLines(IEnumerable<ZnubeAllocationEntry> allocations)
    {
        var result = new List<string>();
        if (allocations == null) return result;

        var normalAllocations = allocations.Where(a => a != null).ToList();
        if (normalAllocations.Count == 0)
            return result;

        var assignmentOrder = new List<string>();
        var byAssignment = new Dictionary<string, AssignmentGroup>(StringComparer.Ordinal);

        foreach (var a in normalAllocations)
        {
            if (a == null) continue;
            var assignment = a.AssignmentName ?? string.Empty;
            var product = a.ProductLabel ?? string.Empty;
            var qty = a.Quantity;
            if (!byAssignment.TryGetValue(assignment, out var group))
            {
                group = new AssignmentGroup();
                byAssignment[assignment] = group;
                assignmentOrder.Add(assignment);
            }
            group.Add(product, qty);
        }

        var totalProducts = byAssignment.Values.Sum(g => g.ProductOrder.Count);

        if (totalProducts <= MaxDetailedProducts)
        {
            foreach (var assignment in assignmentOrder)
            {
                if (!byAssignment.TryGetValue(assignment, out var group)) continue;
                var parts = new List<string>();
                foreach (var p in group.ProductOrder)
                {
                    var q = group.ProductToQty[p];
                    parts.Add(q > 1 ? $"{p} x{q}" : p);
                }
                var shortAssignment = AbbrevAssignmentLabel(assignment);
                var line = string.IsNullOrWhiteSpace(shortAssignment)
                    ? string.Join(" + ", parts)
                    : $"{shortAssignment}: " + string.Join(" + ", parts);
                if (!string.IsNullOrWhiteSpace(line)) result.Add(line);
            }
            return result;
        }

        var indexedAssignments = assignmentOrder
            .Select((name, index) => new { Name = name, Index = index, Count = byAssignment.TryGetValue(name, out var g) ? g.ProductOrder.Count : 0 })
            .OrderBy(a => a.Count).ThenBy(a => a.Index)
            .ToList();

        for (int i = 0; i < indexedAssignments.Count; i++)
        {
            var entry = indexedAssignments[i];
            if (!byAssignment.TryGetValue(entry.Name, out var group)) continue;
            var shortAssignment = AbbrevAssignmentLabel(entry.Name);
            string line;
            if (i == indexedAssignments.Count - 1)
                line = string.IsNullOrWhiteSpace(shortAssignment) ? "Restante" : $"{shortAssignment}: Restante";
            else
            {
                var parts = group.ProductOrder.Select(p => group.ProductToQty[p] > 1 ? $"{p} x{group.ProductToQty[p]}" : p).ToList();
                line = string.IsNullOrWhiteSpace(shortAssignment) ? string.Join(" + ", parts) : $"{shortAssignment}: " + string.Join(" + ", parts);
            }
            if (!string.IsNullOrWhiteSpace(line)) result.Add(line);
        }
        return result;
    }

    private static string AbbrevAssignmentLabel(string assignment)
    {
        if (string.IsNullOrWhiteSpace(assignment)) return string.Empty;
        var trimmed = assignment.Trim();
        var normalized = NoteUtils.RemoveDiacritics(trimmed).ToLowerInvariant();
        if (normalized == "sin asignacion") return "SA";
        if (normalized == "sin stock") return "SS";
        return trimmed.Length <= 3 ? trimmed : trimmed.Substring(0, 3);
    }

    private sealed class AssignmentGroup
    {
        public List<string> ProductOrder { get; } = new List<string>();
        public Dictionary<string, int> ProductToQty { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        public void Add(string product, int qty)
        {
            if (string.IsNullOrWhiteSpace(product)) return;
            if (qty <= 0) qty = 1;
            if (!ProductToQty.ContainsKey(product))
            {
                ProductOrder.Add(product);
                ProductToQty[product] = qty;
            }
            else
                ProductToQty[product] += qty;
        }
    }
}
