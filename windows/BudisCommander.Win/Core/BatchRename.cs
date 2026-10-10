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
}

public static class BatchRename
{
    public static List<string> NewNames(IReadOnlyList<(string Name, bool IsDirectory)> items, RenameOptions o)
    {
        Regex? regex = null;
        if (o.Regex && o.Search.Length > 0)
        {
            try { regex = new Regex(o.Search, RegexOptions.CultureInvariant); } catch { regex = null; }
        }
        var result = new List<string>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var (name, isDir) = items[i];
            var ext = isDir ? "" : Path.GetExtension(name).TrimStart('.');
            var baseName = isDir ? name : Path.GetFileNameWithoutExtension(name);
            var counter = (o.Start + i).ToString(new string('0', Math.Max(o.Digits, 1)));

            var s = o.Mask;
            if (ext.Length == 0) s = s.Replace(".[E]", "").Replace("[E]", "");
            else s = s.Replace("[E]", ext);
            s = s.Replace("[C]", counter);
            s = s.Replace("[N]", baseName);

            if (o.Search.Length > 0)
            {
                if (regex != null) s = regex.Replace(s, o.Replace.Replace("$", "$"));
                else if (!o.Regex) s = s.Replace(o.Search, o.Replace, StringComparison.Ordinal);
            }
            s = o.CaseMode switch { 1 => s.ToLowerInvariant(), 2 => s.ToUpperInvariant(), _ => s };
            foreach (var bad in Path.GetInvalidFileNameChars()) s = s.Replace(bad, '-');
            result.Add(s.Trim());
        }
        return result;
    }
}
