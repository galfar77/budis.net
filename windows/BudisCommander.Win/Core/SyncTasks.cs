namespace BudisCommander.Core;

/// <summary>Úloha synchronizace na pozadí: složka se jednosměrně kopíruje do cíle (složka nebo cloud přes rclone).</summary>
public sealed class SyncTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>Místní složka nebo „rclone:název:/cesta“.</summary>
    public string Target { get; set; } = "";
    /// <summary>0 = jen ručně.</summary>
    public int IntervalMinutes { get; set; }
    /// <summary>Mazat v cíli soubory, které ve zdroji nejsou (jinak se jen přidává a přepisuje).</summary>
    public bool DeleteExtra { get; set; }
    public DateTime? LastRun { get; set; }
    public string LastResult { get; set; } = "";

    public bool IsDue(DateTime now) => IntervalMinutes > 0 && (LastRun == null || (now - LastRun.Value).TotalMinutes >= IntervalMinutes);

    /// <summary>Rozebere cíl typu „rclone:název:/cesta“; jinak vrací null (místní složka).</summary>
    public static (string Remote, string Path)? ParseRclone(string target)
    {
        if (!target.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = target["rclone:".Length..];
        var i = rest.IndexOf(':');
        if (i <= 0) return null;
        var path = rest[(i + 1)..];
        return (rest[..i], path.Length == 0 ? "/" : (path.StartsWith('/') ? path : "/" + path));
    }

    public string Describe() => $"{Name}   {Source} → {Target}   ({(IntervalMinutes > 0 ? $"každých {IntervalMinutes} min" : "ručně")})";
}
