using System.Globalization;
using System.Text;

namespace meli_znube_integration.Core.Domain.Orders;

/// <summary>
/// Pure domain allocation: Depósito-first, then single warehouse or round-robin among multiple.
/// </summary>
public sealed class AllocationDomainService : IAllocationDomainService
{
    public const string SinAsignacion = "Sin asignación";
    public const string SinStock = "Sin stock";

    public List<AllocationResult> Allocate(
        List<ResolvedOrderLine> items,
        Dictionary<string, List<ResourceStock>> stockPerSku)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(stockPerSku);

        var comparer = stockPerSku.Comparer;
        var state = CloneStockState(stockPerSku);
        var results = new List<AllocationResult>();

        foreach (var line in items)
        {
            if (line.Quantity <= 0)
                continue;

            if (!ContainsSkuKey(stockPerSku, line.Sku, comparer))
            {
                results.Add(new AllocationResult
                {
                    ResourceName = SinAsignacion,
                    Sku = line.Sku,
                    AllocatedQuantity = line.Quantity,
                    Label = line.Label,
                });
                continue;
            }

            var skuKey = ResolveKey(state, line.Sku, comparer)
                         ?? ResolveKey(stockPerSku, line.Sku, comparer)
                         ?? line.Sku;

            AllocateLine(state, skuKey, line, results);
        }

        return results;
    }

    private static void AllocateLine(
        Dictionary<string, Dictionary<string, int>> state,
        string skuKey,
        ResolvedOrderLine line,
        List<AllocationResult> results)
    {
        var need = line.Quantity;
        if (!state.TryGetValue(skuKey, out var byResource))
        {
            results.Add(new AllocationResult
            {
                ResourceName = SinAsignacion,
                Sku = line.Sku,
                AllocatedQuantity = need,
                Label = line.Label,
            });
            return;
        }

        var depositoNames = byResource.Keys
            .Where(IsDepositoEligible)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var name in depositoNames)
        {
            if (need <= 0)
                break;
            if (!byResource.TryGetValue(name, out var avail) || avail <= 0)
                continue;

            var take = Math.Min(need, avail);
            byResource[name] = avail - take;
            need -= take;
            results.Add(new AllocationResult
            {
                ResourceName = name,
                Sku = line.Sku,
                AllocatedQuantity = take,
                Label = line.Label,
            });
        }

        if (need <= 0)
            return;

        var nonDepo = byResource
            .Where(kvp => !IsDepositoEligible(kvp.Key) && kvp.Value > 0)
            .Select(kvp => kvp.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (nonDepo.Count == 0)
        {
            if (need > 0)
            {
                results.Add(new AllocationResult
                {
                    ResourceName = SinStock,
                    Sku = line.Sku,
                    AllocatedQuantity = need,
                    Label = line.Label,
                });
            }
            return;
        }

        if (nonDepo.Count == 1)
        {
            var name = nonDepo[0];
            var avail = byResource[name];
            var take = Math.Min(need, avail);
            byResource[name] = avail - take;
            need -= take;
            if (take > 0)
            {
                results.Add(new AllocationResult
                {
                    ResourceName = name,
                    Sku = line.Sku,
                    AllocatedQuantity = take,
                    Label = line.Label,
                });
            }

            if (need > 0)
            {
                results.Add(new AllocationResult
                {
                    ResourceName = SinStock,
                    Sku = line.Sku,
                    AllocatedQuantity = need,
                    Label = line.Label,
                });
            }
            return;
        }

        var cycleOrder = nonDepo;
        var rrIndex = 0;
        while (need > 0)
        {
            var moved = false;
            for (var step = 0; step < cycleOrder.Count; step++)
            {
                var idx = (rrIndex + step) % cycleOrder.Count;
                var name = cycleOrder[idx];
                if (!byResource.TryGetValue(name, out var avail) || avail <= 0)
                    continue;

                byResource[name] = avail - 1;
                need--;
                rrIndex = (idx + 1) % cycleOrder.Count;
                results.Add(new AllocationResult
                {
                    ResourceName = name,
                    Sku = line.Sku,
                    AllocatedQuantity = 1,
                    Label = line.Label,
                });
                moved = true;
                break;
            }

            if (!moved)
            {
                if (need > 0)
                {
                    results.Add(new AllocationResult
                    {
                        ResourceName = SinStock,
                        Sku = line.Sku,
                        AllocatedQuantity = need,
                        Label = line.Label,
                    });
                }
                return;
            }
        }
    }

    private static Dictionary<string, Dictionary<string, int>> CloneStockState(
        Dictionary<string, List<ResourceStock>> stockPerSku)
    {
        var outer = new Dictionary<string, Dictionary<string, int>>(stockPerSku.Comparer);
        foreach (var kvp in stockPerSku)
        {
            var inner = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var rs in kvp.Value)
            {
                if (string.IsNullOrWhiteSpace(rs.ResourceName))
                    continue;
                inner[rs.ResourceName] = inner.GetValueOrDefault(rs.ResourceName) + Math.Max(0, rs.AvailableQuantity);
            }

            outer[kvp.Key] = inner;
        }

        return outer;
    }

    private static bool ContainsSkuKey(
        Dictionary<string, List<ResourceStock>> stockPerSku,
        string sku,
        IEqualityComparer<string>? comparer)
    {
        if (stockPerSku.ContainsKey(sku))
            return true;
        foreach (var key in stockPerSku.Keys)
        {
            if (comparer != null && comparer.Equals(key, sku))
                return true;
        }
        return false;
    }

    private static string? ResolveKey(
        Dictionary<string, Dictionary<string, int>> state,
        string sku,
        IEqualityComparer<string>? comparer)
    {
        if (state.ContainsKey(sku))
            return sku;
        foreach (var key in state.Keys)
        {
            if (comparer != null && comparer.Equals(key, sku))
                return key;
        }

        return null;
    }

    private static string? ResolveKey(
        Dictionary<string, List<ResourceStock>> stockPerSku,
        string sku,
        IEqualityComparer<string>? comparer)
    {
        if (stockPerSku.ContainsKey(sku))
            return sku;
        foreach (var key in stockPerSku.Keys)
        {
            if (comparer != null && comparer.Equals(key, sku))
                return key;
        }

        return null;
    }

    public static bool IsDepositoEligible(string? resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
            return false;
        var normalized = RemoveDiacritics(resourceName.Trim()).ToLowerInvariant();
        return normalized.Contains("deposito", StringComparison.Ordinal);
    }

    private static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
