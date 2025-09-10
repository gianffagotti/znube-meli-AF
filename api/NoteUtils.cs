using System;
using System.Collections.Generic;
using System.Linq;

namespace meli_znube_integration.Api;

public static class NoteUtils
{
    public const string AutoTag = "[AUTO]";

    public static bool IsAutoNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return false;
        return note!.StartsWith(AutoTag, StringComparison.Ordinal);
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
            // Asegura el espacio después del tag para consistencia visual
            return body.StartsWith(AutoTag + " ", StringComparison.Ordinal)
                ? body
                : body.Insert(AutoTag.Length, " ");
        }
        return AutoTag + " " + body;
    }
}


