using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace BudisCommander.Ui;

/// <summary>Malé pomocné funkce pro skládání rozhraní v kódu.</summary>
public static class UiKit
{
    public static Button Button(string text, Action onClick, bool primary = false, double minWidth = 84)
    {
        var b = new Button { Content = text, MinWidth = minWidth, Margin = new Thickness(0, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) b.Classes.Add("accent");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static TextBlock Label(string text, double fontSize = 0, bool bold = false, bool wrap = true, bool dim = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (fontSize > 0) t.FontSize = fontSize;
        if (bold) t.FontWeight = FontWeight.SemiBold;
        if (dim) t.Opacity = 0.7;
        return t;
    }

    public static StackPanel VStack(double spacing = 8, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel HStack(double spacing = 8, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    /// <summary>Řádek tlačítek zarovnaný doprava.</summary>
    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) s.Children.Add(b);
        return s;
    }

    public static Window Dialog(string title, Control content, double width = 460, double? height = null, bool resizable = false)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = height == null ? SizeToContent.Height : SizeToContent.Manual,
            CanResize = resizable,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(16), Child = content },
        };
        if (height != null) w.Height = height.Value;
        return w;
    }

    public static ScrollViewer Scroll(Control content, double height) =>
        new() { Content = content, Height = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };

    public static TextBox Mono(string text, bool readOnly = true) => new()
    {
        Text = text,
        IsReadOnly = readOnly,
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
        FontSize = 12,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
    };

    public static Border Frame(Control child, double thickness = 1) => new()
    {
        BorderThickness = new Thickness(thickness),
        BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
        Child = child,
    };

    public static Window? OwnerOf(Control c) => TopLevel.GetTopLevel(c) as Window;
}
