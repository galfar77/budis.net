using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace BudisCommander.Core;

/// <summary>
/// Překlad textů za běhu. Zdrojové texty jsou česky; při jazyce „en“ se hledá přesná shoda, vzor s vloženými hodnotami
/// ({} v klíči) nebo začátek textu (klíče končící mezerou nebo dvojtečkou). Co se nenajde, zůstane česky.
/// </summary>
public static class Tr
{
    private sealed record Pattern(Regex Rx, string English);

    private static Dictionary<string, string>? _exact;
    private static List<Pattern>? _patterns;
    private static List<(string Prefix, string English)>? _prefixes;
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Language { get; private set; } = "cs";
    public static bool IsEnglish => Language == "en";

    public static void SetLanguage(string? language)
    {
        Language = language == "en" ? "en" : "cs";
        Cache.Clear();
        if (IsEnglish) Load();
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static void Load()
    {
        if (_exact != null) return;
        var exact = new Dictionary<string, string>();
        var patterns = new List<(int Len, Pattern P)>();
        var prefixes = new List<(string, string)>();
        foreach (var line in EnglishStrings.Data.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var cs = Unescape(line[..tab]);
            var en = Unescape(line[(tab + 1)..].TrimEnd('\r'));
            if (cs.Contains("{}"))
            {
                var parts = cs.Split("{}");
                var rx = new Regex("^" + string.Join("(.*?)", parts.Select(Regex.Escape)) + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
                patterns.Add((parts.Sum(p => p.Length), new Pattern(rx, en)));
            }
            else
            {
                exact[cs] = en;
                if (cs.Length >= 4 && (cs.EndsWith(' ') || cs.EndsWith(": "))) prefixes.Add((cs, en));
            }
        }
        _patterns = patterns.OrderByDescending(p => p.Len).Select(p => p.P).ToList();
        _prefixes = prefixes.OrderByDescending(p => p.Item1.Length).ToList();
        _exact = exact;
    }

    /// <summary>Přeloží český text do aktuálního jazyka (při češtině vrací beze změny).</summary>
    public static string T(string cs)
    {
        if (!IsEnglish || string.IsNullOrEmpty(cs)) return cs;
        if (Cache.TryGetValue(cs, out var hit)) return hit;
        var result = Translate(cs);
        if (Cache.Count > 5000) Cache.Clear();
        Cache[cs] = result;
        return result;
    }

    private static string Translate(string cs)
    {
        Load();
        if (_exact!.TryGetValue(cs, out var e)) return e;
        foreach (var p in _patterns!)
        {
            var m = p.Rx.Match(cs);
            if (!m.Success) continue;
            int next = 1;
            return Regex.Replace(p.English, @"\{(\d*)\}", x =>
            {
                int idx = x.Groups[1].Length > 0 ? int.Parse(x.Groups[1].Value) : next++;
                return idx < m.Groups.Count ? m.Groups[idx].Value : x.Value;
            });
        }
        foreach (var (prefix, en) in _prefixes!)
            if (cs.Length > prefix.Length && cs.StartsWith(prefix, StringComparison.Ordinal)) return en + cs[prefix.Length..];
        return cs;
    }

    /// <summary>Zkratka pro texty skládané v kódu: Tr.T při jazyce en, jinak beze změny.</summary>
    public static string F(string cs, params object[] args) => string.Format(T(cs), args);
}
