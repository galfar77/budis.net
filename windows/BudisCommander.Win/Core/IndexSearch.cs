using System.Text;

namespace BudisCommander.Core;

/// <summary>Rychlé hledání přes index Windows Search (stejný jako v Průzkumníku); funguje jen pro indexované složky.</summary>
public static class IndexSearch
{
    private static string Quote(string s) => s.Replace("'", "''");

    /// <summary>Převede masku (* a ?, více masek oddělených středníkem) na podmínku LIKE nad názvem souboru.</summary>
    public static string NameCondition(string mask)
    {
        var parts = (string.IsNullOrWhiteSpace(mask) ? "*" : mask).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var conds = new List<string>();
        foreach (var p in parts)
        {
            var pattern = p;
            if (!pattern.Contains('*') && !pattern.Contains('?')) pattern = "*" + pattern + "*";
            var sb = new StringBuilder();
            foreach (var c in pattern)
                sb.Append(c switch { '*' => "%", '?' => "_", '%' => "", _ => c.ToString() });
            conds.Add($"System.FileName LIKE '{Quote(sb.ToString())}'");
        }
        return conds.Count == 1 ? conds[0] : "(" + string.Join(" OR ", conds) + ")";
    }

    public static string BuildQuery(string root, string mask, string text, int limit)
    {
        var sb = new StringBuilder($"SELECT TOP {limit} System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:{Quote(root.TrimEnd('\\', '/'))}' AND ");
        sb.Append(NameCondition(mask));
        if (!string.IsNullOrWhiteSpace(text))
            sb.Append($" AND CONTAINS('\"{Quote(System.Text.RegularExpressions.Regex.Replace(text.Replace("\"", " "), @"\s+", " ").Trim())}\"')");
        return sb.ToString();
    }

    /// <summary>Spustí dotaz nad indexem přes COM (ADODB); při nedostupném indexu vyhodí výjimku.</summary>
    public static List<string> Query(string sql, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Index Windows Search existuje jen ve Windows.");
        var type = Type.GetTypeFromProgID("ADODB.Connection") ?? throw new InvalidOperationException("ADODB není k dispozici.");
        dynamic conn = Activator.CreateInstance(type)!;
        var results = new List<string>();
        try
        {
            conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            dynamic rs = conn.Execute(sql);
            try
            {
                while (!(bool)rs.EOF)
                {
                    ct.ThrowIfCancellationRequested();
                    var v = rs.Fields.Item(0).Value;
                    if (v is string s && s.Length > 0) results.Add(s);
                    rs.MoveNext();
                }
            }
            finally { try { rs.Close(); } catch { } }
        }
        finally { try { conn.Close(); } catch { } }
        return results;
    }

    public static Task<List<string>> SearchAsync(string root, string mask, string text, int limit, CancellationToken ct) =>
        Task.Run(() => Query(BuildQuery(root, mask, text, limit), ct), ct);
}
