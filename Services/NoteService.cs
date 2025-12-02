namespace meli_znube_integration.Services;

using meli_znube_integration.Clients;
using meli_znube_integration.Models;
using meli_znube_integration.Common;

public class NoteService
{
    private const int MaxNoteLength = 300;
    private const string Other24hTag = "(TOC)";
    private const int MaxDetailedProducts = 9;

    private readonly MeliClient _meliClient;
    private readonly ZnubeClient _znubeClient;

    public NoteService(MeliClient meliClient, ZnubeClient znubeClient)
    {
        _meliClient = meliClient;
        _znubeClient = znubeClient;
    }

    public async Task<string?> BuildSingleOrderBodyAsync(MeliOrder order, string accessToken)
    {
        string? zone = null;
        try
        {
            var shipment = await _meliClient.GetShipmentInfoAsync(order, accessToken);
            if (shipment != null)
            {
                if (shipment.IsFull)
                {
                    return null; // mantener comportamiento: si es FULL, se omite
                }
                if (shipment.IsFlex)
                {
                    zone = shipment.Zone;
                }
            }
        }
        catch { }

        var allocations = await _znubeClient.GetAllocationsForOrderAsync(order);
        var lines = BuildGroupedLines(allocations);
        if (!string.IsNullOrWhiteSpace(zone))
        {
            lines.Add($"({zone})");
        }

        // Chequeo 24h: ¿el comprador hizo otra compra en las últimas 24 horas?
        try
        {
            var sellerId = EnvVars.GetString(EnvVars.Keys.MeliSellerId);
            if (!string.IsNullOrWhiteSpace(sellerId)
                && order.DateCreatedUtc.HasValue
                && !string.IsNullOrWhiteSpace(order.BuyerNickname))
            {
                var to = order.DateCreatedUtc.Value;
                var from = to.AddHours(-24);
                var hasOther = await _meliClient.HasTwoOrMoreOrdersByBuyerAsync(from, to, order.BuyerNickname!, sellerId!, accessToken);
                if (hasOther)
                {
                    lines.Add(Other24hTag);
                }
            }
        }
        catch { }
        var body = string.Join("\n", lines);
        return body;
    }

    public async Task<string?> BuildConsolidatedBodyAsync(IEnumerable<MeliOrder> orders, string accessToken)
    {
        var orderList = orders?.Where(o => o != null).ToList() ?? new List<MeliOrder>();
        if (orderList.Count == 0) return null;

        // Última orden por fecha o id
        var last = orderList
            .OrderBy(o => o.DateCreatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(o => NoteUtils.TryParseLong(o.Id))
            .Last();

        // Reglas de envío aplicadas a última orden
        string? zone = null;
        try
        {
            var shipment = await _meliClient.GetShipmentInfoAsync(last, accessToken);
            if (shipment != null)
            {
                if (shipment.IsFull) return null; // si es FULL, se omite nota
                if (shipment.IsFlex) zone = shipment.Zone;
            }
        }
        catch { }

        // Consolidar asignaciones estructuradas considerando stock global entre órdenes
        var globalAllocations = await _znubeClient.GetAllocationsForOrdersAsync(orderList);
        var lines = BuildGroupedLines(globalAllocations);
        if (!string.IsNullOrWhiteSpace(zone))
        {
            lines.Add($"({zone})");
        }

        // Chequeo 24h para packs, usando la última orden como referencia
        try
        {
            var sellerId = EnvVars.GetString(EnvVars.Keys.MeliSellerId);
            if (!string.IsNullOrWhiteSpace(sellerId)
                && last.DateCreatedUtc.HasValue
                && !string.IsNullOrWhiteSpace(last.BuyerNickname))
            {
                var to = last.DateCreatedUtc.Value;
                var from = to.AddHours(-24);
                var hasOther = await _meliClient.HasTwoOrMoreOrdersByBuyerAsync(from, to, last.BuyerNickname!, sellerId!, accessToken);
                if (hasOther)
                {
                    lines.Add(Other24hTag);
                }
            }
        }
        catch { }
        return string.Join("\n", lines);
    }

    public string BuildFinalNote(string? body)
    {
        var text = body ?? string.Empty;
        text = Compact(text);
        var header = NoteUtils.AutoTag + " ";
        var available = Math.Max(0, MaxNoteLength - header.Length);
        var truncated = Truncate(text, available);
        return NoteUtils.EnsureAutoPrefix(truncated);
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (max <= 0) return string.Empty;
        if (text.Length <= max) return text;
        return text.Substring(0, max);
    }

    private static string Compact(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Split('\n');
        var clean = lines.Select(l => (l ?? string.Empty).Trim()).Where(l => !string.IsNullOrWhiteSpace(l));
        return string.Join("\n", clean);
    }

    private static List<string> BuildGroupedLines(IEnumerable<ZnubeClient.AllocationEntry> allocations)
    {
        var result = new List<string>();
        if (allocations == null) return result;

        // Mantener orden de aparición de asignaciones y de productos dentro de cada asignación
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

        // Calcular total de productos (distintos) en todas las asignaciones
        var totalProducts = byAssignment.Values.Sum(g => g.ProductOrder.Count);

        // Si el total es menor o igual al límite, mantener el comportamiento actual y el orden original
        if (totalProducts <= MaxDetailedProducts)
        {
            foreach (var assignment in assignmentOrder)
            {
                if (!byAssignment.TryGetValue(assignment, out var group)) continue;
                var parts = new List<string>();
                foreach (var p in group.ProductOrder)
                {
                    var q = group.ProductToQty[p];
                    var suffix = q > 1 ? $" x{q}" : string.Empty;
                    parts.Add(p + suffix);
                }
                var shortAssignment = AbbrevAssignmentLabel(assignment);
                var line = string.IsNullOrWhiteSpace(shortAssignment)
                    ? string.Join(" + ", parts)
                    : $"{shortAssignment}: " + string.Join(" + ", parts);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    result.Add(line);
                }
            }
            return result;
        }

        // Si hay más productos que el límite, ordenar asignaciones por cantidad ascendente
        // y resumir la de mayor cantidad como "Restante".
        var indexedAssignments = assignmentOrder
            .Select((name, index) => new
            {
                Name = name,
                Index = index,
                Count = byAssignment.TryGetValue(name, out var g) ? g.ProductOrder.Count : 0
            })
            .ToList();

        var sorted = indexedAssignments
            .OrderBy(a => a.Count)
            .ThenBy(a => a.Index)
            .ToList();

        // La última es la de mayor cantidad (en caso de empate, estable por orden original)
        for (int i = 0; i < sorted.Count; i++)
        {
            var entry = sorted[i];
            if (!byAssignment.TryGetValue(entry.Name, out var group)) continue;

            string line;
                var shortAssignment = AbbrevAssignmentLabel(entry.Name);

            // Para la de mayor cantidad, usar "Restante"
            if (i == sorted.Count - 1)
            {
                line = string.IsNullOrWhiteSpace(shortAssignment)
                    ? "Restante"
                    : $"{shortAssignment}: Restante";
            }
            else
            {
                var parts = new List<string>();
                foreach (var p in group.ProductOrder)
                {
                    var q = group.ProductToQty[p];
                    var suffix = q > 1 ? $" x{q}" : string.Empty;
                    parts.Add(p + suffix);
                }
                line = string.IsNullOrWhiteSpace(shortAssignment)
                    ? string.Join(" + ", parts)
                    : $"{shortAssignment}: " + string.Join(" + ", parts);
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                result.Add(line);
            }
        }

        return result;
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
            {
                ProductToQty[product] += qty;
            }
        }
    }

    private static string AbbrevAssignmentLabel(string assignment)
    {
        if (string.IsNullOrWhiteSpace(assignment)) return string.Empty;
        var trimmed = assignment.Trim();
        var normalized = NoteUtils.RemoveDiacritics(trimmed).ToLowerInvariant();
        if (normalized == "sin asignacion") return "SA";
        if (normalized == "sin stock") return "SS";
        if (trimmed.Length <= 3) return trimmed;
        return trimmed.Substring(0, 3);
    }
}


