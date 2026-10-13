using BudisCommander.Remote;

namespace BudisCommander.Core;

public sealed partial class AppCore
{
    // --- synchronizace na pozadí ----------------------------------------------------------

    private Timer? _syncTimer;
    private int _syncBusy;

    /// <summary>Jednou za minutu zkontroluje, jestli některá úloha synchronizace nemá běžet (jen dokud aplikace běží).</summary>
    public void StartSyncScheduler()
    {
        _syncTimer ??= new Timer(_ => _ = CheckDueSyncTasksAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }

    public async Task CheckDueSyncTasksAsync()
    {
        if (Interlocked.Exchange(ref _syncBusy, 1) == 1) return;
        try
        {
            foreach (var task in Settings.SyncTasks.Where(t => t.IsDue(DateTime.Now)).ToList())
                await RunSyncNowAsync(task);
        }
        finally { Interlocked.Exchange(ref _syncBusy, 0); }
    }

    /// <summary>Spustí úlohu synchronizace, zapíše ji do správce přenosů a uloží výsledek.</summary>
    public async Task<string> RunSyncNowAsync(SyncTask task)
    {
        var rec = new JobRecord { Id = ++_jobId, Title = "Synchronizace: " + task.Name, Added = DateTime.Now, State = JobState.Running, Started = DateTime.Now };
        AddJob(rec);
        string result;
        try
        {
            result = await RunSyncTaskAsync(task, CancellationToken.None);
            rec.State = JobState.Done;
        }
        catch (Exception e)
        {
            result = "Chyba: " + e.Message;
            rec.State = JobState.Failed; rec.Error = e.Message;
        }
        rec.Finished = DateTime.Now;
        task.LastRun = DateTime.Now; task.LastResult = result;
        Settings.Save(_settingsPath);
        JobsChanged?.Invoke();
        ShowNotice($"Synchronizace „{task.Name}“: {result}");
        return result;
    }

    /// <summary>Obousměrná synchronizace: nové a novější soubory jdou na obě strany, nic se nemaže, starší verze přepsaná na místní straně jde do Koše.</summary>
    private async Task<string> RunTwoWayAsync(string source, string dstRoot, TreeMirror.Lister dstList, Func<string, string, string> dstChild,
                                              double tolerance, RemoteSession? session, CancellationToken ct)
    {
        var plan = await TreeMirror.PlanTwoWayAsync(TreeMirror.LocalLister, source, Path.Combine, dstList, dstRoot, dstChild, tolerance, ct);
        if (plan.IsEmpty && plan.Conflicts.Count == 0) return "beze změn";
        int up = 0, down = 0; var errors = new List<string>();

        foreach (var dir in plan.MkdirsA)
            try { Directory.CreateDirectory(dir); } catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Path.GetFileName(dir)}: {e.Message}"); }
        foreach (var dir in plan.MkdirsB)
        {
            ct.ThrowIfCancellationRequested();
            try { if (session != null) await session.MkdirAsync(dir, ct); else Directory.CreateDirectory(dir); }
            catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Path.GetFileName(dir)}: {e.Message}"); }
        }
        foreach (var (from, to, _) in plan.AToB)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (session != null) await session.UploadAsync(from, to, null, ct);
                else CopyKeepingTime(from, to);
                up++;
            }
            catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Path.GetFileName(from)}: {e.Message}"); }
        }
        foreach (var (from, to, size) in plan.BToA)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (session != null)
                {
                    if (File.Exists(to)) FileOps.DeleteToRecycleBin(to);
                    await session.DownloadAsync(from, false, size, to, null, ct);
                }
                else CopyKeepingTime(from, to);
                down++;
            }
            catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Path.GetFileName(from)}: {e.Message}"); }
        }
        var text = $"na cíl {up}, ze cíle {down} souborů ({Formatting.Size(plan.Bytes)})";
        if (plan.Conflicts.Count > 0) text += $", konflikty (přeskočeno) {plan.Conflicts.Count}: {Path.GetFileName(plan.Conflicts[0])}";
        if (errors.Count > 0) text += $", chyb {errors.Count}: {errors[0]}";
        return text;
    }

    /// <summary>Zkopíruje soubor, starší verzi na cíli pošle do Koše a zachová čas změny (jinak by se kopie příště považovala za novější a vracela zpět).</summary>
    private static void CopyKeepingTime(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (File.Exists(to)) FileOps.DeleteToRecycleBin(to);
        File.Copy(from, to, true);
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }

    /// <summary>Provede synchronizaci: zkopíruje nové a změněné soubory, mazání jen když je zapnuté.</summary>
    public async Task<string> RunSyncTaskAsync(SyncTask task, CancellationToken ct)
    {
        var source = task.Source.TrimEnd('\\', '/');
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Zdrojová složka „{source}“ neexistuje.");
        var rclone = SyncTask.ParseRclone(task.Target);
        RemoteSession? session = null;
        try
        {
            string dstRoot;
            TreeMirror.Lister dstList;
            Func<string, string, string> dstChild;
            double tolerance;
            if (rclone is { } rc)
            {
                var exe = Rclone.FindExe(Settings.RclonePath) ?? throw new InvalidOperationException("Program rclone není nainstalovaný.");
                session = new RcloneSession(exe, rc.Remote);
                dstRoot = rc.Path; dstList = TreeMirror.RemoteLister(session); dstChild = RemotePath.Child; tolerance = 120;
                try { await session.MkdirAsync(dstRoot, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            else
            {
                dstRoot = task.Target.TrimEnd('\\', '/');
                if (string.Equals(dstRoot, source, StringComparison.OrdinalIgnoreCase) || dstRoot.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || source.StartsWith(dstRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Zdrojová a cílová složka se nesmí překrývat.");
                Directory.CreateDirectory(dstRoot);
                dstList = TreeMirror.LocalLister; dstChild = Path.Combine; tolerance = 2;
            }

            if (task.TwoWay) return await RunTwoWayAsync(source, dstRoot, dstList, dstChild, tolerance, session, ct);

            var plan = await TreeMirror.PlanAsync(TreeMirror.LocalLister, source, Path.Combine, dstList, dstRoot, dstChild, tolerance, ct);
            if (plan.Copies.Count == 0 && plan.Mkdirs.Count == 0 && (!task.DeleteExtra || plan.Deletes.Count == 0)) return "beze změn";

            int copied = 0, deleted = 0; var errors = new List<string>();
            foreach (var dir in plan.Mkdirs)
            {
                ct.ThrowIfCancellationRequested();
                try { if (session != null) await session.MkdirAsync(dir, ct); else Directory.CreateDirectory(dir); }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Path.GetFileName(dir)}: {e.Message}"); }
            }
            foreach (var (from, to, _) in plan.Copies)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (session != null) await session.UploadAsync(from, to, null, ct);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to, true); }
                    copied++;
                }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Path.GetFileName(from)}: {e.Message}"); }
            }
            if (task.DeleteExtra)
                foreach (var (path, isDir) in plan.Deletes)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (session != null) await session.DeleteAsync(path, isDir, ct);
                        else FileOps.DeleteToRecycleBin(path);
                        deleted++;
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Path.GetFileName(path)}: {e.Message}"); }
                }
            var text = $"zkopírováno {copied} souborů ({Formatting.Size(plan.Bytes)})" + (task.DeleteExtra ? $", smazáno {deleted}" : "");
            if (errors.Count > 0) text += $", chyb {errors.Count}: {errors[0]}";
            return text;
        }
        finally { session?.Dispose(); }
    }
}
