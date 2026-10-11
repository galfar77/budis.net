using Avalonia.Media;

namespace BudisCommander.Core;

/// <summary>Barevné téma: zvýrazňovací barva (výběr, rámeček aktivního panelu, tlačítka) a barva složek.</summary>
public sealed record ColorThemeDef(string Id, string Title, string Accent, string DirLight, string DirDark)
{
    public Color AccentColor => Color.Parse(Accent);
}

public static class ColorThemes
{
    public static readonly ColorThemeDef[] All =
    {
        new("default", "Výchozí (modrá)", "#2B7CD3", "#1B5EB5", "#7FB5FF"),
        new("ocean", "Oceán", "#0E8FA6", "#0A6E80", "#5FD0E4"),
        new("forest", "Les", "#2E9E5B", "#217344", "#7AD9A0"),
        new("sunset", "Západ slunce", "#E5732B", "#B4531A", "#FFAA6B"),
        new("violet", "Fialová", "#8A55D6", "#6A3DB0", "#C2A0FF"),
        new("graphite", "Grafit", "#6B7683", "#4A5360", "#B3BDC9"),
    };

    public static ColorThemeDef Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
