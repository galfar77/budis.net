namespace BudisCommander.Core;

/// <summary>Plán zrcadlení: co zkopírovat, jaké složky vytvořit a co v cíli odstranit.</summary>
public sealed class SyncPlan
{
    public List<string> Mkdirs { get; } = new();
    public List<(string From, string To)> Copies { get; } = new();
    public List<string> Deletes { get; } = new();
    public long Bytes { get; set; }
    public bool IsEmpty => Mkdirs.Count == 0 && Copies.Count == 0 && Deletes.Count == 0;

    public static SyncPlan Make(string src, string dst, CancellationToken ct = default)
    {
        var plan = new SyncPlan();
        Walk(src, dst, plan, ct);
        return plan;
    }

    private static Dictionary<string, FileSystemInfo> Entries(string dir)
    {
        var map = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return map;
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", LocalFs.ListOptions)) map[e.Name] = e;
        return map;
    }

    private static void Walk(string src, string dst, SyncPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var s = Entries(src); var d = Entries(dst);
        foreach (var (name, a) in s)
        {
            var target = Path.Combine(dst, name);
            bool aDir = (a.Attributes & FileAttributes.Directory) != 0;
            d.TryGetValue(name, out var b);
            bool bDir = b != null && (b.Attributes & FileAttributes.Directory) != 0;
            if (aDir)
            {
                if (b == null || !bDir)
                {
                    if (b != null) plan.Deletes.Add(target);
                    plan.Mkdirs.Add(target);
                }
                Walk(a.FullName, target, plan, ct);
            }
            else
            {
                var fa = (FileInfo)a;
                if (b != null && !bDir)
                {
                    var fb = (FileInfo)b;
                    bool sameTime = Math.Abs((fa.LastWriteTimeUtc - fb.LastWriteTimeUtc).TotalSeconds) <= 2;
                    if (fa.Length == fb.Length && sameTime) continue;
                }
                else if (b != null) plan.Deletes.Add(target);
                plan.Copies.Add((fa.FullName, target));
                plan.Bytes += fa.Length;
            }
        }
        foreach (var (name, b) in d)
            if (!s.ContainsKey(name)) plan.Deletes.Add(b.FullName);
    }
}
