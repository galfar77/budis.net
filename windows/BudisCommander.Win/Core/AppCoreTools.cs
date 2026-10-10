using System.Diagnostics;
using BudisCommander.Remote;

namespace BudisCommander.Core;

public sealed partial class AppCore
{
    // --- porovnání a zrcadlení adresářů ---------------------------------------------

    private enum MarkSide { A, B, Both }

    /// <summary>Označí v obou panelech soubory, které chybí v druhém panelu nebo jsou novější či jiné.</summary>
    public async Task CompareAsync(bool byContent)
    {
        var a = Left.Current; var b = Right.Current;
        a.SetFilter(""); b.SetFilter("");
        bool content = byContent && !a.IsRemote && !b.IsRemote;
        var fa = a.AllItems.Where(i => !i.IsParent).GroupBy(i => i.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var fb = b.AllItems.Where(i => !i.IsParent).GroupBy(i => i.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        double tolerance = a.IsRemote || b.IsRemote ? 120 : 2;
        var markA = new HashSet<string>(); var markB = new HashSet<string>();
        var toCheck = new List<(FileEntry X, FileEntry Y, MarkSide Side)>();

        foreach (var (name, x) in fa)
        {
            if (!fb.TryGetValue(name, out var y)) { markA.Add(x.Id); continue; }
            if (x.IsDirectory || y.IsDirectory) continue;
            if (x.Modified is { } dx && y.Modified is { } dy && Math.Abs((dx - dy).TotalSeconds) > tolerance)
            {
                if (content && x.Size == y.Size) toCheck.Add((x, y, dx > dy ? MarkSide.A : MarkSide.B));
                else if (dx > dy) markA.Add(x.Id); else markB.Add(y.Id);
            }
            else if (x.Size != y.Size) { markA.Add(x.Id); markB.Add(y.Id); }
            else if (content) toCheck.Add((x, y, MarkSide.Both));
        }
        foreach (var (name, y) in fb) if (!fa.ContainsKey(name)) markB.Add(y.Id);

        if (toCheck.Count > 0)
        {
            Notice = $"Porovnávám obsah {toCheck.Count} souborů…"; StatusChanged?.Invoke();
            var pairs = toCheck.Select(c => (c.X.FullPath, c.Y.FullPath)).ToList();
            var differs = await Task.Run(() => pairs.Select(p => !LocalFs.SameContent(p.Item1, p.Item2)).ToList());
            Notice = null;
            for (int i = 0; i < differs.Count; i++)
            {
                if (!differs[i]) continue;
                var c = toCheck[i];
                if (c.Side is MarkSide.A or MarkSide.Both) markA.Add(c.X.Id);
                if (c.Side is MarkSide.B or MarkSide.Both) markB.Add(c.Y.Id);
            }
        }
        foreach (var i in a.AllItems) i.IsMarked = markA.Contains(i.Id);
        foreach (var i in b.AllItems) i.IsMarked = markB.Contains(i.Id);
        ShowNotice($"Porovnání: vlevo označeno {markA.Count}, vpravo {markB.Count} (chybějící, novější nebo jiné). Zkopírujte je klávesou F5.");
        a.RaiseStateChangedForUi(); b.RaiseStateChangedForUi();
    }

    /// <summary>Udělá z cílového (neaktivního) panelu přesnou kopii aktivního; přebytečné soubory v cíli přesune do Koše.</summary>
    public async Task StartSyncAsync()
    {
        var src = Active; var dst = Other;
        if (src.IsRemote || dst.IsRemote) { await Ui.ShowErrorAsync("Zrcadlení funguje jen mezi lokálními složkami."); return; }
        if (dst.IsArchive) { await Ui.ShowErrorAsync("Archiv je otevřený jen pro čtení."); return; }
        var s = src.Path.TrimEnd('\\', '/'); var d = dst.Path.TrimEnd('\\', '/');
        if (string.Equals(s, d, StringComparison.OrdinalIgnoreCase) ||
            d.StartsWith(s + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith(d + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            await Ui.ShowErrorAsync("Zdrojová a cílová složka se nesmí překrývat.");
            return;
        }
        Notice = "Počítám rozdíly…"; StatusChanged?.Invoke();
        var plan = await Task.Run(() => SyncPlan.Make(src.Path, dst.Path));
        Notice = null; StatusChanged?.Invoke();
        if (plan.IsEmpty) { ShowNotice("Složky jsou shodné, není co zrcadlit."); return; }
        var info = $"Kopírovat nebo přepsat: {plan.Copies.Count} souborů ({Formatting.Size(plan.Bytes)})\nVytvořit složek: {plan.Mkdirs.Count}\n"
                 + $"Přesunout do Koše (jen v cíli): {plan.Deletes.Count}";
        var preview = plan.Deletes.Take(6).Select(p => "  • " + System.IO.Path.GetFileName(p)).ToList();
        if (preview.Count > 0) info += "\n" + string.Join("\n", preview) + (plan.Deletes.Count > 6 ? "\n  …" : "");
        info += "\n\nCíl bude přesně odpovídat zdroji, i když je v něm některý soubor novější.";
        if (!await Ui.ConfirmAsync($"Zrcadlit „{System.IO.Path.GetFileName(s)}“ do „{System.IO.Path.GetFileName(d)}“?", info, "Zrcadlit")) return;

        RunJob(async ct =>
        {
            var errors = new List<string>();
            int total = plan.Deletes.Count + plan.Mkdirs.Count + plan.Copies.Count, step = 0;
            void Tick(string text) { SetProgress((double)step / Math.Max(total, 1), text); step++; }
            foreach (var path in plan.Deletes)
            {
                if (ct.IsCancellationRequested) break;
                Tick("Do Koše: " + System.IO.Path.GetFileName(path));
                try { await Task.Run(() => FileOps.DeleteToRecycleBin(path), ct); } catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{System.IO.Path.GetFileName(path)}: {e.Message}"); }
            }
            foreach (var path in plan.Mkdirs)
            {
                if (ct.IsCancellationRequested) break;
                Tick("Složka: " + System.IO.Path.GetFileName(path));
                try { Directory.CreateDirectory(path); } catch (Exception e) { errors.Add($"{System.IO.Path.GetFileName(path)}: {e.Message}"); }
            }
            foreach (var (from, to) in plan.Copies)
            {
                if (ct.IsCancellationRequested) break;
                Tick("Kopíruji: " + System.IO.Path.GetFileName(from));
                try { await FileOps.CopyAsync(from, to, true, null, ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { errors.Add($"{System.IO.Path.GetFileName(from)}: {e.Message}"); }
            }
            SetProgress(null, "");
            await src.ReloadAsync(true); await dst.ReloadAsync(true);
            ShowNotice(ct.IsCancellationRequested ? "Zrcadlení zrušeno." : "Zrcadlení hotovo.");
            if (errors.Count > 0) await Ui.ShowErrorAsync(string.Join("\n", errors.Take(12)));
        });
    }

    // --- archivy -----------------------------------------------------------------------

    private string DestinationDir(PaneState src) => !Other.IsRemote && !Other.IsArchive ? Other.Path : src.Path;

    public async Task PackAsync()
    {
        var src = Active;
        if (src.IsRemote) { await Ui.ShowErrorAsync("Balení funguje jen na lokálním disku. Soubory ze serveru nejdřív zkopírujte (F5)."); return; }
        var items = src.Targets();
        if (items.Count == 0) return;
        var destDir = DestinationDir(src);
        var baseName = items.Count == 1 ? items[0].Name : (System.IO.Path.GetFileName(src.Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "Archiv");
        var name = await Ui.PromptAsync("Zabalit do archivu", $"Do: {destDir}\nPodporováno: .zip, .tar.gz", baseName + ".zip", "Zabalit");
        if (string.IsNullOrWhiteSpace(name)) return;
        var lower = name.ToLowerInvariant();
        if (!(lower.EndsWith(".zip") || lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")))
        {
            await Ui.ShowErrorAsync("Název archivu musí končit na .zip, .tar.gz nebo .tgz.");
            return;
        }
        var dest = System.IO.Path.Combine(destDir, name);
        if (File.Exists(dest))
        {
            if (!await Ui.ConfirmAsync($"Archiv „{name}“ už existuje. Přepsat?", "", "Přepsat")) return;
            File.Delete(dest);
        }
        var paths = items.Select(i => i.FullPath).ToList();
        var baseDir = src.Path;
        RunJob(async ct =>
        {
            try
            {
                SetProgress(0, "Balím " + name + "…");
                var progress = new Progress<(string Text, double Fraction)>(p => SetProgress(p.Fraction, "Balím " + p.Text));
                await Task.Run(() => ArchiveService.Create(dest, baseDir, paths, progress, ct), ct);
                ShowNotice("Archiv vytvořen: " + name);
            }
            catch (OperationCanceledException) { try { File.Delete(dest); } catch { } ShowNotice("Balení zrušeno."); }
            catch (Exception e) { try { File.Delete(dest); } catch { } await Ui.ShowErrorAsync(e.Message); }
            foreach (var i in src.AllItems) i.IsMarked = false;
            await src.ReloadAsync(true); await Other.ReloadAsync(true);
        });
    }

    public async Task UnpackAsync()
    {
        var src = Active;
        var item = src.Current;
        if (src.IsRemote || item == null || item.IsParent || item.IsDirectory)
        {
            await Ui.ShowErrorAsync("Vyberte lokální soubor s archivem (zip, tar, tar.gz, 7z, rar…).");
            return;
        }
        var destDir = DestinationDir(src);
        if (!await Ui.ConfirmAsync($"Rozbalit „{item.Name}“?", "Do: " + destDir, "Rozbalit")) return;
        string? password = null;
        if (await Task.Run(() => ArchiveService.IsEncrypted(item.FullPath)))
        {
            password = await Ui.PromptPasswordAsync("Archiv je chráněný heslem", item.Name);
            if (password == null) return;
        }
        var file = item.FullPath;
        RunJob(async ct =>
        {
            try
            {
                SetProgress(0, "Rozbaluji " + item.Name + "…");
                await Task.Run(() => ArchiveService.Extract(file, destDir, password, ct), ct);
                ShowNotice("Rozbaleno do " + System.IO.Path.GetFileName(destDir.TrimEnd('\\', '/')));
            }
            catch (OperationCanceledException) { ShowNotice("Rozbalování zrušeno."); }
            catch (Exception e) { await Ui.ShowErrorAsync(e.Message); }
            await src.ReloadAsync(true); await Other.ReloadAsync(true);
        });
    }

    public async Task AddToArchiveAsync()
    {
        var src = Active; var dst = Other;
        var archive = dst.Current;
        if (src.IsRemote || src.IsArchive || dst.IsRemote || dst.IsArchive || archive == null || archive.IsDirectory || archive.IsParent
            || !archive.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await Ui.ShowErrorAsync("Označte soubory v aktivním panelu a v druhém panelu postavte kurzor na archiv .zip.");
            return;
        }
        var items = src.Targets();
        if (items.Count == 0) return;
        if (!await Ui.ConfirmAsync($"Přidat {Describe(items)} do „{archive.Name}“?", "", "Přidat")) return;
        var paths = items.Select(i => i.FullPath).ToList();
        var baseDir = src.Path; var zip = archive.FullPath;
        RunJob(async ct =>
        {
            try
            {
                SetProgress(0, "Přidávám do " + archive.Name + "…");
                await Task.Run(() => ArchiveService.AddToZip(zip, baseDir, paths, ct), ct);
                ShowNotice("Přidáno do archivu: " + archive.Name);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { await Ui.ShowErrorAsync(e.Message); }
            await src.ReloadAsync(true); await dst.ReloadAsync(true);
        });
    }

    // --- dělení a slepování souborů, odkazy ---------------------------------------------------

    public async Task StartSplitAsync()
    {
        var src = Active;
        var item = src.Current;
        if (src.IsRemote || item == null || item.IsDirectory || item.IsParent)
        {
            await Ui.ShowErrorAsync("Vyberte lokální soubor, který chcete rozdělit.");
            return;
        }
        var dir = DestinationDir(src);
        var text = await Ui.PromptAsync($"Rozdělit „{item.Name}“", $"Velikost jednoho dílu v MB (např. 100, 700, 4000).\nDíly se uloží do {dir}", "100", "Rozdělit");
        if (text == null) return;
        if (!double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb) || mb < 0.001)
        {
            await Ui.ShowErrorAsync("Zadejte velikost dílu jako číslo v MB.");
            return;
        }
        long partSize = (long)(mb * 1_048_576);
        var file = item.FullPath;
        RunJob(async ct =>
        {
            try
            {
                SetProgress(0, "Dělím " + item.Name + "…");
                int count = await Task.Run(() => LocalFs.Split(file, partSize, dir, f => SetProgressThreadSafe(f, "Dělím " + item.Name + "…"), ct), ct);
                ShowNotice($"Soubor rozdělen na {count} dílů.");
            }
            catch (OperationCanceledException) { ShowNotice("Dělení zrušeno."); }
            catch (Exception e) { await Ui.ShowErrorAsync(e.Message); }
            await src.ReloadAsync(true); await Other.ReloadAsync(true);
        });
    }

    public async Task StartCombineAsync()
    {
        var src = Active;
        var item = src.Current;
        if (src.IsRemote || item == null || item.IsDirectory || item.IsParent || !item.Name.EndsWith(".001"))
        {
            await Ui.ShowErrorAsync("Postavte kurzor na první díl souboru (název končí na .001).");
            return;
        }
        var dir = DestinationDir(src);
        var target = item.Name[..^4];
        var dest = System.IO.Path.Combine(dir, target);
        if (File.Exists(dest) && !await Ui.ConfirmAsync($"„{target}“ už existuje. Přepsat?", "", "Přepsat")) return;
        var first = item.FullPath;
        RunJob(async ct =>
        {
            try
            {
                SetProgress(0, "Slepuji " + target + "…");
                await Task.Run(() => LocalFs.Combine(first, dest, f => SetProgressThreadSafe(f, "Slepuji " + target + "…"), ct), ct);
                ShowNotice("Soubor slepen: " + target);
            }
            catch (OperationCanceledException) { ShowNotice("Slepování zrušeno."); }
            catch (Exception e) { await Ui.ShowErrorAsync(e.Message); }
            await src.ReloadAsync(true); await Other.ReloadAsync(true);
        });
    }

    private void SetProgressThreadSafe(double fraction, string text)
    {
        // volá se z pracovního vlákna; změna se jen zaznamená, rozhraní si ji načte při dalším překreslení
        Progress = fraction;
        ProgressText = text;
        StatusChanged?.Invoke();
    }

    public async Task MakeSymlinkAsync()
    {
        var src = Active;
        var item = src.Current;
        if (src.IsRemote || item == null || item.IsParent)
        {
            await Ui.ShowErrorAsync("Symbolický odkaz jde vytvořit jen na lokální soubor nebo složku.");
            return;
        }
        var dir = DestinationDir(src);
        var name = await Ui.PromptAsync($"Symbolický odkaz na „{item.Name}“", "Vytvoří se v " + dir + "\nWindows může vyžadovat Režim pro vývojáře nebo práva správce.", item.Name + " odkaz", "Vytvořit");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var link = System.IO.Path.Combine(dir, name.Trim());
            if (item.IsDirectory) Directory.CreateSymbolicLink(link, item.FullPath); else File.CreateSymbolicLink(link, item.FullPath);
            await src.ReloadAsync(true); await Other.ReloadAsync(true);
        }
        catch (Exception e) { await Ui.ShowErrorAsync(e.Message); }
    }

    // --- atributy a velikosti složek ----------------------------------------------------

    public async Task ApplyAttributesAsync(LocalFs.AttributeChange change, IEnumerable<string> paths)
    {
        Notice = "Nastavuji atributy…"; StatusChanged?.Invoke();
        var list = paths.ToList();
        var errors = await Task.Run(() => LocalFs.ApplyAttributes(change, list));
        Notice = null;
        await Active.ReloadAsync(true); await Other.ReloadAsync(true);
        if (errors.Count == 0) ShowNotice("Atributy nastaveny."); else await Ui.ShowErrorAsync(string.Join("\n", errors.Take(10)));
    }

    public Task CalcDirSizesAsync()
    {
        var pane = Active;
        if (pane.IsRemote) return Task.CompletedTask;
        var dirs = pane.Targets().Where(i => i.IsDirectory).ToList();
        if (dirs.Count == 0) dirs = pane.Items.Where(i => i.IsDirectory && !i.IsParent).ToList();
        return dirs.Count == 0 ? Task.CompletedTask : pane.ComputeDirSizesAsync(dirs);
    }

    // --- prohlížení, úpravy souborů na serveru --------------------------------------------

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" };
    public const int ViewerLimit = 512 * 1024;

    public async Task<ViewerData?> LoadViewerAsync(PaneState pane, FileEntry item)
    {
        var ext = System.IO.Path.GetExtension(item.Name);
        bool isImage = ImageExts.Contains(ext);
        bool isPdf = string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase);
        string file = item.FullPath;

        if (pane.IsRemote)
        {
            var conn = pane.Connection!;
            if ((isImage || isPdf) && item.Size > 100 * 1024 * 1024)
            {
                await Ui.ShowErrorAsync("Soubor je příliš velký pro náhled (nad 100 MB). Zkopírujte ho do lokálního panelu.");
                return null;
            }
            try
            {
                Notice = "Stahuji náhled " + item.Name + "…"; StatusChanged?.Invoke();
                if (isImage || isPdf)
                {
                    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BudisView", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmp);
                    file = System.IO.Path.Combine(tmp, item.Name);
                    await conn.DownloadAsync(item.FullPath, false, item.Size, file, null, CancellationToken.None);
                }
                else
                {
                    var head = await conn.ReadHeadAsync(item.FullPath, ViewerLimit, CancellationToken.None);
                    Notice = null; StatusChanged?.Invoke();
                    return FromBytes(item.Name, head, null);
                }
            }
            catch (Exception e) { Notice = null; await Ui.ShowErrorAsync(e.Message); return null; }
            finally { Notice = null; StatusChanged?.Invoke(); }
        }
        if (isPdf) return new ViewerData(item.Name, ViewerKind.External, null, file, file);
        if (isImage) return new ViewerData(item.Name, ViewerKind.Image, null, file, null);
        try
        {
            byte[] data;
            await using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[ViewerLimit];
                int n = 0, r;
                while (n < buf.Length && (r = await fs.ReadAsync(buf.AsMemory(n))) > 0) n += r;
                data = buf.AsSpan(0, n).ToArray();
            }
            return FromBytes(item.Name, data, pane.IsRemote ? null : file);
        }
        catch (Exception e) { await Ui.ShowErrorAsync(e.Message); return null; }
    }

    public static ViewerData FromBytes(string name, byte[] data, string? openPath)
    {
        if (LocalFs.LooksBinary(data))
            return new ViewerData(name + " (hex)", ViewerKind.Hex, LocalFs.HexDump(data.AsSpan(0, Math.Min(data.Length, 64 * 1024))), null, openPath);
        return new ViewerData(name, ViewerKind.Text, DecodeText(data), null, openPath);
    }

    private static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return System.Text.Encoding.UTF8.GetString(data, 3, data.Length - 3);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return System.Text.Encoding.Unicode.GetString(data, 2, data.Length - 2);
        try { return new System.Text.UTF8Encoding(false, true).GetString(data); }
        catch { return Encodings.Ansi().GetString(data); }
    }

    public static void OpenEditor(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "edit" });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "notepad.exe" : "xdg-open", "\"" + file + "\"") { UseShellExecute = true }); }
            catch { }
        }
    }

    /// <summary>Lokální soubor otevře v editoru; soubor na serveru stáhne a po každém uložení nahraje zpět.</summary>
    public async Task EditAsync()
    {
        var pane = Active;
        var item = pane.Current;
        if (item == null || item.IsParent || item.IsDirectory) return;
        if (!pane.IsRemote) { OpenEditor(item.FullPath); return; }

        var conn = pane.Connection!;
        var key = conn.DisplayName + item.FullPath;
        if (_editWatchers.TryGetValue(key, out var old)) old.Cancel();
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BudisEdit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var file = System.IO.Path.Combine(tmp, item.Name);
        try
        {
            Notice = "Stahuji " + item.Name + "…"; StatusChanged?.Invoke();
            await conn.DownloadAsync(item.FullPath, false, item.Size, file, null, CancellationToken.None);
        }
        catch (Exception e) { Notice = null; await Ui.ShowErrorAsync(e.Message); return; }
        ShowNotice($"Editace „{item.Name}“: po uložení se změny nahrají na server");
        OpenEditor(file);

        var cts = new CancellationTokenSource();
        _editWatchers[key] = cts;
        var remotePath = item.FullPath;
        var dir = RemotePath.Parent(remotePath);
        _ = Task.Run(async () =>
        {
            var last = File.GetLastWriteTimeUtc(file);
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
                if (cts.IsCancellationRequested) break;
                var now = File.GetLastWriteTimeUtc(file);
                if (now == last) continue;
                await Task.Delay(300);
                last = File.GetLastWriteTimeUtc(file);
                try
                {
                    ShowNotice($"Nahrávám změny „{item.Name}“ na server…");
                    await conn.UploadAsync(file, remotePath, null, CancellationToken.None);
                    ShowNotice("Uloženo na server: " + item.Name);
                    if (ReferenceEquals(pane.Connection, conn) && pane.RemotePath == dir) await pane.ReloadAsync();
                }
                catch (Exception e) { await Ui.ShowErrorAsync($"Změny „{item.Name}“ se nepodařilo nahrát:\n{e.Message}"); }
            }
        });
    }

    public async Task OpenRemoteFileAsync(FileEntry item)
    {
        var pane = Active;
        var conn = pane.Connection;
        if (conn == null) return;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BudisOpen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var file = System.IO.Path.Combine(tmp, item.Name);
        try
        {
            Notice = "Stahuji " + item.Name + "…"; StatusChanged?.Invoke();
            await conn.DownloadAsync(item.FullPath, false, item.Size, file, null, CancellationToken.None);
            PaneState.OpenWithShell(file);
        }
        catch (Exception e) { await Ui.ShowErrorAsync(e.Message); }
        finally { Notice = null; StatusChanged?.Invoke(); }
    }

    // --- uživatelské příkazy a různé ----------------------------------------------------------

    public string ExpandCommand(string template)
    {
        string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
        var pane = Active;
        var cur = pane.Current is { IsParent: false } c ? c : null;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '%' || i + 1 >= template.Length) { sb.Append(template[i]); continue; }
            char n = template[++i];
            switch (n)
            {
                case 'f': sb.Append(Q(cur?.FullPath ?? "")); break;
                case 'n': sb.Append(Q(cur?.Name ?? "")); break;
                case 'd': sb.Append(Q(pane.Path)); break;
                case 'o': sb.Append(Q(Other.IsRemote ? pane.Path : Other.Path)); break;
                case 'F': sb.Append(string.Join(" ", pane.Targets().Select(t => Q(t.FullPath)))); break;
                case '%': sb.Append('%'); break;
                default: sb.Append('%').Append(n); break;
            }
        }
        return sb.ToString();
    }

    public async Task<string?> RunUserCommandPrepareAsync(UserCommand cmd)
    {
        if (Active.IsRemote) { await Ui.ShowErrorAsync("Uživatelské příkazy fungují jen v lokálních složkách."); return null; }
        var text = ExpandCommand(cmd.Command);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Otevře složku nalezeného souboru v aktivním panelu a vybere ho.</summary>
    public async Task RevealAsync(string path)
    {
        var pane = Active;
        if (pane.IsRemote)
        {
            await pane.LoadRemoteAsync(pane.Connection!, RemotePath.Parent(path), path);
            return;
        }
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir == null) return;
        await pane.NavigateAsync(dir);
        var idx = pane.Items.FindIndex(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) pane.MoveTo(idx);
    }

    public Task TrashPathsAsync(IEnumerable<string> paths) => Task.Run(() =>
    {
        foreach (var p in paths) FileOps.DeleteToRecycleBin(p);
    });
}
