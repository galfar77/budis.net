using BudisCommander.Remote;

namespace BudisCommander.Core;

/// <summary>Plán zrcadlení mezi místní složkou a serverem (obě strany se čtou přes <see cref="TreeMirror.Lister"/>).</summary>
public sealed class MirrorPlan
{
    public List<string> Mkdirs { get; } = new();
    public List<(string From, string To, long Size)> Copies { get; } = new();
    public List<(string Path, bool IsDirectory)> Deletes { get; } = new();
    public long Bytes { get; set; }
    public bool IsEmpty => Mkdirs.Count == 0 && Copies.Count == 0 && Deletes.Count == 0;
}

/// <summary>Plán obousměrné synchronizace: co kam zkopírovat; nic se nemaže a konflikty se jen hlásí.</summary>
public sealed class TwoWayPlan
{
    public List<string> MkdirsA { get; } = new();
    public List<string> MkdirsB { get; } = new();
    public List<(string From, string To, long Size)> AToB { get; } = new();
    public List<(string From, string To, long Size)> BToA { get; } = new();
    /// <summary>Položky, které se nedají rozhodnout (stejný čas, ale jiná velikost; soubor proti složce).</summary>
    public List<string> Conflicts { get; } = new();
    public long Bytes { get; set; }
    public bool IsEmpty => MkdirsA.Count == 0 && MkdirsB.Count == 0 && AToB.Count == 0 && BToA.Count == 0;
}

public static class TreeMirror
{
    public delegate Task<List<FileEntry>> Lister(string path, CancellationToken ct);

    public static Lister LocalLister => (path, ct) => Task.Run(() =>
    {
        var list = new List<FileEntry>();
        if (!Directory.Exists(path)) return list;
        foreach (var e in new DirectoryInfo(path).EnumerateFileSystemInfos("*", LocalFs.ListOptions))
        {
            bool dir = (e.Attributes & FileAttributes.Directory) != 0;
            list.Add(new FileEntry
            {
                Name = e.Name, FullPath = e.FullName, IsDirectory = dir,
                Size = e is FileInfo f ? f.Length : 0, Modified = e.LastWriteTimeUtc, Attributes = e.Attributes,
            });
        }
        return list;
    }, ct);

    public static Lister RemoteLister(RemoteSession session) => async (path, ct) =>
    {
        try { return (await session.ListAsync(path, ct)).Where(e => !e.IsParent).ToList(); }
        catch (Exception) when (!ct.IsCancellationRequested) { return new List<FileEntry>(); }
    };

    /// <summary>
    /// Spočítá, co zkopírovat ze zdroje do cíle a co v cíli přebývá. Protože server si čas souboru po nahrání
    /// nastaví po svém, kopíruje se, jen když se liší velikost nebo je zdroj novější než cíl o víc než <paramref name="toleranceSeconds"/>.
    /// </summary>
    public static async Task<MirrorPlan> PlanAsync(Lister src, string srcRoot, Func<string, string, string> srcChild,
                                                   Lister dst, string dstRoot, Func<string, string, string> dstChild,
                                                   double toleranceSeconds, CancellationToken ct)
    {
        var plan = new MirrorPlan();
        await WalkAsync(src, srcRoot, srcChild, dst, dstRoot, dstChild, toleranceSeconds, plan, true, ct);
        return plan;
    }

    /// <summary>
    /// Obousměrné porovnání: soubor jen na jedné straně se zkopíruje na druhou, soubor na obou stranách se přepíše tou
    /// novější verzí (rozdíl času větší než <paramref name="toleranceSeconds"/>). Nic se nemaže.
    /// </summary>
    public static async Task<TwoWayPlan> PlanTwoWayAsync(Lister a, string aRoot, Func<string, string, string> aChild,
                                                         Lister b, string bRoot, Func<string, string, string> bChild,
                                                         double toleranceSeconds, CancellationToken ct)
    {
        var plan = new TwoWayPlan();
        await WalkTwoWayAsync(a, aRoot, aChild, true, b, bRoot, bChild, true, toleranceSeconds, plan, ct);
        return plan;
    }

    private static async Task WalkTwoWayAsync(Lister a, string aDir, Func<string, string, string> aChild, bool aExists,
                                              Lister b, string bDir, Func<string, string, string> bChild, bool bExists,
                                              double tol, TwoWayPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var al = aExists ? await a(aDir, ct) : new List<FileEntry>();
        var bl = bExists ? await b(bDir, ct) : new List<FileEntry>();
        var am = al.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var bm = bl.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var names = am.Keys.Concat(bm.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var name in names)
        {
            am.TryGetValue(name, out var ea); bm.TryGetValue(name, out var eb);
            var pa = aChild(aDir, ea?.Name ?? name); var pb = bChild(bDir, eb?.Name ?? name);
            if (ea != null && eb != null)
            {
                if (ea.IsDirectory && eb.IsDirectory) { await WalkTwoWayAsync(a, pa, aChild, true, b, pb, bChild, true, tol, plan, ct); continue; }
                if (ea.IsDirectory != eb.IsDirectory) { plan.Conflicts.Add(pa); continue; }
                double? diff = ea.Modified is { } ma && eb.Modified is { } mb ? (ma - mb).TotalSeconds : null;
                if (ea.Size == eb.Size && diff is { } d0 && Math.Abs(d0) <= tol) continue;
                if (diff is { } d && d > tol) { plan.AToB.Add((pa, pb, ea.Size)); plan.Bytes += ea.Size; }
                else if (diff is { } e && e < -tol) { plan.BToA.Add((pb, pa, eb.Size)); plan.Bytes += eb.Size; }
                else if (ea.Size != eb.Size || diff == null) plan.Conflicts.Add(pa);
            }
            else if (ea != null)
            {
                if (ea.IsDirectory)
                {
                    plan.MkdirsB.Add(pb);
                    await WalkTwoWayAsync(a, pa, aChild, true, b, pb, bChild, false, tol, plan, ct);
                }
                else { plan.AToB.Add((pa, pb, ea.Size)); plan.Bytes += ea.Size; }
            }
            else if (eb != null)
            {
                if (eb.IsDirectory)
                {
                    plan.MkdirsA.Add(pa);
                    await WalkTwoWayAsync(a, pa, aChild, false, b, pb, bChild, true, tol, plan, ct);
                }
                else { plan.BToA.Add((pb, pa, eb.Size)); plan.Bytes += eb.Size; }
            }
        }
    }

    private static async Task WalkAsync(Lister src, string srcDir, Func<string, string, string> srcChild,
                                        Lister dst, string dstDir, Func<string, string, string> dstChild,
                                        double tol, MirrorPlan plan, bool dstExists, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var s = await src(srcDir, ct);
        var d = dstExists ? await dst(dstDir, ct) : new List<FileEntry>();
        var dmap = d.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var smap = new HashSet<string>(s.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var a in s)
        {
            var from = srcChild(srcDir, a.Name);
            var to = dstChild(dstDir, a.Name);
            dmap.TryGetValue(a.Name, out var b);
            if (a.IsDirectory)
            {
                bool exists = b is { IsDirectory: true };
                if (!exists)
                {
                    if (b != null) plan.Deletes.Add((to, false));
                    plan.Mkdirs.Add(to);
                }
                await WalkAsync(src, from, srcChild, dst, to, dstChild, tol, plan, exists, ct);
            }
            else
            {
                if (b is { IsDirectory: false })
                {
                    bool newer = a.Modified is { } ma && b.Modified is { } mb && (ma - mb).TotalSeconds > tol;
                    if (a.Size == b.Size && !newer) continue;
                }
                else if (b != null) plan.Deletes.Add((to, true));
                plan.Copies.Add((from, to, a.Size));
                plan.Bytes += a.Size;
            }
        }
        foreach (var b in d)
            if (!smap.Contains(b.Name)) plan.Deletes.Add((dstChild(dstDir, b.Name), b.IsDirectory));
    }
}
