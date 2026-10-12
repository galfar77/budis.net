using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using BudisCommander.Core;

namespace BudisCommander.Ui;

/// <summary>Historie provedených operací: kopírování jde zopakovat, přesuny, přejmenování a mazání vrátit.</summary>
public sealed class HistoryWindow : Window
{
    private readonly AppCore _core;
    private readonly ListBox _list = new() { Height = 340 };
    private readonly TextBlock _status = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private List<OpEntry> _shown = new();
    private readonly Action _onChanged;

    public HistoryWindow(AppCore core)
    {
        _core = core;
        Title = "Historie operací";
        Width = 760; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var repeat = UiKit.Button("Zopakovat", async () => await RepeatAsync());
        var revert = UiKit.Button("Vrátit zpět", async () => await RevertAsync());
        var clear = UiKit.Button("Vyčistit historii", () => _core.History.Clear());
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Historie operací", 15, true), UiKit.Frame(_list), _status,
                UiKit.HStack(8, repeat, revert, clear, UiKit.Button("Zavřít", Close, true)),
                UiKit.Label("Zopakovat jde kopírování. Vrátit jde kopírování (kopie jde do Koše), přesun, přejmenování, nová složka a smazání do Koše.", dim: true)),
        };
        _onChanged = () => Dispatcher.UIThread.Post(Fill);
        core.History.Changed += _onChanged;
        Closed += (_, _) => core.History.Changed -= _onChanged;
        this.CloseOnEscape();
        Opened += (_, _) => Fill();
    }

    private OpEntry? Selected() => _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count ? _shown[_list.SelectedIndex] : null;

    public static string Line(OpEntry e)
    {
        var mark = e.Reverted ? "↩" : e.Kind switch { OpKind.Copy => "⧉", OpKind.Move => "➜", OpKind.Delete => "🗑", OpKind.Rename => "✎", _ => "＋" };
        return $"{mark} {e.Time:d. M. HH:mm}  {Tr.T(e.Summary)}" + (e.Reverted ? "  (" + Tr.T("vráceno") + ")" : "");
    }

    private void Fill()
    {
        var sel = _list.SelectedIndex;
        _shown = _core.History.Entries.ToList();
        _list.ItemsSource = _shown.Select(Line).ToList();
        if (sel >= 0 && sel < _shown.Count) _list.SelectedIndex = sel;
    }

    private async Task RepeatAsync()
    {
        if (Selected() is not { } e) return;
        if (!OperationLog.CanRepeat(e)) { _status.Text = Tr.T("Tuto operaci nejde zopakovat (zopakovat jde jen kopírování)."); return; }
        try
        {
            var n = await OperationLog.RepeatAsync(e);
            _status.Text = Tr.T($"Zkopírováno znovu: {n}");
            await ReloadPanes();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task RevertAsync()
    {
        if (Selected() is not { } e) return;
        if (!OperationLog.CanRevert(e)) { _status.Text = Tr.T(e.Reverted ? "Tuto operaci už někdo vrátil." : "Tuto operaci nejde vrátit (trvalé smazání)."); return; }
        var (done, errors) = await Task.Run(() => OperationLog.Revert(e, p => RecycleBin.RestoreByOriginalPath(p)));
        if (errors.Count == 0) _core.History.MarkReverted(e.Id);
        _status.Text = Tr.T($"Vráceno položek: {done}") + (errors.Count > 0 ? "\n" + string.Join("\n", errors) : "");
        await ReloadPanes();
    }

    private async Task ReloadPanes()
    {
        await _core.Left.Current.ReloadAsync(true);
        await _core.Right.Current.ReloadAsync(true);
    }
}

/// <summary>Koš Windows: seznam smazaných položek s původním umístěním a obnovení na původní místo.</summary>
public sealed class RecycleBinWindow : Window
{
    private readonly AppCore _core;
    private readonly ListBox _list = new() { Height = 340, SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _status = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private List<RecycledItem> _items = new();

    public RecycleBinWindow(AppCore core)
    {
        _core = core;
        Title = "Koš";
        Width = 760; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var restore = UiKit.Button("Obnovit vybrané", async () => await RestoreAsync(), true);
        var refresh = UiKit.Button("Obnovit seznam", Fill);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Koš", 15, true), UiKit.Frame(_list), _status,
                UiKit.HStack(8, restore, refresh, UiKit.Button("Zavřít", Close)),
                UiKit.Label("Položky se vrátí na původní místo (kdyby tam už něco bylo, přidá se k názvu číslo). Označit víc položek jde s Ctrl nebo Shift.", dim: true)),
        };
        this.CloseOnEscape();
        Opened += (_, _) => Fill();
    }

    public static string Line(RecycledItem i) =>
        $"{(i.IsDirectory ? "📁" : "📄")} {i.Name}   –   {i.OriginalPath}   ({i.Deleted:d. M. yyyy HH:mm}" + (i.IsDirectory ? "" : ", " + Formatting.Size(i.Size)) + ")";

    private void Fill()
    {
        _items = RecycleBin.List();
        _list.ItemsSource = _items.Select(Line).ToList();
        _status.Text = _items.Count == 0 ? Tr.T(OperatingSystem.IsWindows() ? "Koš je prázdný." : "Koš nelze číst (jen ve Windows).") : "";
    }

    private async Task RestoreAsync()
    {
        var selected = _list.SelectedItems?.Cast<string>().ToList() ?? new();
        var picks = selected.Select(s => _list.ItemsSource!.Cast<string>().ToList().IndexOf(s)).Where(i => i >= 0).Select(i => _items[i]).ToList();
        if (picks.Count == 0) { _status.Text = Tr.T("Vyberte položky k obnovení."); return; }
        var errors = new List<string>(); var done = 0;
        foreach (var item in picks)
        {
            try { await Task.Run(() => RecycleBin.Restore(item)); done++; }
            catch (Exception ex) { errors.Add($"{item.Name}: {ex.Message}"); }
        }
        Fill();
        _status.Text = Tr.T($"Obnoveno položek: {done}") + (errors.Count > 0 ? "\n" + string.Join("\n", errors) : "");
        await _core.Left.Current.ReloadAsync(true);
        await _core.Right.Current.ReloadAsync(true);
    }
}
