using meli_znube_integration.Common;
using meli_znube_integration.Core.Domain.Orders;

namespace meli_znube_integration.Core.Application.Orders;

/// <summary>
/// Pure formatter: builds Mercado Libre note text from allocation rows (legacy <c>NoteContentBuilder</c> behavior).
/// </summary>
public sealed class NoteFormatter : INoteFormatter
{
    private const int MaxNoteLength = 300;
    private const string NotePrefix = $"{NoteUtils.AutoTag} ";
    private readonly int _usableBudget = MaxNoteLength - NotePrefix.Length;
    private const string AssignmentSeparator = " / ";
    private const string TocLine = "(TOC)";
    private const string PackTag = "(P)";
    private const string ComboTag = "(C)";
    private const int MaxDetailedProducts = 9;

    /// <summary>Formats the final note with prefix <c>[A] </c> or <c>null</c> when there is nothing to publish.</summary>
    public string? Format(
        IReadOnlyList<AllocationResult>? allocations,
        string? destinationZone,
        bool addToc,
        bool hasPack,
        bool hasCombo)
    {
        if (allocations == null || allocations.Count == 0)
            return null;

        var body = BuildBody(allocations, destinationZone, addToc, hasPack, hasCombo);
        if (string.IsNullOrWhiteSpace(body))
            return null;

        return BuildFinalNote(body);
    }

    private string BuildBody(
        IReadOnlyList<AllocationResult> allocations,
        string? destinationZone,
        bool addToc,
        bool hasPack,
        bool hasCombo)
    {
        var lines = BuildGroupedLines(allocations);
        if (lines.Count == 0)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(destinationZone))
            lines.Add($"({destinationZone.Trim()})");
        if (addToc)
            lines.Add(TocLine);

        var body = string.Join("\n", lines);
        var packComboSuffix = BuildPackComboSuffix(hasPack, hasCombo);
        if (!string.IsNullOrEmpty(packComboSuffix))
            body += packComboSuffix;
        return body;
    }

    private static string BuildPackComboSuffix(bool hasPack, bool hasCombo)
    {
        var parts = new List<string>();
        if (hasPack) parts.Add(PackTag);
        if (hasCombo) parts.Add(ComboTag);
        return parts.Count == 0 ? string.Empty : " " + string.Join(" ", parts);
    }

    private string? BuildFinalNote(string body)
    {
        var text = Compact(body);
        if (string.IsNullOrEmpty(text))
            return null;

        var (assignmentLines, trailerLines) = ParseLines(text);
        var bodyDisplay = BuildDisplayBody(assignmentLines, trailerLines);
        bodyDisplay = ApplyStrictTruncationPipeline(assignmentLines, trailerLines, bodyDisplay);

        var truncated = SmartTruncate(bodyDisplay, _usableBudget);
        var result = NotePrefix + truncated;
        if (result.Length > MaxNoteLength)
            result = NotePrefix + SmartTruncate(truncated, MaxNoteLength - NotePrefix.Length);
        return result.Length > MaxNoteLength ? result[..MaxNoteLength] : result;
    }

    private static (List<string> assignmentLines, List<string> trailerLines) ParseLines(string text)
    {
        var lines = text.Split('\n').Select(l => (l ?? string.Empty).Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var assignmentLines = new List<string>();
        var trailerLines = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith('('))
                trailerLines.Add(line);
            else
                assignmentLines.Add(line);
        }
        return (assignmentLines, trailerLines);
    }

    private static string BuildDisplayBody(List<string> assignmentLines, List<string> trailerLines)
    {
        var body = string.Join(AssignmentSeparator, assignmentLines);
        if (trailerLines.Count > 0)
            body += " " + string.Join(" ", trailerLines);
        return body;
    }

    private string ApplyStrictTruncationPipeline(List<string> assignmentLines, List<string> trailerLines, string currentDisplay)
    {
        if (currentDisplay.Length <= _usableBudget) return currentDisplay;

        for (var i = 0; i < trailerLines.Count; i++)
        {
            var line = trailerLines[i];
            if (!line.Contains(TocLine, StringComparison.Ordinal)) continue;
            var removed = line.Replace(TocLine, "", StringComparison.Ordinal).Trim();
            while (removed.Contains("  ", StringComparison.Ordinal))
                removed = removed.Replace("  ", " ", StringComparison.Ordinal);
            if (string.IsNullOrEmpty(removed))
                trailerLines.RemoveAt(i);
            else
                trailerLines[i] = removed;
            currentDisplay = BuildDisplayBody(assignmentLines, trailerLines);
            if (currentDisplay.Length <= _usableBudget) return currentDisplay;
            break;
        }

        var zoneIdx = trailerLines.FindIndex(l =>
        {
            if (!l.StartsWith('(') || !l.EndsWith(')') || l.Length <= 2) return false;
            if (string.Equals(l, PackTag, StringComparison.Ordinal) || string.Equals(l, ComboTag, StringComparison.Ordinal) || string.Equals(l, TocLine, StringComparison.Ordinal)) return false;
            var firstClose = l.IndexOf(')', StringComparison.Ordinal);
            return firstClose >= 0 && firstClose == l.Length - 1;
        });
        if (zoneIdx >= 0)
        {
            trailerLines.RemoveAt(zoneIdx);
            currentDisplay = BuildDisplayBody(assignmentLines, trailerLines);
            if (currentDisplay.Length <= _usableBudget) return currentDisplay;
        }

        while (assignmentLines.Count > 1 && currentDisplay.Length > _usableBudget)
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
        var cut = text[..max];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > max / 2)
            return cut[..lastSpace];
        return cut;
    }

    private static string Compact(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Split('\n').Select(l => (l ?? string.Empty).Trim()).Where(l => !string.IsNullOrWhiteSpace(l));
        return string.Join("\n", lines);
    }

    private static List<string> BuildGroupedLines(IEnumerable<AllocationResult> allocations)
    {
        var result = new List<string>();
        var normalAllocations = allocations.Where(a => a != null).ToList();
        if (normalAllocations.Count == 0)
            return result;

        var assignmentOrder = new List<string>();
        var byAssignment = new Dictionary<string, AssignmentGroup>(StringComparer.Ordinal);

        foreach (var a in normalAllocations)
        {
            var assignment = a.ResourceName ?? string.Empty;
            var product = a.Label ?? string.Empty;
            var qty = a.AllocatedQuantity;
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
            .OrderBy(a => a.Count)
            .ThenBy(a => a.Index)
            .ToList();

        for (var i = 0; i < indexedAssignments.Count; i++)
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
        return trimmed.Length <= 3 ? trimmed : trimmed[..3];
    }

    private sealed class AssignmentGroup
    {
        public List<string> ProductOrder { get; } = new();
        public Dictionary<string, int> ProductToQty { get; } = new(StringComparer.Ordinal);

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
