using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using BudisCommander.Core;

namespace BudisCommander.Ui;

/// <summary>Dialogy jádra aplikace postavené na oknech Avalonie.</summary>
public sealed class WindowUi : IUserInterface
{
    private readonly Func<Window> _owner;
    public WindowUi(Func<Window> owner) => _owner = owner;

    /// <summary>Zobrazí dialog s tlačítky a vrátí index stisknutého (Esc = poslední tlačítko).</summary>
    public async Task<int> ButtonsAsync(string title, string message, string info, string[] buttons, bool warning = false)
    {
        int result = buttons.Length - 1;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(UiKit.Label(message, 14, true));
        if (!string.IsNullOrEmpty(info))
        {
            var infoBox = new TextBox { Text = info, IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Avalonia.Media.Brushes.Transparent, MaxHeight = 280 };
            panel.Children.Add(infoBox);
        }
        Window? w = null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = UiKit.Button(buttons[i], () => { result = index; w!.Close(); }, i == 0);
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        w = UiKit.Dialog(title, panel, 480);
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { result = buttons.Length - 1; w.Close(); } };
        await w.ShowDialog(_owner());
        return result;
    }

    public async Task ShowErrorAsync(string message) =>
        await Dispatcher.UIThread.InvokeAsync(() => ButtonsAsync("Budis Commander", "Chyba", message, new[] { "OK" }, true));

    public async Task<bool> ConfirmAsync(string title, string info, string okText) =>
        await Dispatcher.UIThread.InvokeAsync(async () => await ButtonsAsync("Budis Commander", title, info, new[] { okText, "Zrušit" }) == 0);

    public async Task<int> ChooseAsync(string title, string info, string[] buttons) =>
        await Dispatcher.UIThread.InvokeAsync(() => ButtonsAsync("Budis Commander", title, info, buttons));

    public async Task<ConflictChoice> AskConflictAsync(string name)
    {
        var r = await Dispatcher.UIThread.InvokeAsync(() => ButtonsAsync("Budis Commander", $"„{name}“ již v cíli existuje",
            "Chcete existující položku přepsat?", new[] { "Přepsat", "Přepsat vše", "Přeskočit", "Zrušit" }));
        return r switch { 0 => ConflictChoice.Overwrite, 1 => ConflictChoice.OverwriteAll, 2 => ConflictChoice.Skip, _ => ConflictChoice.Cancel };
    }

    private async Task<string?> TextAsync(string title, string info, string initial, string okText, bool password)
    {
        string? result = null;
        var box = new TextBox { Text = initial, MinWidth = 380, PasswordChar = password ? '•' : '\0' };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(UiKit.Label(title, 14, true));
        if (!string.IsNullOrEmpty(info)) panel.Children.Add(UiKit.Label(info, dim: true));
        panel.Children.Add(box);
        Window? w = null;
        void Accept() { result = box.Text ?? ""; w!.Close(); }
        panel.Children.Add(UiKit.ButtonRow(UiKit.Button(okText, Accept, true), UiKit.Button("Zrušit", () => w!.Close())));
        w = UiKit.Dialog("Budis Commander", panel, 480);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Opened += (_, _) =>
        {
            box.Focus();
            if (!password && !string.IsNullOrEmpty(initial))
            {
                // u přejmenování se označí jen název bez přípony
                var dot = initial.LastIndexOf('.');
                box.SelectionStart = 0;
                box.SelectionEnd = dot > 0 ? dot : initial.Length;
            }
        };
        await w.ShowDialog(_owner());
        return result;
    }

    public async Task<string?> PromptAsync(string title, string info, string initial, string okText)
    {
        var r = await Dispatcher.UIThread.InvokeAsync(() => TextAsync(title, info, initial, okText, false));
        return string.IsNullOrWhiteSpace(r) ? null : r;
    }

    public Task<string?> PromptPasswordAsync(string title, string info) =>
        Dispatcher.UIThread.InvokeAsync(() => TextAsync(title, info, "", "OK", true));

    /// <summary>Volá se z vlákna spojení, takže dotaz se zobrazí na vlákně rozhraní a vlákno spojení čeká.</summary>
    public bool TrustHostKey(string host, string fingerprint)
    {
        var task = Dispatcher.UIThread.InvokeAsync(async () =>
            await ButtonsAsync("Budis Commander", $"Důvěřovat serveru {host}?",
                $"Otisk klíče serveru:\n{fingerprint}\n\nPokud tomuto otisku nevěříte (nebo se změnil od posledního připojení), připojení zrušte.",
                new[] { "Důvěřovat", "Zrušit" }) == 0);
        return task.GetAwaiter().GetResult();
    }
}
