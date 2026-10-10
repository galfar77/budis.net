using BudisCommander.Remote;

namespace BudisCommander.Core;

public sealed partial class AppCore
{
    // --- zrcadlení mezi místní složkou a serverem --------------------------------------------

    /// <summary>Udělá z cíle (neaktivní panel) kopii aktivního panelu; jeden z panelů je server.</summary>
    private async Task StartServerSyncAsync(PaneState src, PaneState dst)
    {
        if (src.IsArchive || dst.IsArchive) { await Ui.ShowErrorAsync("Archiv se zrcadlit nedá."); return; }
        bool upload = dst.IsRemote;
        var session = (upload ? dst : src).Connection!;
        var srcRoot = src.IsRemote ? src.RemotePath : src.Path.TrimEnd('\\', '/');
        var dstRoot = dst.IsRemote ? dst.RemotePath : dst.Path.TrimEnd('\\', '/');
        TreeMirror.Lister srcList = src.IsRemote ? TreeMirror.RemoteLister(session) : TreeMirror.LocalLister;
        TreeMirror.Lister dstList = dst.IsRemote ? TreeMirror.RemoteLister(session) : TreeMirror.LocalLister;
        Func<string, string, string> srcChild = src.IsRemote ? RemotePath.Child : System.IO.Path.Combine;
        Func<string, string, string> dstChild = dst.IsRemote ? RemotePath.Child : System.IO.Path.Combine;

        Notice = "Porovnávám se serverem…"; StatusChanged?.Invoke();
        MirrorPlan plan;
        try { plan = await TreeMirror.PlanAsync(srcList, srcRoot, srcChild, dstList, dstRoot, dstChild, 120, CancellationToken.None); }
        catch (Exception e) { Notice = null; StatusChanged?.Invoke(); await Ui.ShowErrorAsync(e.Message); return; }
        Notice = null; StatusChanged?.Invoke();
        if (plan.IsEmpty) { ShowNotice("Složky jsou shodné, není co zrcadlit."); return; }

        var trash = upload ? "Smazat na serveru" : "Přesunout do Koše (jen v cíli)";
        var info = $"Kopírovat nebo přepsat: {plan.Copies.Count} souborů ({Formatting.Size(plan.Bytes)})\nVytvořit složek: {plan.Mkdirs.Count}\n{trash}: {plan.Deletes.Count}";
        var preview = plan.Deletes.Take(6).Select(p => "  • " + System.IO.Path.GetFileName(p.Path.TrimEnd('/', '\\'))).ToList();
        if (preview.Count > 0) info += "\n" + string.Join("\n", preview) + (plan.Deletes.Count > 6 ? "\n  …" : "");
        info += "\n\nKopírují se soubory s jinou velikostí nebo novější než v cíli.";
        var title = upload ? "Nahrát změny na server?" : "Stáhnout změny ze serveru?";
        if (!await Ui.ConfirmAsync(title, info, upload ? "Nahrát" : "Stáhnout")) return;

        RunJob(async ct =>
        {
            var errors = new List<string>();
            int total = plan.Deletes.Count + plan.Mkdirs.Count + plan.Copies.Count, step = 0;
            void Tick(string text) { SetProgress((double)step / Math.Max(total, 1), text); step++; }
            string Nm(string p) => System.IO.Path.GetFileName(p.TrimEnd('/', '\\'));
            foreach (var (path, isDir) in plan.Deletes)
            {
                if (ct.IsCancellationRequested) break;
                Tick((upload ? "Mažu: " : "Do Koše: ") + Nm(path));
                try
                {
                    if (upload) await session.DeleteAsync(path, isDir, ct);
                    else await Task.Run(() => FileOps.DeleteToRecycleBin(path), ct);
                }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Nm(path)}: {e.Message}"); }
            }
            foreach (var path in plan.Mkdirs)
            {
                if (ct.IsCancellationRequested) break;
                Tick("Složka: " + Nm(path));
                try { if (upload) await session.MkdirAsync(path, ct); else Directory.CreateDirectory(path); }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Nm(path)}: {e.Message}"); }
            }
            long done = 0;
            foreach (var (from, to, size) in plan.Copies)
            {
                if (ct.IsCancellationRequested) break;
                Tick((upload ? "Nahrávám: " : "Stahuji: ") + Nm(from));
                var baseDone = done;
                var progress = new Progress<(string Text, double Fraction)>(p =>
                    SetProgress(plan.Bytes > 0 ? (baseDone + p.Fraction * size) / plan.Bytes : null, (upload ? "Nahrávám: " : "Stahuji: ") + Nm(from)));
                try
                {
                    if (upload) await session.UploadAsync(from, to, progress, ct);
                    else
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                        await session.DownloadAsync(from, false, size, to, progress, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { errors.Add($"{Nm(from)}: {e.Message}"); }
                done += size;
            }
            SetProgress(null, "");
            await src.ReloadAsync(true); await dst.ReloadAsync(true);
            ShowNotice(ct.IsCancellationRequested ? "Zrcadlení zrušeno." : "Zrcadlení hotovo.");
            if (errors.Count > 0) await Ui.ShowErrorAsync(string.Join("\n", errors.Take(12)));
        });
    }
}
