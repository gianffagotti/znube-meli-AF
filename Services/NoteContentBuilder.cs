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
    private const string NotePrefix = "[A] ";
    private const int UsableBudget = 296;
    /// <summary>TOC line for sacrifice step 1. Spec: "(otros en 24hs)".</summary>
    private const string TocLine = "(otros en 24hs)";
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

        return string.Join("\n", lines);
    }

    public string BuildFinalNote(string? body)
    {
        var text = Compact(body ?? string.Empty);
        if (string.IsNullOrEmpty(text))
            return NotePrefix.TrimEnd();

        // Strict truncation pipeline (Spec 02): (1) TOC, (2) Zona, (3) Comprimir, (4) Truncar
        text = ApplyStrictTruncationPipeline(text);
        var truncated = SmartTruncate(text, UsableBudget);
        return NotePrefix + truncated;
    }

    /// <summary>Pipeline: remove TOC line → remove zone line → compress assignments → result (smart truncate applied after).</summary>
    private string ApplyStrictTruncationPipeline(string text)
    {
        var lines = text.Split('\n').Select(l => (l ?? string.Empty).Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0) return string.Empty;

        string Build() => string.Join("\n", lines);
        if (Build().Length <= UsableBudget) return Build();

        // (1) Eliminar Tag (TOC)
        var tocIdx = lines.FindIndex(l => l.Equals(TocLine, StringComparison.Ordinal) || l.Equals("(TOC)", StringComparison.Ordinal));
        if (tocIdx >= 0)
        {
            lines.RemoveAt(tocIdx);
            if (Build().Length <= UsableBudget) return Build();
        }

        // (2) Eliminar Zona — remove last line that looks like (zone)
        var zoneIdx = lines.FindLastIndex(l => l.StartsWith("(", StringComparison.Ordinal) && l.EndsWith(")", StringComparison.Ordinal) && l.Length > 2);
        if (zoneIdx >= 0)
        {
            lines.RemoveAt(zoneIdx);
            if (Build().Length <= UsableBudget) return Build();
        }

        // (3) Comprimir Asignaciones — drop last lines until under budget
        while (lines.Count > 1 && Build().Length > UsableBudget)
            lines.RemoveAt(lines.Count - 1);

        return Build();
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

        var assignmentOrder = new List<string>();
        var byAssignment = new Dictionary<string, AssignmentGroup>(StringComparer.Ordinal);

        foreach (var a in allocations)
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
