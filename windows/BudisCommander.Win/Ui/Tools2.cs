using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BudisCommander.Core;
using BudisCommander.Remote;

namespace BudisCommander.Ui;

/// <summary>Hledání souborů podle názvu a obsahu (lokálně) nebo jen podle názvu na serveru.</summary>
public sealed class SearchWindow : Window
{
    private readonly AppCore _core;
    private readonly TextBox _root = new(), _mask = new() { Text = "*" }, _text = new() { Watermark = "text v souboru (volitelné)" };
    private readonly CheckBox _hidden = new() { Content = "Včetně skrytých" };
    private readonly ListBox _results = new() { Height = 260 };
    private readonly TextBlock _status = new() { Opacity = 0.7 };
    private readonly Button _go = new() { Content = "Hledat" };
    private readonly List<string> _found = new();
    private CancellationTokenSource? _cts;
    private readonly RemoteSession? _conn;
    private readonly string _rootBase;
    private string _searchedRoot = "";

    public SearchWindow(AppCore core)
    {
        _core = core;
        var pane = core.Active;
        _conn = pane.Connection;
        _root.Text = pane.IsRemote ? pane.RemotePath : pane.PersistentPath;
        _rootBase = _root.Text!;
        if (pane.IsRemote) _text.IsEnabled = false;
        Title = "Hledat soubory";
        Width = 640; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 6 };
        void Row(int r, string label, Control c) { var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(l, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1); form.Children.Add(l); form.Children.Add(c); }
        Row(0, "Začít v", _root); Row(1, "Název", _mask); Row(2, "Obsahuje", _text);
        _go.Click += (_, _) => { if (_cts != null) Stop(); else Start(); };
        _results.DoubleTapped += async (_, _) => await OpenSelected();
        _results.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await OpenSelected(); } };
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Hledat soubory", 15, true), form, _hidden,
                UiKit.HStack(8, _go, _status), UiKit.Frame(_results),
                UiKit.Label("Dvojklikem se v aktivním panelu otevře složka souboru.", dim: true),
                UiKit.ButtonRow(UiKit.Button("Zavřít", Close))),
        };
        Closing += (_, _) => _cts?.Cancel();
        this.CloseOnEscape();
        Opened += (_, _) => _mask.Focus();
    }

    private async Task OpenSelected()
    {
        if (_results.SelectedItem is not string rel) return;
        var path = _conn != null ? rel : Path.Combine(_searchedRoot, rel);
        Close();
        await _core.RevealAsync(path);
    }

    private void Stop() => _cts?.Cancel();

    private async void Start()
    {
        _found.Clear(); _results.ItemsSource = null;
        var cts = _cts = new CancellationTokenSource();
        _go.Content = "Zastavit";
        var mask = _mask.Text ?? "*"; var text = _text.Text ?? ""; bool hidden = _hidden.IsChecked == true;
        var root = _root.Text ?? "";
        _searchedRoot = root;
        int scanned = 0;
        var progress = new Progress<string>(s => _status.Text = s);
        try
        {
            if (_conn != null)
            {
                await SearchRemoteAsync(_conn, root, mask, hidden, cts.Token);
            }
            else
            {
                if (!Directory.Exists(root)) { await _core.Ui.ShowErrorAsync($"Složka „{root}“ neexistuje."); return; }
                var batch = new List<string>();
                await Task.Run(() =>
                {
                    var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = hidden ? 0 : FileAttributes.Hidden | FileAttributes.System };
                    var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                    foreach (var fi in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        scanned++;
                        if (Formatting.MatchesMask(fi.Name, mask) && (text.Length == 0 || FileContains(fi, text)))
                        {
                            var rel = fi.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? fi.FullName[prefix.Length..] : fi.FullName;
                            lock (batch) batch.Add(rel);
                        }
                        if (scanned % 300 == 0)
                        {
                            string[] snapshot; lock (batch) snapshot = batch.ToArray();
                            Dispatcher.UIThread.Post(() => { _results.ItemsSource = snapshot; _status.Text = $"Nalezeno {snapshot.Length}, prohledáno {scanned}"; });
                        }
                        if (batch.Count >= 5000) break;
                    }
                }, cts.Token);
                _results.ItemsSource = batch.ToArray();
                _status.Text = $"Nalezeno {batch.Count}, prohledáno {scanned}";
            }
        }
        catch (OperationCanceledException) { _status.Text = "Zastaveno."; }
        catch (Exception e) { _status.Text = e.Message; }
        finally { _cts = null; _go.Content = "Hledat"; }
    }

    private static bool FileContains(FileSystemInfo fi, string text)
    {
        if (fi is not FileInfo f || f.Length == 0 || f.Length > 20 * 1024 * 1024) return false;
        try
        {
            var data = File.ReadAllBytes(f.FullName);
            if (LocalFs.LooksBinary(data)) return false;
            return System.Text.Encoding.UTF8.GetString(data).Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async Task SearchRemoteAsync(RemoteSession conn, string root, string mask, bool hidden, CancellationToken ct)
    {
        var queue = new Queue<string>(); queue.Enqueue(root);
        var found = new List<string>();
        int listed = 0;
        while (queue.Count > 0 && listed < 3000 && found.Count < 5000)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue(); listed++;
            List<FileEntry> items;
            try { items = await conn.ListAsync(dir, ct); } catch (OperationCanceledException) { throw; } catch { continue; }
            foreach (var i in items)
            {
                if (!hidden && i.Name.StartsWith('.')) continue;
                if (i.IsDirectory) queue.Enqueue(i.FullPath);
                if (Formatting.MatchesMask(i.Name, mask)) found.Add(i.FullPath);
            }
            _results.ItemsSource = found.ToArray();
            _status.Text = $"Nalezeno {found.Count}, prohledaných složek {listed}";
        }
    }
}

/// <summary>Hledání duplicit podle obsahu s výběrem přebytečných a mazáním do Koše.</summary>
public sealed class DuplicatesWindow : Window
{
    private readonly AppCore _core;
    private readonly CheckBox _both = new() { Content = "Hledat i ve složce druhého panelu" };
    private readonly CheckBox _hidden = new() { Content = "Včetně skrytých" };
    private readonly TextBox _minKb = new() { Text = "1", Width = 70 };
    private readonly StackPanel _groups = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { Opacity = 0.7 };
    private readonly Button _go = new() { Content = "Hledat" };
    private readonly Button _trash = new() { Content = "Do Koše (0)", IsEnabled = false };
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private List<DupGroup> _result = new();
    private CancellationTokenSource? _cts;

    public DuplicatesWindow(AppCore core)
    {
        _core = core;
        Title = "Hledání duplicit";
        Width = 760; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _go.Click += (_, _) => { if (_cts != null) _cts.Cancel(); else Start(); };
        _trash.Click += async (_, _) => await TrashSelectedAsync();
        var options = UiKit.HStack(12, _both, _hidden, UiKit.Label("Nejmenší velikost (kB)"), _minKb);
        var buttons = UiKit.HStack(8,
            UiKit.Button("Vybrat přebytečné (ponechat nejstarší)", SelectExtras), UiKit.Button("Zrušit výběr", () => { _selected.Clear(); Render(); }),
            _trash, UiKit.Button("Zavřít", Close));
        var scroll = new ScrollViewer { Content = _groups, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var dock = new DockPanel { Margin = new Thickness(16) };
        var top = UiKit.VStack(8, UiKit.Label("Hledání duplicit", 15, true), options, UiKit.HStack(8, _go, _status));
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(top); dock.Children.Add(buttons); dock.Children.Add(scroll);
        Content = dock;
        Closing += (_, _) => _cts?.Cancel();
        this.CloseOnEscape();
    }

    private async void Start()
    {
        var roots = new List<string>();
        if (!_core.Active.IsRemote) roots.Add(_core.Active.PersistentPath);
        if (_both.IsChecked == true && !_core.Other.IsRemote && !string.Equals(_core.Other.PersistentPath, _core.Active.PersistentPath, StringComparison.OrdinalIgnoreCase))
            roots.Add(_core.Other.PersistentPath);
        if (roots.Count == 0) { await _core.Ui.ShowErrorAsync("Hledání duplicit funguje jen v lokálních složkách."); return; }
        _selected.Clear(); _groups.Children.Clear();
        long kb = long.TryParse(_minKb.Text, out var k) ? Math.Max(k, 0) : 1;
        bool hidden = _hidden.IsChecked == true;
        var cts = _cts = new CancellationTokenSource();
        _go.Content = "Zastavit";
        var progress = new Progress<string>(s => _status.Text = s);
        try
        {
            _result = await Task.Run(() => DuplicateFinder.Scan(roots, kb * 1024, hidden, progress, cts.Token));
            _status.Text = _result.Count == 0 ? "Žádné duplicity." : $"Nalezeno skupin: {_result.Count}";
            Render();
        }
        catch (OperationCanceledException) { _status.Text = "Zastaveno."; }
        catch (Exception e) { _status.Text = e.Message; }
        finally { _cts = null; _go.Content = "Hledat"; }
    }

    private void Render()
    {
        _groups.Children.Clear();
        foreach (var g in _result.Take(500))
        {
            var box = new StackPanel { Spacing = 2 };
            box.Children.Add(UiKit.Label($"{g.Files.Count} × {Formatting.Size(g.Size)}", 12, true));
            foreach (var file in g.Files)
            {
                var path = file;
                var cb = new CheckBox { Content = path, IsChecked = _selected.Contains(path), FontSize = 12 };
                cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) _selected.Add(path); else _selected.Remove(path); UpdateTrash(); };
                var show = new Button { Content = "Ukázat", Padding = new Thickness(8, 0), FontSize = 11 };
                show.Click += async (_, _) => { Close(); await _core.RevealAsync(path); };
                var row = new DockPanel();
                DockPanel.SetDock(show, Dock.Right);
                row.Children.Add(show); row.Children.Add(cb);
                box.Children.Add(row);
            }
            _groups.Children.Add(UiKit.Frame(new Border { Padding = new Thickness(8), Child = box }));
        }
        UpdateTrash();
    }

    private void UpdateTrash()
    {
        _trash.Content = $"Do Koše ({_selected.Count})";
        _trash.IsEnabled = _selected.Count > 0;
    }

    private void SelectExtras()
    {
        _selected.Clear();
        foreach (var g in _result)
        {
            var keep = g.Files.OrderBy(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MaxValue; } }).First();
            foreach (var f in g.Files) if (f != keep) _selected.Add(f);
        }
        Render();
    }

    private async Task TrashSelectedAsync()
    {
        var paths = _selected.ToList();
        if (!await _core.Ui.ConfirmAsync($"Přesunout {paths.Count} souborů do Koše?", "Vybrané duplicity půjde obnovit z Koše.", "Do Koše")) return;
        try { await _core.TrashPathsAsync(paths); }
        catch (Exception e) { await _core.Ui.ShowErrorAsync(e.Message); }
        _result = _result.Select(g => g with { Files = g.Files.Where(f => !_selected.Contains(f)).ToList() }).Where(g => g.Files.Count > 1).ToList();
        _selected.Clear();
        Render();
        await _core.Active.ReloadAsync(true); await _core.Other.ReloadAsync(true);
    }
}

/// <summary>Oblíbené a naposledy navštívené složky.</summary>
public sealed class FavoritesWindow : Window
{
    private readonly AppCore _core;
    private readonly ListBox _list = new() { Height = 360 };
    private readonly List<(string Title, string? Path, bool Removable)> _entries = new();

    public FavoritesWindow(AppCore core)
    {
        _core = core;
        Title = "Oblíbené a poslední složky";
        Width = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Fill();
        _list.DoubleTapped += async (_, _) => await Go();
        _list.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Go(); } else if (e.Key == Key.Delete) { e.Handled = true; RemoveSelected(); } };
        var addCurrent = UiKit.Button("Přidat aktuální složku", () =>
        {
            var p = core.Active.PersistentPath;
            if (!core.Active.IsRemote && !core.Settings.Favorites.Contains(p, StringComparer.OrdinalIgnoreCase)) { core.Settings.Favorites.Add(p); Fill(); }
        });
        var remove = UiKit.Button("Odebrat z oblíbených", RemoveSelected);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Oblíbené a poslední složky", 15, true), UiKit.Frame(_list),
                UiKit.HStack(8, addCurrent, remove, UiKit.Button("Zavřít", Close, true)),
                UiKit.Label("Enter nebo dvojklik přejde do složky. Alt+← a Alt+→ jdou zpět a vpřed v historii panelu.", dim: true)),
        };
        this.CloseOnEscape();
        Opened += (_, _) => _list.Focus();
    }

    private void Fill()
    {
        _entries.Clear();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _entries.Add(("— Rychlý přístup —", null, false));
        foreach (var (name, path) in new[]
        {
            ("Domů", home), ("Plocha", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Dokumenty", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), ("Stažené", Path.Combine(home, "Downloads")),
        })
            if (Directory.Exists(path)) _entries.Add(($"{name}   {path}", path, false));
        foreach (var d in DriveInfo.GetDrives()) { try { if (d.IsReady) _entries.Add(($"Disk {d.Name}", d.Name, false)); } catch { } }
        if (_core.Settings.Favorites.Count > 0)
        {
            _entries.Add(("— Oblíbené —", null, false));
            foreach (var f in _core.Settings.Favorites) _entries.Add(($"{Path.GetFileName(f.TrimEnd('\\', '/'))}   {f}", f, true));
        }
        if (_core.Settings.Recents.Count > 0)
        {
            _entries.Add(("— Naposledy navštívené —", null, false));
            foreach (var r in _core.Settings.Recents) _entries.Add((r, r, false));
        }
        _list.ItemsSource = _entries.Select(e => e.Title).ToList();
    }

    private async Task Go()
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _entries.Count || _entries[i].Path == null) return;
        var path = _entries[i].Path!;
        Close();
        if (!Directory.Exists(path)) { await _core.Ui.ShowErrorAsync($"Složka „{path}“ neexistuje."); return; }
        await _core.Active.NavigateAsync(path);
    }

    private void RemoveSelected()
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _entries.Count || !_entries[i].Removable) return;
        _core.Settings.Favorites.Remove(_entries[i].Path!);
        Fill();
    }
}

/// <summary>Síťové disky a sdílené složky (UNC cesty).</summary>
public sealed class NetworkWindow : Window
{
    public NetworkWindow(AppCore core)
    {
        Title = "Síť a disky";
        Width = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var drives = new ListBox { Height = 140 };
        var items = new List<(string Text, string Path)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                items.Add(($"{d.Name}   {(string.IsNullOrEmpty(d.VolumeLabel) ? "" : d.VolumeLabel + "  ")}[{d.DriveType}]  {Formatting.Size(d.AvailableFreeSpace)} volných", d.Name));
            }
            catch { }
        }
        drives.ItemsSource = items.Select(i => i.Text).ToList();
        drives.DoubleTapped += async (_, _) =>
        {
            if (drives.SelectedIndex >= 0) { var p = items[drives.SelectedIndex].Path; Close(); await core.Active.NavigateAsync(p); }
        };
        var unc = new TextBox { Watermark = @"\\server\sdileni", Text = @"\\" };
        var user = new TextBox { Watermark = "uživatel (volitelné, např. DOMENA\\jmeno)" };
        var pass = new TextBox { Watermark = "heslo (volitelné)", PasswordChar = '•' };
        var status = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var go = UiKit.Button("Připojit a otevřít", async () =>
        {
            var path = (unc.Text ?? "").Trim();
            if (path.Length < 4) return;
            status.Text = "Připojuji…";
            if (!string.IsNullOrWhiteSpace(user.Text) && OperatingSystem.IsWindows())
            {
                var result = await Task.Run(() => RunNetUse(path, user.Text!.Trim(), pass.Text ?? ""));
                if (result != null) { status.Text = result; return; }
            }
            if (!Directory.Exists(path)) { status.Text = "Sdílenou složku se nepodařilo otevřít (zkontrolujte název a přihlášení)."; return; }
            Close();
            await core.Active.NavigateAsync(path);
        }, true);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Síť a disky", 15, true), UiKit.Label("Disky (dvojklik otevře)", dim: true), UiKit.Frame(drives),
                UiKit.Label("Sdílená složka v síti", dim: true), unc, user, pass, status,
                UiKit.Label("Přihlášení se uloží jen pro tuto relaci Windows (příkaz net use).", dim: true),
                UiKit.ButtonRow(UiKit.Button("Zavřít", Close), go)),
        };
        this.CloseOnEscape();
    }

    private static string? RunNetUse(string unc, string user, string password)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("net.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add("use"); psi.ArgumentList.Add(unc); psi.ArgumentList.Add(password); psi.ArgumentList.Add("/user:" + user);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? null : err.Trim();
        }
        catch (Exception e) { return e.Message; }
    }
}
