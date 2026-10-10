using BudisCommander.Remote;

namespace BudisCommander.Core;

public sealed record TransferPlan(bool Move, PaneState Src, PaneState Dst, List<FileEntry> Items, string SrcKey, string DstKey);

public enum ViewerKind { Text, Hex, Image, External }
public sealed record ViewerData(string Title, ViewerKind Kind, string? Text, string? FilePath, string? OpenPath);

/// <summary>Logika aplikace nezávislá na rozhraní: panely, operace, fronta, vrácení.</summary>
public sealed partial class AppCore : IDisposable
{
    public AppSettings Settings { get; }
    public IUserInterface Ui { get; }
    public TabGroup Left { get; }
    public TabGroup Right { get; }
    public bool ActiveIsLeft { get; set; } = true;
    public CommandRunner Runner { get; } = new();
    /// <summary>Panel, ze kterého právě vychází přetahování.</summary>
    public PaneState? DragSource { get; set; }

    public TabGroup ActiveGroup => ActiveIsLeft ? Left : Right;
    public TabGroup OtherGroup => ActiveIsLeft ? Right : Left;
    public PaneState Active => ActiveGroup.Current;
    public PaneState Other => OtherGroup.Current;

    // --- stav pro stavový řádek ------------------------------------------------
    public string? Notice { get; private set; }
    public double? Progress { get; private set; }
    public string ProgressText { get; private set; } = "";
    public bool CanCancel { get; private set; }
    public int QueueCount => _queue.Count;
    public string? UndoTitle => _undo.Count > 0 ? _undo[^1].Title : null;
    public event Action? StatusChanged;
    public event Action? SessionChanged;

    private readonly string? _settingsPath;
    private Task? _currentJob;
    private CancellationTokenSource? _jobCts;
    private readonly List<Func<CancellationToken, Task>> _queue = new();
    private TransferPlan? _interrupted;
    private readonly List<UndoEntry> _undo = new();
    private readonly Dictionary<string, CancellationTokenSource> _editWatchers = new();

    private sealed record UndoEntry(string Title, Func<Task> Action);

    public AppCore(AppSettings settings, IUserInterface ui, string? settingsPath = null)
    {
        Settings = settings;
        Ui = ui;
        _settingsPath = settingsPath;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        var s = settings.Session;
        Left = new TabGroup(settings, ExistingDirs(s.Left, home), s.LeftSelected);
        Right = new TabGroup(settings, ExistingDirs(s.Right, Directory.Exists(downloads) ? downloads : home), s.RightSelected);
        ActiveIsLeft = s.ActiveLeft;
        FileEntry.SplitExtension = settings.ShowExtColumn;
        foreach (var tab in Left.Tabs.Concat(Right.Tabs)) Hook(tab);
        Left.Changed += () => { Hook(Left.Current); SessionChanged?.Invoke(); };
        Right.Changed += () => { Hook(Right.Current); SessionChanged?.Invoke(); };
    }

    private static List<string> ExistingDirs(IEnumerable<string> paths, string fallback)
    {
        var list = paths.Where(Directory.Exists).ToList();
        if (list.Count == 0) list.Add(fallback);
        return list;
    }

    private readonly HashSet<PaneState> _hooked = new();

    private void Hook(PaneState pane)
    {
        if (!_hooked.Add(pane)) return;
        pane.Error += msg => _ = Ui.ShowErrorAsync(msg);
        pane.LocationChanged += SaveState;
    }

    /// <summary>Načte obsah všech panelů (při startu).</summary>
    public async Task LoadAllAsync()
    {
        var tasks = new List<Task>();
        foreach (var t in Left.Tabs.Concat(Right.Tabs)) tasks.Add(t.LoadLocalAsync(t.Path, "", true));
        await Task.WhenAll(tasks);
    }

    public void SaveState()
    {
        Settings.Session = new SessionState
        {
            Left = Left.Tabs.Select(t => t.PersistentPath).ToList(),
            LeftSelected = Left.Selected,
            Right = Right.Tabs.Select(t => t.PersistentPath).ToList(),
            RightSelected = Right.Selected,
            ActiveLeft = ActiveIsLeft,
        };
        Settings.Save(_settingsPath);
    }

    // --- stavový řádek ----------------------------------------------------------

    private int _noticeId;

    public void ShowNotice(string text)
    {
        Notice = text;
        int id = ++_noticeId;
        StatusChanged?.Invoke();
        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            if (id == _noticeId) { Notice = null; StatusChanged?.Invoke(); }
        });
    }

    private void SetProgress(double? fraction, string text)
    {
        Progress = fraction;
        ProgressText = text;
        StatusChanged?.Invoke();
    }

    public static string Describe(IReadOnlyList<FileEntry> items) =>
        items.Count == 1 ? $"„{items[0].Name}“" : $"{items.Count} položek";

    // --- spouštění úloh se zrušením a fronta -----------------------------------------

    private bool Busy => _currentJob is { IsCompleted: false };

    /// <summary>Spustí úlohu, nebo ji (u přenosů) zařadí do fronty.</summary>
    private void RunJob(Func<CancellationToken, Task> work, bool queueable = false)
    {
        if (Busy)
        {
            if (queueable)
            {
                _queue.Add(work);
                ShowNotice($"Přidáno do fronty (ve frontě: {_queue.Count})");
                StatusChanged?.Invoke();
            }
            else ShowNotice("Právě probíhá jiná operace.");
            return;
        }
        StartJob(work);
    }

    private void StartJob(Func<CancellationToken, Task> work)
    {
        var cts = _jobCts = new CancellationTokenSource();
        var tcs = new TaskCompletionSource();
        _currentJob = tcs.Task;
        CanCancel = true;
        StatusChanged?.Invoke();
        _ = RunJobAsync(work, cts.Token, tcs);
    }

    private async Task RunJobAsync(Func<CancellationToken, Task> work, CancellationToken ct, TaskCompletionSource tcs)
    {
        try { await work(ct); }
        catch (OperationCanceledException) { }
        catch (Exception e) { await Ui.ShowErrorAsync(e.Message); }
        finally
        {
            CanCancel = false;
            SetProgress(null, "");
            tcs.SetResult();
            if (_queue.Count > 0)
            {
                var next = _queue[0]; _queue.RemoveAt(0);
                StartJob(next);
            }
        }
    }

    public void CancelJob(bool all = false)
    {
        if (all) { _queue.Clear(); StatusChanged?.Invoke(); }
        _jobCts?.Cancel();
    }

    /// <summary>Počká na dokončení běžící úlohy i celé fronty (pro testy).</summary>
    public async Task WaitIdleAsync()
    {
        while (Busy || _queue.Count > 0) await Task.Delay(10);
    }

    // --- přenosy (F5 / F6) ----------------------------------------------------------

    private static string LocationKey(PaneState p) => p.IsRemote ? $"{p.Connection!.DisplayName}|{p.RemotePath}" : $"local|{p.Path}";

    private bool Exists(string name, PaneState pane)
    {
        if (pane.IsRemote) return pane.AllItems.Any(i => !i.IsParent && i.Name == name);
        var p = System.IO.Path.Combine(pane.Path, name);
        return File.Exists(p) || Directory.Exists(p);
    }

    public async Task<TransferPlan?> PrepareTransferAsync(bool move)
    {
        var src = Active; var dst = Other;
        var items = src.Targets();
        if (items.Count == 0) return null;
        if (dst.IsArchive) { await Ui.ShowErrorAsync("Archiv je otevřený jen pro čtení, nelze do něj kopírovat."); return null; }
        if (src.IsArchive && move) { await Ui.ShowErrorAsync("Z archivu lze soubory jen kopírovat (F5), ne přesouvat."); return null; }
        bool same = !src.IsRemote && !dst.IsRemote
            ? string.Equals(src.Path, dst.Path, StringComparison.OrdinalIgnoreCase)
            : ReferenceEquals(src.Connection, dst.Connection) && src.RemotePath == dst.RemotePath;
        if (same && !src.Branch) { await Ui.ShowErrorAsync("Zdrojový a cílový adresář jsou stejné."); return null; }
        var verb = move ? "Přesunout" : "Kopírovat";
        if (!await Ui.ConfirmAsync($"{verb} {Describe(items)}?", "Cíl: " + dst.Title, verb)) return null;
        return new TransferPlan(move, src, dst, items, LocationKey(src), LocationKey(dst));
    }

    public async Task StartTransferAsync(bool move)
    {
        var plan = await PrepareTransferAsync(move);
        if (plan != null) RunJob(ct => TransferAsync(plan, ct), true);
    }

    public void ResumeTransfer()
    {
        if (_interrupted == null) { ShowNotice("Není žádný přerušený přenos."); return; }
        var plan = _interrupted; _interrupted = null;
        RunJob(ct => TransferAsync(plan, ct), true);
    }

    public async Task TransferAsync(TransferPlan plan, CancellationToken ct)
    {
        var src = plan.Src; var dst = plan.Dst; var move = plan.Move;
        if (LocationKey(src) != plan.SrcKey || LocationKey(dst) != plan.DstKey)
        {
            await Ui.ShowErrorAsync("Panely mezitím přešly do jiných složek. Vraťte je do původních a pokračujte z menu Nástroje.");
            _interrupted = plan;
            return;
        }
        var verb = move ? "Přesunout" : "Kopírovat";
        var items = plan.Items;
        var copies = new List<string>();
        var moves = new List<(string Current, string Original)>();
        bool overwriteAll = false, cancelled = false;
        var remaining = new List<FileEntry>();
        var errors = new List<string>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (ct.IsCancellationRequested) { cancelled = true; remaining = items.Skip(i).ToList(); break; }
            SetProgress((double)i / items.Count, $"{verb} {item.Name} ({i + 1}/{items.Count})");
            int index = i;
            var progress = new Progress<(string Text, double Fraction)>(p =>
                SetProgress((index + p.Fraction) / items.Count, $"{verb} {p.Text} – {(int)(p.Fraction * 100)} % ({index + 1}/{items.Count})"));

            if (!src.IsRemote && !dst.IsRemote && item.IsDirectory)
            {
                var d = dst.Path.TrimEnd('\\', '/'); var s = item.FullPath.TrimEnd('\\', '/');
                if (string.Equals(d, s, StringComparison.OrdinalIgnoreCase) || d.StartsWith(s + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{item.Name}: nelze {(move ? "přesunout" : "kopírovat")} složku do sebe sama");
                    continue;
                }
            }
            bool overwrite = false;
            if (Exists(item.Name, dst))
            {
                if (overwriteAll) overwrite = true;
                else
                {
                    switch (await Ui.AskConflictAsync(item.Name))
                    {
                        case ConflictChoice.Overwrite: overwrite = true; break;
                        case ConflictChoice.OverwriteAll: overwrite = true; overwriteAll = true; break;
                        case ConflictChoice.Skip: continue;
                        default:
                            await FinishTransferAsync(src, dst);
                            return;
                    }
                }
            }
            try
            {
                await TransferOneAsync(item, src, dst, move, overwrite, progress, ct);
                if (!overwrite && !src.IsRemote && !dst.IsRemote)
                {
                    var created = System.IO.Path.Combine(dst.Path, item.Name);
                    if (move) moves.Add((created, item.FullPath)); else copies.Add(created);
                }
            }
            catch (OperationCanceledException)
            {
                if (!dst.IsRemote)
                {
                    try { FileOps.DeletePermanently(System.IO.Path.Combine(dst.Path, item.Name)); } catch { }
                }
                cancelled = true; remaining = items.Skip(i).ToList(); break;
            }
            catch (Exception e) { errors.Add($"{item.Name}: {e.Message}"); }
        }
        foreach (var m in src.AllItems) m.IsMarked = false;
        await FinishTransferAsync(src, dst);
        if (copies.Count + moves.Count > 0)
            PushUndo($"{(move ? "přesun" : "kopírování")} {copies.Count + moves.Count} položek", () => UndoTransferAsync(copies, moves));
        _interrupted = cancelled ? plan with { Items = remaining } : null;
        if (cancelled) ShowNotice($"Přenos zrušen. Zbylé položky ({remaining.Count}) jde dokončit z menu Nástroje.");
        if (errors.Count > 0) await Ui.ShowErrorAsync(string.Join("\n", errors));
    }

    private static Task UndoTransferAsync(List<string> copies, List<(string Current, string Original)> moves) => Task.Run(() =>
    {
        foreach (var c in copies) FileOps.DeleteToRecycleBin(c);
        foreach (var (cur, orig) in moves)
        {
            if (Directory.Exists(cur)) Directory.Move(cur, orig); else File.Move(cur, orig);
        }
    });

    private async Task FinishTransferAsync(PaneState a, PaneState b)
    {
        SetProgress(null, "");
        await a.ReloadAsync(true);
        await b.ReloadAsync(true);
    }

    private async Task TransferOneAsync(FileEntry item, PaneState src, PaneState dst, bool move, bool overwrite,
                                        IProgress<(string Text, double Fraction)> progress, CancellationToken ct)
    {
        var sc = src.Connection; var dc = dst.Connection;
        if (sc == null && dc == null)
        {
            var dest = System.IO.Path.Combine(dst.Path, item.Name);
            if (move) await FileOps.MoveAsync(item.FullPath, dest, overwrite, progress, ct);
            else await FileOps.CopyAsync(item.FullPath, dest, overwrite, progress, ct);
        }
        else if (sc == null && dc != null)
        {
            await dc.UploadAsync(item.FullPath, RemotePath.Child(dst.RemotePath, item.Name), progress, ct);
            if (move) FileOps.DeletePermanently(item.FullPath);
        }
        else if (sc != null && dc == null)
        {
            var dest = System.IO.Path.Combine(dst.Path, item.Name);
            if (overwrite && (File.Exists(dest) || Directory.Exists(dest))) FileOps.DeletePermanently(dest);
            await sc.DownloadAsync(item.FullPath, item.IsDirectory, item.Size, dest, progress, ct);
            if (move) await sc.DeleteAsync(item.FullPath, item.IsDirectory, ct);
        }
        else
        {
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BudisTransfer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var local = System.IO.Path.Combine(tmp, item.Name);
                await sc!.DownloadAsync(item.FullPath, item.IsDirectory, item.Size, local,
                    new Progress<(string Text, double Fraction)>(p => progress.Report((p.Text, p.Fraction / 2))), ct);
                await dc!.UploadAsync(local, RemotePath.Child(dst.RemotePath, item.Name),
                    new Progress<(string Text, double Fraction)>(p => progress.Report((p.Text, 0.5 + p.Fraction / 2))), ct);
                if (move) await sc.DeleteAsync(item.FullPath, item.IsDirectory, ct);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }

    // --- přetahování a schránka --------------------------------------------------

    /// <summary>Soubory puštěné na panel nebo složku (z druhého panelu nebo z Průzkumníka).</summary>
    public async Task DropFilesAsync(IReadOnlyList<string> paths, PaneState dst, string? folder)
    {
        if (paths.Count == 0) return;
        var ds = DragSource;
        DragSource = null;
        if (ds != null && !ds.IsRemote && ds.AllItems.Any(i => paths.Contains(i.FullPath, StringComparer.OrdinalIgnoreCase)))
        {
            var dragged = ds.AllItems.Where(i => !i.IsParent && paths.Contains(i.FullPath, StringComparer.OrdinalIgnoreCase)).ToList();
            var items = dragged.Any(d => d.IsMarked) ? ds.Targets() : dragged;
            if (folder == null && ReferenceEquals(ds, dst)) return;
            if (folder != null && items.Any(i => string.Equals(i.FullPath, folder, StringComparison.OrdinalIgnoreCase))) return;
            await TransferItemsAsync(items, ds, dst, folder, null);
            return;
        }
        var entries = paths.Select(EntryFromPath).Where(e => e != null).Cast<FileEntry>().ToList();
        if (entries.Count == 0) return;
        await TransferItemsAsync(entries, null, dst, folder, null);
    }

    public async Task PasteAsync(IReadOnlyList<string> paths, bool cut)
    {
        var entries = paths.Select(EntryFromPath).Where(e => e != null).Cast<FileEntry>().ToList();
        if (entries.Count == 0) { ShowNotice("Schránka neobsahuje soubory."); return; }
        await TransferItemsAsync(entries, null, Active, null, cut);
    }

    public static FileEntry? EntryFromPath(string path)
    {
        try
        {
            FileSystemInfo fi = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!fi.Exists) return null;
            bool isDir = (fi.Attributes & FileAttributes.Directory) != 0;
            return new FileEntry
            {
                Name = fi.Name, FullPath = fi.FullName, IsDirectory = isDir,
                Size = fi is FileInfo f ? f.Length : 0, Modified = fi.LastWriteTime, Attributes = fi.Attributes,
            };
        }
        catch { return null; }
    }

    private async Task TransferItemsAsync(List<FileEntry> items, PaneState? source, PaneState dst, string? folder, bool? move)
    {
        if (items.Count == 0) return;
        if (dst.IsArchive) { await Ui.ShowErrorAsync("Archiv je otevřený jen pro čtení."); return; }
        var src = source ?? new PaneState(Settings, System.IO.Path.GetDirectoryName(items[0].FullPath) ?? items[0].FullPath, true);
        var target = folder != null ? new PaneState(Settings, folder, true) : dst;
        if (!src.IsRemote && !target.IsRemote && string.Equals(src.Path, target.Path, StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice("Zdrojová a cílová složka jsou stejné.");
            return;
        }
        bool doMove;
        if (move.HasValue) doMove = move.Value;
        else
        {
            var choice = await Ui.ChooseAsync($"Co udělat s {Describe(items)}?", "Cíl: " + target.Title, new[] { "Kopírovat", "Přesunout", "Zrušit" });
            if (choice == 0) doMove = false; else if (choice == 1) doMove = true; else return;
        }
        // dočasné panely je nutné načíst, aby šlo zjistit existující soubory
        if (!ReferenceEquals(target, dst)) await target.LoadLocalAsync(target.Path, "", true);
        if (source == null) await src.LoadLocalAsync(src.Path, "", true);
        var plan = new TransferPlan(doMove, src, target, items, LocationKey(src), LocationKey(target));
        RunJob(ct => TransferAsync(plan, ct), true);
    }

    // --- vrácení operací ----------------------------------------------------------------

    private void PushUndo(string title, Func<Task> action)
    {
        _undo.Add(new UndoEntry(title, action));
        if (_undo.Count > 30) _undo.RemoveAt(0);
        StatusChanged?.Invoke();
    }

    public async Task UndoAsync()
    {
        if (_undo.Count == 0) { ShowNotice("Není co vracet."); return; }
        var e = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        try { await e.Action(); ShowNotice($"Vráceno: {e.Title}"); }
        catch (Exception ex) { await Ui.ShowErrorAsync($"Vrácení se nepodařilo:\n{ex.Message}"); }
        await Left.Current.ReloadAsync(true);
        await Right.Current.ReloadAsync(true);
        StatusChanged?.Invoke();
    }

    // --- mazání, nová složka, přejmenování ------------------------------------------------

    public async Task StartDeleteAsync(bool permanent)
    {
        var pane = Active;
        var items = pane.Targets();
        if (items.Count == 0) return;
        if (pane.IsArchive) { await Ui.ShowErrorAsync("Archiv je otevřený jen pro čtení."); return; }
        bool toBin = !pane.IsRemote && !permanent;
        var ok = toBin
            ? await Ui.ConfirmAsync($"Přesunout {Describe(items)} do Koše?", "", "Do Koše")
            : await Ui.ConfirmAsync($"Trvale smazat {Describe(items)}?", "Tuto akci nelze vrátit zpět.", "Smazat");
        if (!ok) return;
        RunJob(async ct =>
        {
            var errors = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var item = items[i];
                SetProgress((double)i / items.Count, "Mažu " + item.Name);
                try
                {
                    if (pane.IsRemote) await pane.Connection!.DeleteAsync(item.FullPath, item.IsDirectory, ct);
                    else if (toBin) await Task.Run(() => FileOps.DeleteToRecycleBin(item.FullPath), ct);
                    else await Task.Run(() => FileOps.DeletePermanently(item.FullPath), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { errors.Add($"{item.Name}: {e.Message}"); }
            }
            SetProgress(null, "");
            await pane.ReloadAsync(true);
            if (errors.Count > 0) await Ui.ShowErrorAsync(string.Join("\n", errors));
        });
    }

    public async Task MakeDirectoryAsync()
    {
        var pane = Active;
        if (pane.IsArchive) { await Ui.ShowErrorAsync("Archiv je otevřený jen pro čtení."); return; }
        var name = await Ui.PromptAsync("Nová složka", "Vytvoří se v " + pane.Title, "", "Vytvořit");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        try
        {
            if (pane.IsRemote)
            {
                var conn = pane.Connection!;
                var path = RemotePath.Child(pane.RemotePath, name);
                await conn.MkdirAsync(path, CancellationToken.None);
                PushUndo($"vytvoření složky „{name}“", () => conn.DeleteAsync(path, true, CancellationToken.None));
                await pane.ReloadAsync();
            }
            else
            {
                var created = System.IO.Path.Combine(pane.Path, name);
                Directory.CreateDirectory(created);
                PushUndo($"vytvoření složky „{name}“", () => Task.Run(() => FileOps.DeleteToRecycleBin(created)));
                await pane.ReloadAsync(true);
            }
            var idx = pane.Items.FindIndex(i => i.Name == name.Split('\\', '/')[0]);
            if (idx >= 0) pane.MoveTo(idx);
        }
        catch (Exception e) { await Ui.ShowErrorAsync(e.Message); }
    }

    public async Task RenameCurrentAsync()
    {
        var pane = Active;
        var item = pane.Current;
        if (item == null || item.IsParent) return;
        if (pane.IsArchive) { await Ui.ShowErrorAsync("Archiv je otevřený jen pro čtení."); return; }
        var name = await Ui.PromptAsync("Přejmenovat", "", item.Name, "Přejmenovat");
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        name = name.Trim();
        try
        {
            if (pane.IsRemote)
            {
                var conn = pane.Connection!;
                var newPath = RemotePath.Child(RemotePath.Parent(item.FullPath), name);
                var oldPath = item.FullPath;
                await conn.RenameAsync(oldPath, newPath, CancellationToken.None);
                PushUndo($"přejmenování „{item.Name}“", () => conn.RenameAsync(newPath, oldPath, CancellationToken.None));
                await pane.ReloadAsync();
            }
            else
            {
                var original = item.FullPath;
                var dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(original)!, name);
                if (item.IsDirectory) Directory.Move(original, dest); else File.Move(original, dest);
                PushUndo($"přejmenování „{item.Name}“", () => Task.Run(() =>
                {
                    if (Directory.Exists(dest)) Directory.Move(dest, original); else File.Move(dest, original);
                }));
                await pane.ReloadAsync(true);
            }
            var idx = pane.Items.FindIndex(i => i.Name == name);
            if (idx >= 0) pane.MoveTo(idx);
        }
        catch (Exception e) { await Ui.ShowErrorAsync(e.Message); }
    }

    /// <summary>Hromadné přejmenování; při překryvu názvů používá dočasné názvy.</summary>
    public async Task ApplyRenameAsync(IReadOnlyList<FileEntry> items, IReadOnlyList<string> names)
    {
        var pane = Active;
        var pairs = items.Zip(names).Where(p => p.First.Name != p.Second).ToList();
        if (pairs.Count == 0) return;
        var old = pairs.Select(p => p.First.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool needTemp = pairs.Any(p => old.Contains(p.Second));
        var errors = new List<string>();
        var staged = new List<(FileEntry Item, string Temp, string New)>();

        async Task Rename(FileEntry item, string from, string to)
        {
            if (pane.IsRemote)
            {
                var dir = RemotePath.Parent(item.FullPath);
                await pane.Connection!.RenameAsync(RemotePath.Child(dir, from), RemotePath.Child(dir, to), CancellationToken.None);
            }
            else
            {
                var dir = System.IO.Path.GetDirectoryName(item.FullPath)!;
                var a = System.IO.Path.Combine(dir, from); var b = System.IO.Path.Combine(dir, to);
                if (Directory.Exists(a)) Directory.Move(a, b); else File.Move(a, b);
            }
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            var (item, @new) = pairs[i];
            SetProgress((double)i / pairs.Count, "Přejmenovávám " + item.Name);
            try
            {
                if (needTemp)
                {
                    var tmp = $".__brn{i}_{Guid.NewGuid().ToString("N")[..6]}";
                    await Rename(item, item.Name, tmp);
                    staged.Add((item, tmp, @new));
                }
                else await Rename(item, item.Name, @new);
            }
            catch (Exception e) { errors.Add($"{item.Name}: {e.Message}"); }
        }
        foreach (var (item, tmp, @new) in staged)
        {
            try { await Rename(item, tmp, @new); }
            catch (Exception e) { errors.Add($"{@new}: {e.Message}"); }
        }
        SetProgress(null, "");
        foreach (var m in pane.AllItems) m.IsMarked = false;
        await pane.ReloadAsync(true);
        if (errors.Count > 0) await Ui.ShowErrorAsync(string.Join("\n", errors));
    }

    // --- připojení k serveru -------------------------------------------------------------

    public async Task<bool> ConnectAsync(SavedServer server, string password)
    {
        RemoteSession session;
        try
        {
            session = server.Protocol == RemoteProtocol.Sftp
                ? new SftpSession(server.Host, server.EffectivePort, server.User, password, server.KeyPath, TrustHost(server))
                : new FtpSession(server.Host, server.EffectivePort, server.User, password, server.Protocol == RemoteProtocol.FtpTls, server.Insecure);
        }
        catch (Exception e)
        {
            await Ui.ShowErrorAsync(e.Message);
            return false;
        }
        try
        {
            SetProgress(0, "Připojuji " + session.DisplayName + "…");
            await session.ConnectAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            SetProgress(null, "");
            session.Dispose();
            await Ui.ShowErrorAsync($"Připojení k {server.Host} se nezdařilo:\n\n{e.Message}");
            return false;
        }
        SetProgress(null, "");
        await Active.ConnectAsync(session);
        return Active.Connection == session;
    }

    private Func<string, string, bool> TrustHost(SavedServer server) => (host, fingerprint) =>
    {
        if (server.Insecure) return true;
        if (Settings.KnownHosts.TryGetValue(host, out var known)) return known == fingerprint || Ui.TrustHostKey(host, fingerprint);
        var trust = Ui.TrustHostKey(host, fingerprint);
        if (trust) Settings.KnownHosts[host] = fingerprint;
        return trust;
    };

    public void Dispose()
    {
        foreach (var w in _editWatchers.Values) w.Cancel();
        foreach (var t in Left.Tabs.Concat(Right.Tabs)) t.Dispose();
        try { if (Directory.Exists(ArchiveService.TempRoot)) Directory.Delete(ArchiveService.TempRoot, true); } catch { }
    }
}

