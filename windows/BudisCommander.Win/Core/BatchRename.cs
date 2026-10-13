using System.Text.RegularExpressions;

namespace BudisCommander.Core;

public sealed class RenameOptions
{
    public string Mask { get; set; } = "[N].[E]";
    public string Search { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool Regex { get; set; }
    public int Start { get; set; } = 1;
    public int Digits { get; set; } = 1;
    /// <summary>0 beze změny, 1 malá, 2 velká</summary>
    public int CaseMode { get; set; }
    /// <summary>Odstranit diakritiku (á → a).</summary>
    public bool RemoveDiacritics { get; set; }
    /// <summary>Mezery nahradit podtržítkem.</summary>
    public bool SpacesToUnderscore { get; set; }
}

/// <summary>Položka k přejmenování včetně údajů pro značky [Y], [M], [D], [h], [m], [s] (datum změny) a [P] (název nadřazené složky).</summary>
public sealed record RenameItem(string Name, bool IsDirectory, DateTime? Modified = null, string Parent = "");

public static class BatchRename
{
    public static List<string> NewNames(IReadOnlyList<(string Name, bool IsDirectory)> items, RenameOptions o) =>
        NewNames(items.Select(i => new RenameItem(i.Name, i.IsDirectory)).ToList(), o);

    /// <summary>Odstraní diakritiku (včetně písmen jako ł a đ, která se rozkladem neodstraní).</summary>
    public static string StripDiacritics(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch { 'ł' => 'l', 'Ł' => 'L', 'đ' => 'd', 'Đ' => 'D', 'ø' => 'o', 'Ø' => 'O', 'ß' => 's', _ => ch });
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    public static List<string> NewNames(IReadOnlyList<RenameItem> items, RenameOptions o)
    {
        Regex? regex = null;
        if (o.Regex && o.Search.Length > 0)
        {
            try { regex = new Regex(o.Search, RegexOptions.CultureInvariant); } catch { regex = null; }
        }
        var range = new Regex(@"\[N(\d+)-(\d+)\]", RegexOptions.CultureInvariant);
        var result = new List<string>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var name = item.Name; var isDir = item.IsDirectory;
            var ext = isDir ? "" : Path.GetExtension(name).TrimStart('.');
            var baseName = isDir ? name : Path.GetFileNameWithoutExtension(name);
            var counter = (o.Start + i).ToString(new string('0', Math.Max(o.Digits, 1)));

            var s = o.Mask;
            if (ext.Length == 0) s = s.Replace(".[E]", "").Replace("[E]", "");
            else s = s.Replace("[E]", ext);
            s = range.Replace(s, m =>
            {
                int from = int.Parse(m.Groups[1].Value), to = int.Parse(m.Groups[2].Value);
                if (from < 1) from = 1;
                if (from > baseName.Length || to < from) return "";
                return baseName.Substring(from - 1, Math.Min(to, baseName.Length) - from + 1);
            });
            s = s.Replace("[C]", counter);
            s = s.Replace("[N]", baseName);
            var mod = item.Modified;
            s = s.Replace("[Y]", mod?.ToString("yyyy") ?? "").Replace("[M]", mod?.ToString("MM") ?? "").Replace("[D]", mod?.ToString("dd") ?? "")
                 .Replace("[h]", mod?.ToString("HH") ?? "").Replace("[m]", mod?.ToString("mm") ?? "").Replace("[s]", mod?.ToString("ss") ?? "");
            s = s.Replace("[P]", item.Parent);

            if (o.Search.Length > 0)
            {
                if (regex != null) s = regex.Replace(s, o.Replace.Replace("$", "$"));
                else if (!o.Regex) s = s.Replace(o.Search, o.Replace, StringComparison.Ordinal);
            }
            s = o.CaseMode switch { 1 => s.ToLowerInvariant(), 2 => s.ToUpperInvariant(), _ => s };
            if (o.RemoveDiacritics) s = StripDiacritics(s);
            if (o.SpacesToUnderscore) s = s.Replace(' ', '_');
            foreach (var bad in Path.GetInvalidFileNameChars()) s = s.Replace(bad, '-');
            result.Add(s.Trim());
        }
        return result;
    }
}
