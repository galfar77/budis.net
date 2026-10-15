using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BudisCommander.Core;

namespace BudisCommander.Ui;

/// <summary>Okno s průběhem kopírování a dalších delších operací; Minimalizovat ho schová do stavového řádku.</summary>
public sealed class ProgressWindow : Window
{
    private readonly ProgressBar _bar = new() { Maximum = 1, Height = 16, MinWidth = 380 };
    private readonly TextBlock _text = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _queue = new() { Opacity = 0.7 };
    private readonly Button _cancel;
    private readonly Button _cancelAll;
    private readonly Action _onMinimize;
    private bool _closing;

    public ProgressWindow(AppCore core, Action onMinimize)
    {
        _onMinimize = onMinimize;
        Title = "Probíhá operace";
        Width = 440; SizeToContent = SizeToContent.Height;
        CanResize = false; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _cancel = UiKit.Button("Zrušit", () => core.CancelJob());
        _cancelAll = UiKit.Button("Zrušit vše", () => core.CancelJob(true));
        var minimize = UiKit.Button("Minimalizovat", Minimize);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(10, _text, _bar, _queue, UiKit.HStack(8, _cancel, _cancelAll, minimize)),
        };
        // křížek znamená minimalizaci, ne zrušení operace
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Minimize(); } };
    }

    private void Minimize() => _onMinimize();

    public void Update(AppCore core)
    {
        _text.Text = core.ProgressText;
        _bar.IsIndeterminate = false;
        _bar.Value = core.Progress ?? 0;
        _queue.Text = core.QueueCount > 0 ? $"ve frontě: {core.QueueCount}" : "";
        _cancel.IsVisible = core.CanCancel;
        _cancelAll.IsVisible = core.CanCancel && core.QueueCount > 0;
    }

    public void CloseForReal() { _closing = true; Close(); }
}
