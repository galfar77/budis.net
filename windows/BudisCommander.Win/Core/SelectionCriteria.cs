using System.Globalization;

namespace BudisCommander.Core;

/// <summary>Podmínky pro rozšířené označování: maska názvu, druh položky, velikost a datum změny.</summary>
public sealed class SelectionCriteria
{
    public string Mask { get; set; } = "*";
    /// <summary>0 vše, 1 jen soubory, 2 jen složky.</summary>
    public int Kind { get; set; }
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public DateTime? ModifiedFrom { get; set; }
    /// <summary>Poslední den včetně (do konce dne).</summary>
    public DateTime? ModifiedTo { get; set; }
    /// <summary>Jen položky, které jsou už označené, nebo jen neoznačené (null = bez ohledu).</summary>
    public bool? Marked { get; set; }

    public bool Matches(string name, bool isDirectory, long size, DateTime? modified)
    {
        if (Kind == 1 && isDirectory) return false;
        if (Kind == 2 && !isDirectory) return false;
        if (!Formatting.MatchesMask(name, Mask)) return false;
        if (MinSize != null || MaxSize != null)
        {
            if (isDirectory) return false;
            if (MinSize != null && size < MinSize) return false;
            if (MaxSize != null && size > MaxSize) return false;
        }
        if (ModifiedFrom != null || ModifiedTo != null)
        {
            if (modified == null) return false;
            if (ModifiedFrom != null && modified.Value < ModifiedFrom.Value.Date) return false;
            if (ModifiedTo != null && modified.Value >= ModifiedTo.Value.Date.AddDays(1)) return false;
        }
        return true;
    }

    /// <summary>Velikost jako „500“, „10 KB“, „1,5 MB“, „2 GB“ (násobky 1024). Prázdný text = žádná hranice.</summary>
    public static bool TryParseSize(string? text, out long? bytes)
    {
        bytes = null;
        text = (text ?? "").Trim().Replace(" ", "");
        if (text.Length == 0) return true;
        var mult = 1L;
        foreach (var (suffix, m) in new[] { ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("G", 1L << 30), ("M", 1L << 20), ("K", 1L << 10), ("B", 1L) })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { mult = m; text = text[..^suffix.Length]; break; }
        }
        text = text.Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
        bytes = (long)(v * mult);
        return true;
    }

    /// <summary>Datum jako 2025-03-31, 31.3.2025 nebo 31. 3. 2025; prázdný text = žádná hranice.</summary>
    public static bool TryParseDate(string? text, out DateTime? date)
    {
        date = null;
        text = (text ?? "").Trim();
        if (text.Length == 0) return true;
        var formats = new[] { "yyyy-MM-dd", "d.M.yyyy", "d. M. yyyy", "dd.MM.yyyy" };
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { date = d; return true; }
        return false;
    }
}
