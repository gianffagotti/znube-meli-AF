namespace meli_znube_integration.Api;

public class NoteService
{
    private const int MaxNoteLength = 300;

    private readonly MeliClient _meliClient;
    private readonly ZnubeClient _znubeClient;

    public NoteService(MeliClient meliClient, ZnubeClient znubeClient)
    {
        _meliClient = meliClient;
        _znubeClient = znubeClient;
    }

    public async Task<string?> BuildSingleOrderBodyAsync(MeliOrder order, string accessToken, CancellationToken cancellationToken)
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

        var assignments = await _znubeClient.GetAssignmentsForOrderAsync(order, cancellationToken);
        var lines = new List<string>();
        lines.AddRange(assignments);
        if (!string.IsNullOrWhiteSpace(zone))
        {
            lines.Add($"({zone})");
        }
        var body = string.Join("\n", lines);
        return body;
    }

    public async Task<string?> BuildConsolidatedBodyAsync(IEnumerable<MeliOrder> orders, string accessToken, CancellationToken cancellationToken)
    {
        var orderList = orders?.Where(o => o != null).ToList() ?? new List<MeliOrder>();
        if (orderList.Count == 0) return null;

        // Última orden por fecha o id
        var last = orderList
            .OrderBy(o => o.DateCreatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(o => TryParseLong(o.Id))
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

        // Consolidar líneas de todas las órdenes (únicas, preservando orden de aparición)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var o in orderList)
        {
            var ass = await _znubeClient.GetAssignmentsForOrderAsync(o, cancellationToken);
            foreach (var entry in ass)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                if (seen.Add(entry))
                {
                    lines.Add(entry);
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(zone))
        {
            lines.Add($"({zone})");
        }
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

    private static long TryParseLong(string? s)
    {
        if (long.TryParse(s, out var v)) return v;
        return 0L;
    }
}


