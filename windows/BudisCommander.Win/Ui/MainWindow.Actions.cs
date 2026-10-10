using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BudisCommander.Core;
using BudisCommander.Remote;

namespace BudisCommander.Ui;

public sealed partial class MainWindow
{
    private HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);

    public async Task Run(string id)
    {
        if (_handlers.TryGetValue(id, out var h))
        {
            try { await h(); }
            catch (Exception e) { await _ui.ShowErrorAsync(e.Message); }
        }
        FocusActiveList();
    }

    private PaneState Pane => _core.Active;

    private void RegisterHandlers()
    {
        void H(string id, Func<Task> f) => _handlers[id] = f;
        void S(string id, Action f) => _handlers[id] = () => { f(); return Task.CompletedTask; };

        H("view", ViewCurrentAsync);
        H("edit", () => _core.EditAsync());
        H("copy", () => _core.StartTransferAsync(false));
        H("move", () => _core.StartTransferAsync(true));
        H("mkdir", () => _core.MakeDirectoryAsync());
        H("delete", () => _core.StartDeleteAsync(false));
        H("deleteAlt", () => _core.StartDeleteAsync(false));
        H("deletePermanent", () => _core.StartDeleteAsync(true));
        H("rename", () => _core.RenameCurrentAsync());
        H("renameAlt", () => _core.RenameCurrentAsync());
        H("attributes", ShowAttributesAsync);
        H("symlink", () => _core.MakeSymlinkAsync());
        H("pack", () => _core.PackAsync());
        H("unpack", () => _core.UnpackAsync());
        H("addToArchive", () => _core.AddToArchiveAsync());
        H("split", () => _core.StartSplitAsync());
        H("combine", () => _core.StartCombineAsync());
        H("checksum", ShowChecksumAsync);
        H("diff", ShowDiffAsync);
        H("undo", () => _core.UndoAsync());
        S("quit", Close);

        S("markAll", () => Pane.MarkAll());
        S("unmarkAll", () => Pane.UnmarkAll());
        S("invertMarks", () => Pane.InvertMarks());
        H("markMask", () => MarkByMaskAsync(true));
        H("unmarkMask", () => MarkByMaskAsync(false));
        H("compare", () => _core.CompareAsync(false));
        H("compareContent", () => _core.CompareAsync(true));
        H("sync", () => _core.StartSyncAsync());
        H("copyFiles", () => CopyToClipboardAsync(false));
        H("cutFiles", () => CopyToClipboardAsync(true));
        H("pasteFiles", PasteFromClipboardAsync);
        H("copyPath", () => CopyTextAsync(string.Join("\n", Pane.Targets().Select(t => t.FullPath))));
        H("copyName", () => CopyTextAsync(string.Join("\n", Pane.Targets().Select(t => t.Name))));
        H("copyDirPath", () => CopyTextAsync(Pane.IsRemote ? Pane.Title : Pane.PersistentPath));

        H("search", () => ShowToolAsync(new SearchWindow(_core)));
        H("duplicates", () => ShowToolAsync(new DuplicatesWindow(_core)));
        H("batchRename", ShowBatchRenameAsync);
        S("commandLine", () => SetCommandVisible(!_commandHost.IsVisible));
        H("dirSizes", () => _core.CalcDirSizesAsync());
        H("userMenu", async () => { await ShowToolAsync(new UserMenuWindow(_settings)); RebuildUserBar(); });
        S("resumeTransfer", () => _core.ResumeTransfer());
        H("shortcuts", async () =>
        {
            var exe = Updater.SelfExePath() ?? Environment.ProcessPath;
            if (exe == null || !OperatingSystem.IsWindows()) { await _ui.ShowErrorAsync("Zástupce lze vytvořit jen ve Windows ze spuštěného BudisCommander.exe."); return; }
            if (!await _ui.ConfirmAsync("Vytvořit zástupce?", $"Zástupce na ploše a v nabídce Start bude mířit na:\n{exe}\n\nUmístěte aplikaci nejdřív na trvalé místo (např. C:\\Programy\\BudisCommander).", "Vytvořit")) return;
            Shortcuts.CreateAll(exe);
            _core.ShowNotice("Zástupci vytvořeni na ploše a v nabídce Start.");
        });
        H("whatsNew", () => ShowToolAsync(new InfoWindow("Co je nového", InfoTexts.News)));
        H("features", () => ShowToolAsync(new InfoWindow("Přehled funkcí", InfoTexts.Features)));
        H("about", async () =>
        {
            var commit = Updater.CurrentCommit;
            var build = commit.Length > 0 ? Tr.T($"Sestavení z commitu: {commit[..Math.Min(commit.Length, 12)]}") : Tr.T("Vývojové sestavení (bez označení verze)");
            var header = "Budis Commander\n" + Tr.T("Dvoupanelový správce souborů") + "\n" + build + "\n" + Tr.T($"Projekt: {"https://github.com/" + Updater.Repo}");
            await ShowToolAsync(new InfoWindow("O aplikaci", new[] { "# Nové" }.Concat(InfoTexts.News.Skip(1).Take(5)), header, () => _ = Run("update")));
        });
        H("update", async () =>
        {
            if (!await _core.UpdateAsync()) return;
            if (!await _ui.ConfirmAsync("Aktualizace nainstalována", "Aplikace se teď restartuje.", "Restartovat")) return;
            var exe = Updater.SelfExePath();
            if (exe != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Close();
        });
        H("settings", async () => { await ShowToolAsync(new SettingsWindow(_settings)); ApplySettings(); _settings.Save(); });

        H("refresh", () => Pane.ReloadAsync());
        H("hidden", () => Pane.ToggleHiddenAsync());
        H("branch", () => Pane.ToggleBranchAsync());
        S("quickView", ToggleQuickView);
        S("filter", () => ActivePanel.ShowFilter());
        H("favorites", () => ShowToolAsync(new FavoritesWindow(_core)));
        H("back", () => Pane.GoBackAsync());
        H("forward", () => Pane.GoForwardAsync());
        H("mirror", async () =>
        {
            if (!_core.Other.IsRemote) await _core.Other.NavigateAsync(Pane.PersistentPath);
        });
        H("swapPanels", async () =>
        {
            var a = _core.Left.Current; var b = _core.Right.Current;
            if (a.IsRemote || b.IsRemote) return;
            var pa = a.PersistentPath; var pb = b.PersistentPath;
            await a.NavigateAsync(pb); await b.NavigateAsync(pa);
        });
        S("newTab", () => _core.ActiveGroup.NewTab());
        S("closeTab", () => _core.ActiveGroup.Close(_core.ActiveGroup.Selected));
        S("nextTab", () => _core.ActiveGroup.Cycle(1));
        S("prevTab", () => _core.ActiveGroup.Cycle(-1));
        H("tabSets", () => ShowToolAsync(new TabSetsWindow(_core)));
        H("toggleTheme", async () =>
        {
            _settings.Theme = _settings.Theme switch { "system" => "light", "light" => "dark", _ => "system" };
            _settings.Save();
            ApplyTheme();
            foreach (var t in _core.Left.Tabs.Concat(_core.Right.Tabs)) t.RaiseStateChangedForUi();
            _core.ShowNotice("Vzhled: " + _settings.Theme switch { "light" => "světlý", "dark" => "tmavý", _ => "podle systému" });
            await Task.CompletedTask;
        });
        S("typeAll", () => Pane.SetTypeFilter(null));
        S("typeImages", () => Pane.SetTypeFilter("images"));
        S("typeDocs", () => Pane.SetTypeFilter("docs"));
        S("typeAudio", () => Pane.SetTypeFilter("audio"));
        S("typeVideo", () => Pane.SetTypeFilter("video"));
        S("typeArchives", () => Pane.SetTypeFilter("archives"));
        H("sortType", () => Pane.SetSortAsync(SortKey.Ext));
        S("focusPath", () => ActivePanel.FocusPathBox());

        H("connect", () => ShowToolAsync(new ConnectWindow(_core)));
        H("cloud", () => ShowToolAsync(new CloudWindow(_core)));
        H("network", () => ShowToolAsync(new NetworkWindow(_core)));
    }

    private async Task ShowToolAsync(Window w)
    {
        await w.ShowDialog(this);
    }

    // --- klávesnice ----------------------------------------------------------------------------------

    private async void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        bool inText = IsTextFocused();
        bool functionKey = e.Key is >= Key.F1 and <= Key.F12;

        // 1) zkratky akcí
        if (!inText || functionKey)
        {
            foreach (var a in Actions.All)
            {
                var g = Actions.GestureFor(a, _settings);
                if (g == null || !g.Matches(e)) continue;
                e.Handled = true;
                await Run(a.Id);
                return;
            }
        }
        if (inText) return;

        var pane = _core.Active;
        var mods = e.KeyModifiers;
        bool plain = mods == KeyModifiers.None;
        bool shift = mods == KeyModifiers.Shift;

        switch (e.Key)
        {
            case Key.Tab when plain:
                SetActive(!_core.ActiveIsLeft); FocusActiveList(); e.Handled = true; return;
            case Key.Up when plain || shift:
                if (shift) pane.ToggleMark(false);
                pane.MoveBy(-1); e.Handled = true; return;
            case Key.Down when plain || shift:
                if (shift) pane.ToggleMark(false);
                pane.MoveBy(1); e.Handled = true; return;
            case Key.PageUp when plain: pane.MoveBy(-VisibleRows()); e.Handled = true; return;
            case Key.PageDown when plain: pane.MoveBy(VisibleRows()); e.Handled = true; return;
            case Key.Home when plain: pane.MoveTo(0); e.Handled = true; return;
            case Key.End when plain: pane.MoveTo(pane.Items.Count - 1); e.Handled = true; return;
            case Key.Enter when plain:
                e.Handled = true; await ActivePanel.Enter(); FocusActiveList(); return;
            case Key.Back when plain:
                e.Handled = true; await pane.GoUpAsync(); return;
            case Key.Space when plain:
                pane.ToggleMark(); e.Handled = true; return;
            case Key.Insert when plain:
                pane.ToggleMark(); e.Handled = true; return;
            case Key.Escape when plain:
                if (pane.Filter.Length > 0) ActivePanel.HideFilter();
                else { pane.UnmarkAll(); }
                e.Handled = true; return;
            case Key.Add when plain: e.Handled = true; await MarkByMaskAsync(true); return;
            case Key.Subtract when plain: e.Handled = true; await MarkByMaskAsync(false); return;
            case Key.Multiply when plain: e.Handled = true; pane.InvertMarks(); return;
        }
    }

    private int VisibleRows()
    {
        var h = ActivePanel.List.Bounds.Height;
        return Math.Max(1, (int)(h / (_settings.FontSize + 10)) - 1);
    }

    private void OnTextInputTunnel(object? sender, TextInputEventArgs e)
    {
        if (IsTextFocused() || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        _core.Active.QuickSearch(e.Text);
        e.Handled = true;
    }

    // --- jednotlivé akce ---------------------------------------------------------------------------------

    private async Task MarkByMaskAsync(bool on)
    {
        var mask = await _ui.PromptAsync(on ? "Označit podle masky" : "Odznačit podle masky", "Např. *.jpg nebo *.txt;*.doc", "*", "OK");
        if (mask != null) Pane.MarkMask(mask, on);
    }

    private async Task ViewCurrentAsync()
    {
        var pane = Pane;
        var item = pane.Current;
        if (item == null || item.IsParent) return;
        if (item.IsDirectory) { await ActivePanel.Enter(); return; }
        var data = await _core.LoadViewerAsync(pane, item);
        if (data == null) return;
        if (data.Kind == ViewerKind.External && data.OpenPath != null) { PaneState.OpenWithShell(data.OpenPath); return; }
        await ShowToolAsync(new ViewerWindow(data));
    }

    private async Task ShowAttributesAsync()
    {
        if (Pane.IsRemote || Pane.IsArchive) { await _ui.ShowErrorAsync("Atributy lze měnit jen u souborů na lokálním disku."); return; }
        var items = Pane.Targets();
        if (items.Count == 0) return;
        await ShowToolAsync(new AttributesWindow(_core, items));
    }

    private async Task ShowChecksumAsync()
    {
        if (Pane.IsRemote) { await _ui.ShowErrorAsync("Kontrolní součty se počítají jen z lokálních souborů."); return; }
        var files = Pane.Targets().Where(i => !i.IsDirectory).ToList();
        if (files.Count == 0) { await _ui.ShowErrorAsync("Vyberte aspoň jeden soubor."); return; }
        await ShowToolAsync(new ChecksumWindow(files));
    }

    private async Task ShowDiffAsync()
    {
        var a = _core.Left.Current; var b = _core.Right.Current;
        if (a.IsRemote || b.IsRemote) { await _ui.ShowErrorAsync("Porovnání souborů funguje jen mezi lokálními soubory."); return; }
        if (a.Current is not { IsDirectory: false, IsParent: false } x || b.Current is not { IsDirectory: false, IsParent: false } y)
        {
            await _ui.ShowErrorAsync("Postavte kurzor v obou panelech na soubor, který chcete porovnat.");
            return;
        }
        await ShowToolAsync(new DiffWindow(x.FullPath, y.FullPath));
    }

    private async Task ShowBatchRenameAsync()
    {
        var items = Pane.Targets();
        if (items.Count == 0) return;
        if (Pane.IsArchive) { await _ui.ShowErrorAsync("Archiv je otevřený jen pro čtení."); return; }
        var window = new BatchRenameWindow(_core, items);
        await ShowToolAsync(window);
        if (window.Result is { } names) await _core.ApplyRenameAsync(items, names);
    }

    // --- schránka -------------------------------------------------------------------------------------------

    private async Task CopyTextAsync(string text)
    {
        if (Clipboard == null || text.Length == 0) return;
        await Clipboard.SetTextAsync(text);
        _core.ShowNotice("Zkopírováno do schránky.");
    }

    private async Task CopyToClipboardAsync(bool cut)
    {
        var pane = Pane;
        if (pane.IsRemote) { await _ui.ShowErrorAsync("Schránka souborů funguje jen pro lokální soubory."); return; }
        var items = pane.Targets();
        if (items.Count == 0 || Clipboard == null) return;
        var storage = new List<IStorageItem>();
        foreach (var en in items)
        {
            IStorageItem? s = en.IsDirectory
                ? await StorageProvider.TryGetFolderFromPathAsync(en.FullPath)
                : await StorageProvider.TryGetFileFromPathAsync(en.FullPath);
            if (s != null) storage.Add(s);
        }
        if (storage.Count == 0) return;
        var data = new DataObject();
        data.Set(DataFormats.Files, storage);
        await Clipboard.SetDataObjectAsync(data);
        _cutPaths = cut ? items.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        _core.ShowNotice(cut ? $"Vyjmuto: {items.Count} položek. Vložte klávesou Ctrl+V." : $"Zkopírováno: {items.Count} položek. Vložte klávesou Ctrl+V.");
    }

    private async Task PasteFromClipboardAsync()
    {
        if (Clipboard == null) return;
        var obj = await Clipboard.GetDataAsync(DataFormats.Files);
        var paths = (obj as IEnumerable<IStorageItem>)?.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths == null || paths.Count == 0) { _core.ShowNotice("Schránka neobsahuje soubory."); return; }
        bool cut = _cutPaths.Count > 0 && paths.All(_cutPaths.Contains) && paths.Count == _cutPaths.Count;
        await _core.PasteAsync(paths, cut);
        if (cut) _cutPaths.Clear();
    }
}
