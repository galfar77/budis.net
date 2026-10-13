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

/// <summary>Správce připojení: uložené servery ve stromu se složkami, rychlé připojení dvojklikem.</summary>
public sealed class ConnectionManagerWindow : Window
{
    private readonly AppCore _core;
    private readonly TreeView _tree = new() { Height = 340 };
    private readonly TextBlock _status = new() { Opacity = 0.85, TextWrapping = TextWrapping.Wrap };

    public ConnectionManagerWindow(AppCore core)
    {
        _core = core;
        Title = "Správce připojení";
        Width = 620; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var connect = UiKit.Button("Připojit", async () => await ConnectSelectedAsync(), true);
        var add = UiKit.Button("Nový…", async () => { await new ConnectWindow(_core).ShowDialog(this); Fill(); });
        var edit = UiKit.Button("Upravit…", async () => { if (Selected() is { } s) { await new ConnectWindow(_core, s).ShowDialog(this); Fill(); } });
        var dup = UiKit.Button("Duplikovat", () =>
        {
            if (Selected() is not { } s) return;
            var copy = ServerBook.Duplicate(s);
            _core.Settings.Servers.Add(copy); _core.Settings.Save(); Fill();
        });
        var del = UiKit.Button("Smazat", async () =>
        {
            if (Selected() is not { } s) return;
            if (!await _core.Ui.ConfirmAsync($"Smazat uložený server „{ServerBook.DisplayName(s)}“?", "", "Smazat")) return;
            _core.Settings.Servers.Remove(s); _core.Settings.Save(); Fill();
        });
        _tree.DoubleTapped += async (_, _) => await ConnectSelectedAsync();
        _tree.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await ConnectSelectedAsync(); } };
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Správce připojení", 15, true), UiKit.Frame(_tree), _status,
                UiKit.HStack(8, connect, add, edit, dup, del, UiKit.Button("Zavřít", Close)),
                UiKit.Label("Dvojklik se připojí. Název a skupinu nastavíte v okně Upravit; servery ve skupinách se samy nemažou. Rychlé připojení je i v menu Servery.", dim: true)),
        };
        this.CloseOnEscape();
        Opened += (_, _) => Fill();
    }

    private SavedServer? Selected() => (_tree.SelectedItem as TreeViewItem)?.Tag as SavedServer;

    private void Fill()
    {
        var items = new List<TreeViewItem>();
        foreach (var (group, servers) in ServerBook.Tree(_core.Settings.Servers))
        {
            var nodes = servers.Select(s => new TreeViewItem { Header = ServerBook.DisplayName(s), Tag = s }).ToList();
            if (group.Length == 0) items.AddRange(nodes);
            else items.Add(new TreeViewItem { Header = "📁 " + group, ItemsSource = nodes, IsExpanded = true });
        }
        _tree.ItemsSource = items;
        _status.Text = _core.Settings.Servers.Count == 0 ? Tr.T("Zatím žádný uložený server. Přidejte ho tlačítkem Nový.") : "";
    }

    private async Task ConnectSelectedAsync()
    {
        if (Selected() is not { } s) return;
        var password = AppSettings.Unprotect(s.ProtectedPassword);
        if (password == null && s.User.Length > 0)                       // bez uloženého hesla se zeptá okno připojení
        {
            await new ConnectWindow(_core, s).ShowDialog(this);
            Fill();
            return;
        }
        _status.Text = Tr.T("Připojuji…");
        var ok = await _core.ConnectAsync(s, password ?? "");
        if (ok) Close(); else _status.Text = "";
    }
}
