using System.Text.RegularExpressions;
using meli_znube_integration.Models;

namespace meli_znube_integration.Common;

public static class ZnubeLogicExtensions
{
    /// <summary>
    /// Normalizes a seller SKU to the format expected by the Znube API.
    /// </summary>
    public static string NormalizeSellerSku(string? sellerSku)
    {
        if (string.IsNullOrWhiteSpace(sellerSku)) return sellerSku ?? string.Empty;
        var s = sellerSku.Trim();
        if (s.Contains("#")) return s;

        if (s.Contains("!"))
        {
            var replaced = s.Replace('!', '#');
            while (replaced.Contains("##"))
                replaced = replaced.Replace("##", "#");
            return replaced;
        }

        if (s.IndexOf("HST", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var parts = Regex.Split(s, "HST", RegexOptions.IgnoreCase)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            if (parts.Length >= 2)
                return string.Join('#', parts);
        }

        return s;
    }

    /// <summary>
    /// Returns true if the resource name represents "Depósito" (diacritic-insensitive).
    /// </summary>
    public static bool IsDepositoName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = NoteUtils.RemoveDiacritics(name).Trim().ToLowerInvariant();
        return normalized == "deposito";
    }

    public static Dictionary<string, (string Name, double TotalStock)> BuildResourcesMap(OmnichannelData data)
    {
        var resources = new Dictionary<string, (string Name, double TotalStock)>(StringComparer.OrdinalIgnoreCase);
        if (data?.Resources == null) return resources;
        foreach (var r in data.Resources)
            resources[r.ResourceId] = (r.Name ?? string.Empty, r.TotalStock ?? 0);
        return resources;
    }

    public static HashSet<string> BuildResourceIdsWithQty(OmnichannelData data)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data?.Stock == null) return set;
        foreach (var s in data.Stock)
        {
            foreach (var st in s.Stock ?? [])
            {
                if (st.Quantity > 0.0 && !string.IsNullOrWhiteSpace(st.ResourceId))
                    set.Add(st.ResourceId);
            }
        }
        return set;
    }

    public static string? TryGetProductId(OmnichannelData data)
    {
        if (data?.Stock == null) return null;
        foreach (var s in data.Stock)
        {
            if (!string.IsNullOrWhiteSpace(s.ProductId))
                return s.ProductId;
        }
        return null;
    }

    public static string? BuildTitleFromZnube(OmnichannelData? data, string? sellerSku)
    {
        if (data == null || string.IsNullOrWhiteSpace(sellerSku)) return null;

        OmnichannelStockItem? target = null;
        foreach (var s in data.Stock ?? [])
        {
            if (!string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, sellerSku, StringComparison.OrdinalIgnoreCase))
            {
                target = s;
                break;
            }
        }
        if (target == null) return null;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(target.ProductId))
            parts.Add(target.ProductId!);

        foreach (var v in target.Variants ?? [])
        {
            string? label = null;
            foreach (var vt in data.Variants ?? [])
            {
                if (!string.IsNullOrWhiteSpace(vt.TypeName) && string.Equals(vt.TypeName, v.VariantType, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(vt.TypeName, "TALLE", StringComparison.OrdinalIgnoreCase))
                        label = v.VariantId;
                    else if (!string.IsNullOrWhiteSpace(v.VariantId) && vt.Names != null && vt.Names.TryGetValue(v.VariantId, out var nameVal))
                        label = nameVal;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(label))
                label = v.VariantId;
            if (!string.IsNullOrWhiteSpace(label))
                parts.Add(label!);
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// Resolves assignment when response is by SKU: Depósito first, then single non-Depósito, else null.
    /// </summary>
    public static string? ResolveAssignmentFromSku(
        Dictionary<string, (string Name, double TotalStock)> resources,
        HashSet<string> resourceIdsWithQty)
    {
        string? depositoId = null;
        foreach (var kvp in resources)
        {
            if (IsDepositoName(kvp.Value.Name))
            {
                depositoId = kvp.Key;
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(depositoId) && resourceIdsWithQty.Contains(depositoId))
            return resources.TryGetValue(depositoId!, out var info2) ? info2.Name : "Deposito";

        string? unicoId = null;
        int count = 0;
        foreach (var id in resourceIdsWithQty)
        {
            if (!string.IsNullOrWhiteSpace(depositoId) && string.Equals(id, depositoId, StringComparison.OrdinalIgnoreCase))
                continue;
            count++;
            if (count == 1) unicoId = id;
        }

        if (count == 1 && !string.IsNullOrWhiteSpace(unicoId) && resources.TryGetValue(unicoId!, out var info))
            return info.Name;

        return null;
    }

    /// <summary>
    /// Resolves assignment when response is by ProductId: pick resource with maximum TotalStock among allowed.
    /// </summary>
    public static string? ResolveAssignmentFromProduct(
        Dictionary<string, (string Name, double TotalStock)> resources,
        HashSet<string> allowedResourceIds)
    {
        string? bestId = null;
        double bestQty = double.MinValue;
        foreach (var id in allowedResourceIds)
        {
            if (resources.TryGetValue(id, out var info) && info.TotalStock > bestQty)
            {
                bestQty = info.TotalStock;
                bestId = id;
            }
        }
        return !string.IsNullOrWhiteSpace(bestId) && resources.TryGetValue(bestId!, out var best) ? best.Name : null;
    }
}
