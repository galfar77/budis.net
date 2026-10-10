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
