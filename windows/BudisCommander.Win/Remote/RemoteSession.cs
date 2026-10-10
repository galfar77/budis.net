using BudisCommander.Core;

namespace BudisCommander.Remote;

public static class RemotePath
{
    public static string Normalize(string p)
    {
        p = p.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//")) p = p.Replace("//", "/");
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        return p;
    }

    public static string Parent(string p)
    {
        p = Normalize(p);
        int i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    public static string Child(string p, string name) => (p == "/" ? "" : Normalize(p)) + "/" + name;
    public static string NameOf(string p) => Normalize(p).Split('/').Last();
}

/// <summary>Sleduje celkový průběh přenosu složky (podle bajtů přes všechny soubory).</summary>
public sealed class ProgressTracker
{
    private readonly long _total;
    private long _done;
    private readonly IProgress<(string Text, double Fraction)>? _sink;
    private readonly object _lock = new();

    public ProgressTracker(long total, IProgress<(string Text, double Fraction)>? sink)
    {
        _total = total;
        _sink = sink;
    }

    public void File(string name, double fraction, long size)
    {
        double overall;
        lock (_lock) overall = _total > 0 ? (_done + fraction * size) / _total : fraction;
        _sink?.Report((name, Math.Clamp(overall, 0, 1)));
    }

    public void FileDone(long size) { lock (_lock) _done += size; }
}

/// <summary>Společný základ spojení k FTP a SFTP serveru. Rekurzivní operace stojí na několika primitivech.</summary>
public abstract class RemoteSession : IDisposable
{
    public abstract string DisplayName { get; }
    public string Host { get; protected set; } = "";
    public abstract string StartPath { get; }

    public abstract Task ConnectAsync(CancellationToken ct);
    public abstract Task<List<FileEntry>> ListAsync(string path, CancellationToken ct);
    protected abstract Task DownloadFileAsync(string remote, string local, long size, Action<double>? progress, CancellationToken ct);
    protected abstract Task UploadFileAsync(string local, string remote, Action<double>? progress, CancellationToken ct);
    protected abstract Task DeleteFileAsync(string path, CancellationToken ct);
    protected abstract Task DeleteEmptyDirectoryAsync(string path, CancellationToken ct);
    public abstract Task MkdirAsync(string path, CancellationToken ct);
    public abstract Task RenameAsync(string from, string to, CancellationToken ct);
    /// <summary>Přečte prvních <paramref name="maxBytes"/> bajtů souboru (náhled).</summary>
    public abstract Task<byte[]> ReadHeadAsync(string path, int maxBytes, CancellationToken ct);

    public async Task<long> TotalSizeAsync(string path, bool isDirectory, long size, CancellationToken ct)
    {
        if (!isDirectory) return size;
        long sum = 0;
        foreach (var child in await ListAsync(path, ct))
            sum += await TotalSizeAsync(child.FullPath, child.IsDirectory, child.Size, ct);
        return sum;
    }

    public async Task DownloadAsync(string remote, bool isDirectory, long size, string local,
                                    IProgress<(string Text, double Fraction)>? progress, CancellationToken ct)
    {
        var tracker = progress == null ? null : new ProgressTracker(await TotalSizeAsync(remote, isDirectory, size, ct), progress);
        await DownloadTreeAsync(remote, isDirectory, size, local, tracker, ct);
    }

    private async Task DownloadTreeAsync(string remote, bool isDirectory, long size, string local, ProgressTracker? tracker, CancellationToken ct)
    {
        if (isDirectory)
        {
            Directory.CreateDirectory(local);
            foreach (var child in await ListAsync(remote, ct))
                await DownloadTreeAsync(child.FullPath, child.IsDirectory, child.Size, Path.Combine(local, child.Name), tracker, ct);
        }
        else
        {
            var name = Path.GetFileName(local);
            await DownloadFileAsync(remote, local, size, tracker == null ? null : f => tracker.File(name, f, size), ct);
            tracker?.FileDone(size);
        }
    }

    public async Task UploadAsync(string local, string remote, IProgress<(string Text, double Fraction)>? progress, CancellationToken ct)
    {
        var tracker = progress == null ? null : new ProgressTracker(LocalFs.TotalSize(local, ct), progress);
        await UploadTreeAsync(local, remote, tracker, ct);
    }

    private async Task UploadTreeAsync(string local, string remote, ProgressTracker? tracker, CancellationToken ct)
    {
        if (Directory.Exists(local))
        {
            try { await MkdirAsync(remote, ct); } catch (Exception) when (!ct.IsCancellationRequested) { /* složka už může existovat */ }
            foreach (var child in Directory.EnumerateFileSystemEntries(local))
                await UploadTreeAsync(child, RemotePath.Child(remote, Path.GetFileName(child)), tracker, ct);
        }
        else
        {
            long size = new FileInfo(local).Length;
            var name = Path.GetFileName(local);
            await UploadFileAsync(local, remote, tracker == null ? null : f => tracker.File(name, f, size), ct);
            tracker?.FileDone(size);
        }
    }

    public async Task DeleteAsync(string path, bool isDirectory, CancellationToken ct)
    {
        if (isDirectory)
        {
            foreach (var child in await ListAsync(path, ct))
                await DeleteAsync(child.FullPath, child.IsDirectory, ct);
            await DeleteEmptyDirectoryAsync(path, ct);
        }
        else await DeleteFileAsync(path, ct);
    }

    public abstract void Dispose();
}
