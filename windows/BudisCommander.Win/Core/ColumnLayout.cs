namespace BudisCommander.Core;

/// <summary>Pořadí a šířky sloupců seznamu souborů (sloupec Název vyplní zbytek místa).</summary>
public sealed class ColumnLayout
{
    public static readonly string[] All = { "name", "ext", "attr", "size", "date" };
    public const double MinWidth = 30, MaxWidth = 600;

    public List<string> Order { get; set; } = new(All);
    public Dictionary<string, double> Widths { get; set; } = new();

    public static double DefaultWidth(string column) => column switch { "ext" => 60, "attr" => 48, "size" => 92, "date" => 140, _ => 0 };

    public bool IsDefault => Widths.Count == 0 && Normalized().SequenceEqual(All);

    public ColumnLayout Clone() => new() { Order = new List<string>(Order), Widths = new Dictionary<string, double>(Widths) };

    /// <summary>Úplné pořadí všech sloupců (neznámé názvy se zahodí, chybějící se přidají na konec).</summary>
    public List<string> Normalized()
    {
        var list = Order.Where(c => All.Contains(c)).Distinct().ToList();
        foreach (var c in All) if (!list.Contains(c)) list.Add(c);
        return list;
    }

    /// <summary>Sloupce, které se zobrazují, v zadaném pořadí.</summary>
    public List<string> Visible(bool showExt, bool showAttr) =>
        Normalized().Where(c => (c != "ext" || showExt) && (c != "attr" || showAttr)).ToList();

    public double WidthOf(string column) => Widths.TryGetValue(column, out var w) ? Math.Clamp(w, MinWidth, MaxWidth) : DefaultWidth(column);

    /// <summary>Definice sloupců mřížky: 26 pro ikonu, pak sloupce v pořadí (Název je „*“).</summary>
    public string Definition(bool showExt, bool showAttr) =>
        "26," + string.Join(",", Visible(showExt, showAttr).Select(c => c == "name" ? "*" : WidthOf(c).ToString("0", System.Globalization.CultureInfo.InvariantCulture)));

    public void Resize(string column, double width)
    {
        if (column == "name") return;
        Widths[column] = Math.Clamp(Math.Round(width), MinWidth, MaxWidth);
    }

    /// <summary>Posune sloupec o jedno místo doleva (-1) nebo doprava (+1) mezi zobrazenými sloupci.</summary>
    public bool Move(string column, int delta, bool showExt, bool showAttr)
    {
        var order = Normalized();
        var visible = Visible(showExt, showAttr);
        var vi = visible.IndexOf(column);
        var target = vi + delta;
        if (vi < 0 || target < 0 || target >= visible.Count) return false;
        var other = visible[target];
        var a = order.IndexOf(column); var b = order.IndexOf(other);
        (order[a], order[b]) = (order[b], order[a]);
        Order = order;
        return true;
    }
}
