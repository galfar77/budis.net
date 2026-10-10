using Avalonia;
using Avalonia.Controls;
using BudisCommander.Core;

namespace BudisCommander.Ui;

/// <summary>
/// Překládá texty ovládacích prvků při jejich nastavení (jen v angličtině). Prvky, které zobrazují data uživatele
/// (názvy souborů, obsah souborů), se označí <see cref="NoTranslateProperty"/> a nepřekládají se.
/// </summary>
public static class UiTranslator
{
    public static readonly AttachedProperty<bool> NoTranslateProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("NoTranslate", false, inherits: true);

    public static void SetNoTranslate(Control c, bool value = true) => c.SetValue(NoTranslateProperty, value);
    public static T Raw<T>(this T c) where T : Control { SetNoTranslate(c); return c; }

    private static bool _installed;

    public static void Install()
    {
        if (_installed || !Tr.IsEnglish) return;
        _installed = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((c, e) =>
        {
            if (e.NewValue is not string s || c.GetValue(NoTranslateProperty)) return;
            var t = Tr.T(s);
            if (t != s) c.Text = t;
        });
        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>((c, e) =>
        {
            if (e.NewValue is not string s || c.GetValue(NoTranslateProperty)) return;
            var t = Tr.T(s);
            if (t != s) c.Content = t;
        });
        MenuItem.HeaderProperty.Changed.AddClassHandler<MenuItem>((c, e) =>
        {
            if (e.NewValue is not string s) return;
            var t = Tr.T(s);
            if (t != s) c.Header = t;
        });
        TextBox.WatermarkProperty.Changed.AddClassHandler<TextBox>((c, e) =>
        {
            if (e.NewValue is not string s) return;
            var t = Tr.T(s);
            if (t != s) c.Watermark = t;
        });
        Window.TitleProperty.Changed.AddClassHandler<Window>((c, e) =>
        {
            if (e.NewValue is not string s) return;
            var t = Tr.T(s);
            if (t != s) c.Title = t;
        });
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (e.NewValue is not string s) return;
            var t = Tr.T(s);
            if (t != s) ToolTip.SetTip(c, t);
        });
    }
}
