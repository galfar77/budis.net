using System.Runtime.InteropServices;
using BudisCommander.Remote;

namespace BudisCommander.Core;

public enum SortKey { Name, Ext, Size, Date }

public sealed class ArchiveInfo
{
    public string Name { get; init; } = "";
    public string Root { get; init; } = "";
    public string Origin { get; init; } = "";
    public string ItemId { get; init; } = "";
}

/// <summary>Řazení názvů jako v Průzkumníku (číslice po číslech).</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        if (OperatingSystem.IsWindows())
        {
            try { return StrCmpLogicalW(x, y); } catch { /* spadne na obecné porovnání */ }
        }
        return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
    }
}

/// <summary>Stav jednoho panelu: kde je, co v něm je, kurzor, označení, řazení, filtr a historie.</summary>
public sealed class PaneState : IDisposable
{
    public AppSettings Settings { get; }
    public bool Ephemeral { get; }

    /// <summary>Poslední lokální složka (zůstává nastavená i při připojení k serveru).</summary>
    public string Path { get; private set; }
    public RemoteSession? Connection { get; private set; }
    public string RemotePath { get; private set; } = "/";
    public ArchiveInfo? Archive { get; private set; }

    public List<FileEntry> AllItems { get; private set; } = new();
    public List<FileEntry> Items { get; private set; } = new();
    public int Cursor { get; private set; }
    public string Filter { get; private set; } = "";
    public SortKey SortKey { get; private set; } = SortKey.Name;
    public bool Ascending { get; private set; } = true;
    /// <summary>Rychlý filtr podle druhu souboru (id z <see cref="TypeFilters"/>) nebo null.</summary>
    public string? TypeFilter { get; private set; }
    private bool _viewApplied;

    public static readonly (string Id, string Label, HashSet<string> Exts)[] TypeFilters =
    {
        ("images", "Jen obrázky", Ext("jpg jpeg png gif bmp tif tiff webp heic heif svg ico raw cr2 nef arw dng")),
        ("docs", "Jen dokumenty", Ext("pdf doc docx odt rtf txt md pages xls xlsx ods csv numbers ppt pptx odp key epub")),
        ("audio", "Jen hudba", Ext("mp3 flac wav aac m4a ogg opus wma aiff aif")),
        ("video", "Jen video", Ext("mp4 mkv avi mov wmv webm m4v mpg mpeg flv")),
        ("archives", "Jen archivy", Ext("zip rar 7z tar gz tgz bz2 xz iso dmg")),
    };

    private static HashSet<string> Ext(string list) => new(list.Split(' '), StringComparer.OrdinalIgnoreCase);

    /// <summary>Název aktivního filtru podle druhu (česky) nebo null.</summary>
    public string? TypeFilterLabel => TypeFilters.FirstOrDefault(t => t.Id == TypeFilter).Label;
    public bool ShowHidden { get; private set; }
    public bool Branch { get; private set; }
    public bool IsLoading { get; private set; }
    public int Generation { get; private set; }

    public event Action? ListChanged;
    public event Action? CursorChanged;
    public event Action? StateChanged;
    public event Action<string>? Error;
    /// <summary>Po každé úspěšné změně složky (pro ukládání stavu).</summary>
    public event Action? LocationChanged;

    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();
    private bool _historyMove;
    private int _version;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private FileSystemWatcher? _watcher;
    private string _watchedPath = "";
    private bool _reloadScheduled;
    private CancellationTokenSource? _sizesCts;
    private string _searchBuffer = "";
    private DateTime _searchTime = DateTime.MinValue;

    public PaneState(AppSettings settings, string path, bool ephemeral = false)
    {
        Settings = settings;
        Ephemeral = ephemeral;
        Path = path;
        ShowHidden = settings.ShowHidden;
    }

    // --- vlastnosti odvozené ---------------------------------------------------

    public bool IsRemote => Connection != null;
    public bool IsArchive => Archive != null;
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public FileEntry? Current => Cursor >= 0 && Cursor < Items.Count ? Items[Cursor] : null;
    public string PersistentPath => Archive?.Origin ?? Path;

    public string Title
    {
        get
        {
            if (Archive != null)
            {
                var rel = Path.Length > Archive.Root.Length ? Path[Archive.Root.Length..] : "";
                return $"Archiv {Archive.Name}{rel} (jen pro čtení)";
            }
            if (Connection != null) return Connection.DisplayName + RemotePath;
            return Branch ? Path + "  (všechny podsložky)" : Path;
        }
    }

    public string TabTitle
    {
        get
        {
            if (Archive != null) return Archive.Name;
            if (Connection != null) return Connection.Host;
            var name = System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }

    public IEnumerable<FileEntry> Marked => AllItems.Where(i => i.IsMarked && !i.IsParent);

    /// <summary>Označené položky, případně položka pod kurzorem.</summary>
    public List<FileEntry> Targets()
    {
        var m = AllItems.Where(i => i.IsMarked && !i.IsParent).ToList();
        if (m.Count > 0) return m;
        var c = Current;
        return c != null && !c.IsParent ? new List<FileEntry> { c } : new List<FileEntry>();
    }

    public string Summary
    {
        get
        {
            var files = Items.Where(i => !i.IsParent).ToList();
            var marked = files.Where(i => i.IsMarked).ToList();
            if (Filter.Length > 0)
                return $"Filtr „{Filter}“: {files.Count} z {AllItems.Count(i => !i.IsParent)} položek (Esc zruší)";
            if (TypeFilter != null)
                return $"{Tr.T(TypeFilterLabel ?? "")}: {files.Count} z {AllItems.Count(i => !i.IsParent)} položek";
            if (marked.Count == 0)
                return $"{files.Count} položek, {Formatting.Size(files.Where(f => !f.IsDirectory).Sum(f => f.Size))}";
            return $"Označeno {marked.Count} z {files.Count}, {Formatting.Size(marked.Where(f => !f.IsDirectory).Sum(f => f.Size))}";
        }
    }

    // --- načítání ---------------------------------------------------------------

    private void Post(Action a)
    {
        if (_ui != null) _ui.Post(_ => a(), null); else a();
    }

    private void RaiseError(string text, bool silent)
    {
        if (!silent && !Ephemeral) Error?.Invoke(text);
    }

    public async Task<bool> LoadLocalAsync(string dir, string? selectId = null, bool silent = false)
    {
        int version = ++_version;
        IsLoading = true; StateChanged?.Invoke();
        List<FileEntry> list;
        bool wantBranch = Branch && dir == Path;
        bool hidden = ShowHidden;
        try
        {
            list = await Task.Run(() => wantBranch ? LocalFs.ListBranch(dir, hidden) : LocalFs.List(dir, hidden));
        }
        catch (Exception e)
        {
            if (version == _version) { IsLoading = false; StateChanged?.Invoke(); }
            RaiseError($"Složku nelze otevřít:\n{dir}\n\n{e.Message}", silent);
            return false;
        }
        if (version != _version) return false;      // mezitím se načítá jiná složka

        var previousId = selectId ?? Current?.Id;
        bool changed = !string.Equals(dir, Path, StringComparison.OrdinalIgnoreCase);
        if (changed) { Branch = false; Filter = ""; CancelSizes(); }
        if ((changed || !_viewApplied) && !Ephemeral && !IsTemp(dir)) { ApplyFolderView(dir); _viewApplied = true; }
        if (changed && !_historyMove && !IsTemp(Path) && !IsTemp(dir) && Connection == null)
        {
            _back.Add(Path); _forward.Clear();
            if (_back.Count > 100) _back.RemoveAt(0);
        }
        if (!Ephemeral && !IsTemp(dir)) Settings.AddRecent(dir);

        Path = dir;
        Connection = null;
        SortInPlace(list);
        var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent) || dir.Length > 3) list.Insert(0, FileEntry.Parent(parent ?? dir));
        Apply(list, previousId);
        IsLoading = false;
        if (!Ephemeral) LocationChanged?.Invoke();
        Watch(dir);
        StartAutoSizes();
        StateChanged?.Invoke();
        return true;
    }

    public async Task<bool> LoadRemoteAsync(RemoteSession conn, string path, string? selectId = null)
    {
        int version = ++_version;
        IsLoading = true; StateChanged?.Invoke();
        try
        {
            var list = await conn.ListAsync(path, CancellationToken.None);
            if (version != _version) return false;
            if (!ShowHidden) list = list.Where(i => !i.Name.StartsWith('.')).ToList();
            var previousId = selectId ?? Current?.Id;
            if (!ReferenceEquals(conn, Connection) || path != RemotePath) { Filter = ""; CancelSizes(); }
            if (!ReferenceEquals(conn, Connection)) { StopWatching(); Branch = false; }
            Connection = conn;
            RemotePath = RemoteSessionPath(path);
            SortInPlace(list);
            if (RemotePath != "/") list.Insert(0, FileEntry.Parent(Remote.RemotePath.Parent(RemotePath), true));
            Apply(list, previousId);
            IsLoading = false;
            StateChanged?.Invoke();
            return true;
        }
        catch (Exception e)
        {
            if (version == _version) { IsLoading = false; StateChanged?.Invoke(); }
            RaiseError($"Server: {conn.DisplayName}\n\n{e.Message}", false);
            return false;
        }
    }

    private static string RemoteSessionPath(string p) => Remote.RemotePath.Normalize(p);

    private static bool IsTemp(string path) => path.StartsWith(ArchiveService.TempRoot, StringComparison.OrdinalIgnoreCase);

    private void Apply(List<FileEntry> list, string? previousId)
    {
        AllItems = list;
        Generation++;
        Items = Visible(list);
        if (previousId == "") Cursor = 0;
        else if (previousId != null && Items.FindIndex(i => i.Id == previousId) is var idx and >= 0) Cursor = idx;
        else Cursor = Math.Min(Cursor, Math.Max(Items.Count - 1, 0));
        ListChanged?.Invoke();
        CursorChanged?.Invoke();
    }

    private List<FileEntry> Visible(List<FileEntry> list)
    {
        if (Filter.Length == 0 && TypeFilter == null) return list;
        var f = Formatting.Fold(Filter);
        var exts = TypeFilters.FirstOrDefault(t => t.Id == TypeFilter).Exts;
        return list.Where(i => i.IsParent
            || ((Filter.Length == 0 || Formatting.Fold(i.Name).Contains(f))
                && (exts == null || i.IsDirectory || exts.Contains(i.Ext)))).ToList();
    }

    // --- řazení pamatované pro složku -------------------------------------------------

    private static string ViewKey(string dir) => dir.TrimEnd('\\', '/').ToLowerInvariant();

    private void ApplyFolderView(string dir)
    {
        if (Settings.FolderViews.TryGetValue(ViewKey(dir), out var v)) { SortKey = v.Sort; Ascending = v.Ascending; }
        else { SortKey = SortKey.Name; Ascending = true; }
    }

    private void SaveFolderView()
    {
        if (Connection != null || Ephemeral || IsTemp(Path)) return;
        var key = ViewKey(Path);
        if (SortKey == SortKey.Name && Ascending) Settings.FolderViews.Remove(key);
        else Settings.FolderViews[key] = new FolderView { Sort = SortKey, Ascending = Ascending };
        LocationChanged?.Invoke();      // uloží nastavení
    }

    public Task ReloadAsync(bool silent = false)
    {
        if (Connection != null) return LoadRemoteAsync(Connection, RemotePath);
        return LoadLocalAsync(Path, null, silent);
    }

    // --- řazení -----------------------------------------------------------------

    private void SortInPlace(List<FileEntry> list)
    {
        int dir = Ascending ? 1 : -1;
        int Cmp(FileEntry a, FileEntry b)
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            int r = SortKey switch
            {
                SortKey.Size => a.IsDirectory ? 0 : a.Size.CompareTo(b.Size),
                SortKey.Date => Nullable.Compare(a.Modified, b.Modified),
                SortKey.Ext => NaturalComparer.Instance.Compare(a.Ext, b.Ext),
                _ => 0,
            };
            if (r == 0) { r = NaturalComparer.Instance.Compare(a.Name, b.Name); return r; }
            return r * dir;
        }
        // stabilní řazení
        var sorted = list.Select((e, i) => (e, i)).ToList();
        sorted.Sort((x, y) => { int c = Cmp(x.e, y.e); return c != 0 ? c : x.i.CompareTo(y.i); });
        // pro řazení podle názvu sestupně se musí obrátit i název
        if (SortKey == SortKey.Name && !Ascending)
        {
            sorted.Sort((x, y) =>
            {
                if (x.e.IsDirectory != y.e.IsDirectory) return x.e.IsDirectory ? -1 : 1;
                return -NaturalComparer.Instance.Compare(x.e.Name, y.e.Name);
            });
        }
        for (int i = 0; i < sorted.Count; i++) list[i] = sorted[i].e;
    }

    public Task SetSortAsync(SortKey key)
    {
        if (SortKey == key) Ascending = !Ascending; else { SortKey = key; Ascending = true; }
        SaveFolderView();
        return ReloadAsync(true);
    }

    public Task ToggleHiddenAsync() { ShowHidden = !ShowHidden; Settings.ShowHidden = ShowHidden; return ReloadAsync(true); }

    public async Task ToggleBranchAsync()
    {
        if (Connection != null) return;
        Branch = !Branch;
        UnmarkAll();
        await LoadLocalAsync(Path, "", true);
    }

    // --- navigace ---------------------------------------------------------------

    public async Task NavigateAsync(string dir)
    {
        UnmarkAll();
        bool leavingArchive = Archive != null && !dir.StartsWith(Archive.Root, StringComparison.OrdinalIgnoreCase);
        var remote = Connection;
        if (await LoadLocalAsync(dir, "", false))
        {
            if (leavingArchive) DiscardArchive();
            if (remote != null) { remote.Dispose(); }
            StateChanged?.Invoke();
        }
    }

    public async Task GoUpAsync()
    {
        UnmarkAll();
        if (Archive != null && string.Equals(Path.TrimEnd('\\', '/'), Archive.Root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            await LeaveArchiveAsync();
        }
        else if (Connection != null)
        {
            if (RemotePath == "/") return;
            var old = RemotePath;
            await LoadRemoteAsync(Connection, Remote.RemotePath.Parent(old), old);
        }
        else
        {
            var parent = System.IO.Path.GetDirectoryName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent)) return;
            await LoadLocalAsync(parent, Path, false);
        }
    }

    public async Task EnterAsync(Func<FileEntry, Task>? openRemoteFile = null)
    {
        var item = Current;
        if (item == null) return;
        if (item.IsParent) { await GoUpAsync(); return; }
        if (Connection != null)
        {
            if (item.IsDirectory) { UnmarkAll(); await LoadRemoteAsync(Connection, item.FullPath, ""); }
            else if (openRemoteFile != null) await openRemoteFile(item);
            return;
        }
        if (item.IsDirectory) { await NavigateAsync(item.FullPath); return; }
        if (Archive == null && ArchiveService.IsArchive(item.Name)) { await OpenArchiveAsync(item); return; }
        OpenWithShell(item.FullPath);
    }

    public static void OpenWithShell(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* chybu hlásí volající, když ji potřebuje */ }
    }

    public async Task GoBackAsync()
    {
        if (_back.Count == 0) return;
        var prev = _back[^1]; _back.RemoveAt(_back.Count - 1);
        var current = Path;
        _historyMove = true;
        await NavigateAsync(prev);
        _historyMove = false;
        if (string.Equals(Path, prev, StringComparison.OrdinalIgnoreCase)) _forward.Add(current); else _back.Add(prev);
    }

    public async Task GoForwardAsync()
    {
        if (_forward.Count == 0) return;
        var next = _forward[^1]; _forward.RemoveAt(_forward.Count - 1);
        var current = Path;
        _historyMove = true;
        await NavigateAsync(next);
        _historyMove = false;
        if (string.Equals(Path, next, StringComparison.OrdinalIgnoreCase)) _back.Add(current); else _forward.Add(next);
    }

    public async Task ConnectAsync(RemoteSession session)
    {
        var old = Connection;
        if (await LoadRemoteAsync(session, session.StartPath, ""))
        {
            if (old != null && !ReferenceEquals(old, session)) old.Dispose();
        }
    }

    public async Task DisconnectAsync()
    {
        var c = Connection;
        Connection = null;
        RemotePath = "/";
        UnmarkAll();
        c?.Dispose();
        await LoadLocalAsync(Path, "", true);
    }

    // --- archivy jako složky ----------------------------------------------------

    public async Task OpenArchiveAsync(FileEntry item)
    {
        var dest = System.IO.Path.Combine(ArchiveService.TempRoot, Guid.NewGuid().ToString("N"));
        IsLoading = true; StateChanged?.Invoke();
        try
        {
            await Task.Run(() => ArchiveService.Extract(item.FullPath, dest, null, CancellationToken.None));
            var origin = Path;
            UnmarkAll();
            var info = new ArchiveInfo { Name = item.Name, Root = dest, Origin = origin, ItemId = item.Id };
            if (await LoadLocalAsync(dest, "", false)) Archive = info;
            else TryDelete(dest);
        }
        catch (Exception e)
        {
            TryDelete(dest);
            IsLoading = false; StateChanged?.Invoke();
            RaiseError($"Archiv „{item.Name}“ se nepodařilo otevřít:\n{e.Message}", false);
        }
    }

    private async Task LeaveArchiveAsync()
    {
        var a = Archive;
        if (a == null) return;
        Archive = null;
        TryDelete(a.Root);
        await LoadLocalAsync(a.Origin, a.ItemId, false);
    }

    private void DiscardArchive()
    {
        var a = Archive;
        if (a == null) return;
        Archive = null;
        TryDelete(a.Root);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    // --- kurzor, označování, filtr ---------------------------------------------

    public void MoveBy(int delta) => MoveTo(Cursor + delta);

    public void MoveTo(int index)
    {
        if (Items.Count == 0) return;
        int n = Math.Clamp(index, 0, Items.Count - 1);
        if (n == Cursor) return;
        Cursor = n;
        CursorChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void RaiseSummaryOnly() => StateChanged?.Invoke();

    public void RaiseStateChangedForUi() { Generation++; ListChanged?.Invoke(); StateChanged?.Invoke(); }

    public void SetCursorSilently(int index)
    {
        if (index >= 0 && index < Items.Count) Cursor = index;
    }

    public void ToggleMark(bool advance = true)
    {
        var c = Current;
        if (c != null && !c.IsParent) c.IsMarked = !c.IsMarked;
        if (advance) MoveBy(1);
        StateChanged?.Invoke();
    }

    public void MarkAll() { foreach (var i in Items.Where(i => !i.IsParent)) i.IsMarked = true; StateChanged?.Invoke(); }
    public void UnmarkAll() { foreach (var i in AllItems) i.IsMarked = false; StateChanged?.Invoke(); }
    public void InvertMarks() { foreach (var i in Items.Where(i => !i.IsParent)) i.IsMarked = !i.IsMarked; StateChanged?.Invoke(); }

    public void MarkMask(string mask, bool on)
    {
        foreach (var i in Items.Where(i => !i.IsParent && Formatting.MatchesMask(i.Name, mask))) i.IsMarked = on;
        StateChanged?.Invoke();
    }

    /// <summary>Nastaví rychlý filtr podle druhu souboru (null = všechny soubory).</summary>
    public void SetTypeFilter(string? id)
    {
        TypeFilter = string.IsNullOrEmpty(id) ? null : id;
        SetFilter(Filter);
    }

    public void SetFilter(string text)
    {
        Filter = text;
        Items = Visible(AllItems);
        Cursor = Items.FindIndex(i => !i.IsParent) is var idx and >= 0 ? idx : 0;
        Generation++;
        ListChanged?.Invoke();
        CursorChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void QuickSearch(string chars)
    {
        var now = DateTime.UtcNow;
        if ((now - _searchTime).TotalSeconds > 1) _searchBuffer = "";
        _searchTime = now;
        _searchBuffer += chars;
        var f = Formatting.Fold(_searchBuffer);
        var idx = Items.FindIndex(i => !i.IsParent && Formatting.Fold(i.Name).StartsWith(f));
        if (idx >= 0) MoveTo(idx);
    }

    // --- velikosti složek -------------------------------------------------------

    private void CancelSizes()
    {
        _sizesCts?.Cancel();
        _sizesCts = null;
    }

    private void StartAutoSizes()
    {
        if (Ephemeral || !Settings.AutoDirSizes || Connection != null || Branch) return;
        _ = ComputeDirSizesAsync(AllItems.Where(i => i.IsDirectory && !i.IsParent && i.DirSize == null).ToList());
    }

    public async Task ComputeDirSizesAsync(List<FileEntry> dirs)
    {
        if (Connection != null) return;
        CancelSizes();
        var cts = _sizesCts = new CancellationTokenSource();
        foreach (var d in dirs.Where(d => d.IsDirectory && !d.IsParent)) d.SizePending = true;
        try
        {
            foreach (var d in dirs.Where(d => d.IsDirectory && !d.IsParent))
            {
                if (cts.IsCancellationRequested) break;
                var path = d.FullPath;
                var size = await Task.Run(() => LocalFs.TotalSize(path, cts.Token), cts.Token);
                d.DirSize = size;
                d.SizePending = false;
            }
        }
        catch (OperationCanceledException) { }
        finally { foreach (var d in dirs) d.SizePending = false; }
    }

    // --- sledování složky -------------------------------------------------------

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _watchedPath = "";
    }

    private void Watch(string dir)
    {
        if (Ephemeral || IsTemp(dir)) { StopWatching(); return; }
        if (_watcher != null && string.Equals(_watchedPath, dir, StringComparison.OrdinalIgnoreCase) && _watcher.IncludeSubdirectories == Branch) return;
        StopWatching();
        try
        {
            _watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = Branch,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            _watchedPath = dir;
        }
        catch { _watcher = null; }
    }

    private void OnFsEvent(object? sender, FileSystemEventArgs e)
    {
        if (_reloadScheduled) return;
        _reloadScheduled = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            Post(() =>
            {
                _reloadScheduled = false;
                if (Connection == null) _ = LoadLocalAsync(Path, null, true);
            });
        });
    }

    public void Dispose()
    {
        StopWatching();
        CancelSizes();
        DiscardArchive();
    }
}
