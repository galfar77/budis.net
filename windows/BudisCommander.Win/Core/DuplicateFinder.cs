namespace BudisCommander.Core;

public sealed record DupGroup(long Size, List<string> Files);

/// <summary>Hledá soubory se stejným obsahem: nejdřív podle velikosti, pak podle prvních 64 kB, nakonec podle SHA-256.</summary>
public static class DuplicateFinder
{
    public const int FileLimit = 300_000;

    public static List<DupGroup> Scan(IEnumerable<string> roots, long minSize, bool hidden,
                                      IProgress<string>? progress, CancellationToken ct)
    {
        var bySize = new Dictionary<long, List<string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!hidden && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    long size = f.Length;
                    if (size <= 0 || size < minSize || !seen.Add(f.FullName)) continue;
                    if (!bySize.TryGetValue(size, out var list)) bySize[size] = list = new List<string>();
                    list.Add(f.FullName);
                    if (++count % 500 == 0) progress?.Report($"Prohledáno souborů: {count}");
                    if (count >= FileLimit) goto done;
                }
                catch { }
            }
        }
    done:
        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        var result = new List<DupGroup>();
        int doneCount = 0;
        foreach (var (size, files) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Porovnávám obsah ({++doneCount}/{candidates.Count})…");
            var byPartial = new Dictionary<string, List<string>>();
            foreach (var f in files)
            {
                try
                {
                    var h = LocalFs.Sha256Of(f, 64 * 1024, ct);
                    if (!byPartial.TryGetValue(h, out var l)) byPartial[h] = l = new List<string>();
                    l.Add(f);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            foreach (var partial in byPartial.Values.Where(l => l.Count > 1))
            {
                var byFull = new Dictionary<string, List<string>>();
                foreach (var f in partial)
                {
                    try
                    {
                        var h = LocalFs.Sha256Of(f, long.MaxValue, ct);
                        if (!byFull.TryGetValue(h, out var l)) byFull[h] = l = new List<string>();
                        l.Add(f);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                foreach (var same in byFull.Values.Where(l => l.Count > 1))
                    result.Add(new DupGroup(size, same.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()));
            }
        }
        return result.OrderByDescending(g => g.Size * g.Files.Count).ToList();
    }
}
