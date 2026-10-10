using System.Globalization;

namespace BudisCommander.Core;

public static class Formatting
{
    private static readonly string[] Units = { "B", "kB", "MB", "GB", "TB" };

    /// <summary>Velikost v lidsky čitelném tvaru (1 024 B = 1 kB).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.CurrentCulture) + " B";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + Units[i];
    }

    public static string Date(DateTime? d) => d.HasValue ? d.Value.ToString("g", CultureInfo.CurrentCulture) : "";

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>Shoda názvu s maskou typu *.jpg nebo a?c (bez ohledu na velikost písmen).</summary>
    public static bool MatchesMask(string name, string mask)
    {
        mask = mask.Trim();
        if (mask.Length == 0) mask = "*";
        // více masek oddělených středníkem nebo čárkou
        foreach (var m in mask.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (WildcardMatch(name, m)) return true;
        }
        return false;
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            { t++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star != -1) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>Odstraní diakritiku a převede na malá písmena (pro filtr a rychlé hledání).</summary>
    public static string Fold(string s)
    {
        var norm = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(norm.Length);
        foreach (var c in norm)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().ToLowerInvariant();
    }
}
