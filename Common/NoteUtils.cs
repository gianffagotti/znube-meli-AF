using System.Globalization;
using System.Text;

namespace meli_znube_integration.Common;

public static class NoteUtils
{
    /// <summary>Current prefix for new notes. Spec 02: [A] (4 chars with space).</summary>
    public const string AutoTag = "[A]";

    public static bool IsAutoNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return false;
        return note!.StartsWith(AutoTag, StringComparison.Ordinal)
            || note.StartsWith("[AUTO]", StringComparison.Ordinal);
    }

    public static bool ContainsAutoNote(IEnumerable<string> notes)
    {
        if (notes == null) return false;
        return notes.Any(IsAutoNote);
    }

    public static string EnsureAutoPrefix(string? text)
    {
        var body = text ?? string.Empty;
        if (IsAutoNote(body))
        {
            if (body.StartsWith(AutoTag + " ", StringComparison.Ordinal)) return body;
            if (body.StartsWith(AutoTag, StringComparison.Ordinal)) return body.Insert(AutoTag.Length, " ");
            if (body.StartsWith("[AUTO] ", StringComparison.Ordinal)) return AutoTag + " " + body.Substring(7);
            if (body.StartsWith("[AUTO]", StringComparison.Ordinal)) return AutoTag + " " + body.Substring(6).TrimStart();
        }
        return AutoTag + " " + body;
    }

    public static long TryParseLong(string? s)
    {
        if (long.TryParse(s, out var v)) return v;
        return 0L;
    }

    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}


