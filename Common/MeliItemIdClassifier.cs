using System.Text.RegularExpressions;

namespace meli_znube_integration.Common;

public static class MeliItemIdClassifier
{
    private static readonly Regex MlaIdRegex = new(@"^MLA(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsUserProductId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Trim().StartsWith("MLAU", StringComparison.OrdinalIgnoreCase);

    public static bool IsFamilyId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var text = id.Trim();
        if (text.Length < 12) return false;
        foreach (var c in text)
        {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    public static bool IsFamilyContainer(string? id) => IsFamilyId(id);

    public static bool TryNormalizeItemId(string query, out string mlaId)
    {
        if (MlaIdRegex.IsMatch(query))
        {
            mlaId = MeliConstants.ItemIdPrefixMla + MlaIdRegex.Match(query).Groups[1].Value;
            return true;
        }

        if (Regex.IsMatch(query, @"^\d{8,11}$"))
        {
            mlaId = MeliConstants.ItemIdPrefixMla + query;
            return true;
        }

        mlaId = string.Empty;
        return false;
    }

    public static long ParseMlaNumericId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return 0;
        var match = MlaIdRegex.Match(itemId.Trim());
        if (!match.Success) return 0;
        return long.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    public static IReadOnlyList<string> AffectedSourceKeys(string? sourceItemId, string? familyId)
    {
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(sourceItemId))
            keys.Add(sourceItemId.Trim());
        if (!string.IsNullOrWhiteSpace(familyId))
        {
            var family = familyId.Trim();
            if (!keys.Any(k => string.Equals(k, family, StringComparison.OrdinalIgnoreCase)))
                keys.Add(family);
        }
        return keys;
    }
}
