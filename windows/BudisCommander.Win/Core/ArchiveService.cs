using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace BudisCommander.Core;

public static class ArchiveService
{
    private static readonly string[] Extensions = { ".zip", ".tar", ".tgz", ".tbz", ".tbz2", ".txz", ".7z", ".rar", ".jar", ".gz", ".bz2", ".xz" };
    private static readonly string[] Compound = { ".tar.gz", ".tar.bz2", ".tar.xz" };

    public static bool IsArchive(string name)
    {
        var lower = name.ToLowerInvariant();
        return Extensions.Any(lower.EndsWith) || Compound.Any(lower.EndsWith);
    }

    public static string TempRoot => Path.Combine(Path.GetTempPath(), "BudisArchives");

    /// <summary>Zabalí soubory a složky do .zip nebo .tar.gz (cesty v archivu jsou relativní k <paramref name="baseDir"/>).</summary>
    public static void Create(string archivePath, string baseDir, IEnumerable<string> paths,
                              IProgress<(string Text, double Fraction)>? progress, CancellationToken ct)
    {
        var lower = archivePath.ToLowerInvariant();
        var files = ExpandFiles(baseDir, paths).ToList();
        long total = Math.Max(files.Where(f => !f.IsDir).Sum(f => new FileInfo(f.Full).Length), 1), done = 0;

        if (lower.EndsWith(".zip"))
        {
            using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var (full, rel, isDir) in files)
            {
                ct.ThrowIfCancellationRequested();
                if (isDir) { zip.CreateEntry(rel.TrimEnd('/') + "/"); continue; }
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                entry.LastWriteTime = new FileInfo(full).LastWriteTime;
                using var es = entry.Open();
                using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Copy(src, es, n => { done += n; progress?.Report((rel, (double)done / total)); }, ct);
            }
        }
        else if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
        {
            using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write);
            using var gz = new GZipStream(fs, CompressionLevel.Optimal);
            using var tar = new TarWriter(gz);
            foreach (var (full, rel, isDir) in files)
            {
                ct.ThrowIfCancellationRequested();
                if (isDir) continue;
                tar.WriteEntry(full, rel);
                done += new FileInfo(full).Length;
                progress?.Report((rel, (double)done / total));
            }
        }
        else throw new NotSupportedException("Název archivu musí končit na .zip, .tar.gz nebo .tgz.");
    }

    private static IEnumerable<(string Full, string Rel, bool IsDir)> ExpandFiles(string baseDir, IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var rel = Path.GetRelativePath(baseDir, p).Replace('\\', '/');
            if (Directory.Exists(p))
            {
                yield return (p, rel, true);
                var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
                foreach (var e in Directory.EnumerateFileSystemEntries(p, "*", opts))
                    yield return (e, Path.GetRelativePath(baseDir, e).Replace('\\', '/'), Directory.Exists(e));
            }
            else yield return (p, rel, false);
        }
    }

    private static void Copy(Stream from, Stream to, Action<int> onBytes, CancellationToken ct)
    {
        var buf = new byte[1 << 17];
        int n;
        while ((n = from.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            to.Write(buf, 0, n);
            onBytes(n);
        }
    }

    /// <summary>Rozbalí libovolný podporovaný archiv (zip, tar, tar.gz, 7z, rar…) do složky.</summary>
    public static void Extract(string archivePath, string destDir, string? password, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var lower = archivePath.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
        {
            using var fs = File.OpenRead(archivePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, destDir, true);
            return;
        }
        if (lower.EndsWith(".tar"))
        {
            using var fs = File.OpenRead(archivePath);
            TarFile.ExtractToDirectory(fs, destDir, true);
            return;
        }
        if (lower.EndsWith(".tar.bz2") || lower.EndsWith(".tar.xz") || lower.EndsWith(".tbz") || lower.EndsWith(".tbz2") || lower.EndsWith(".txz"))
        {
            using var stream = File.OpenRead(archivePath);
            using var reader = ReaderFactory.Open(stream, new ReaderOptions { Password = password });
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory || reader.Entry.Key == null) continue;
                var target = SafeTarget(destDir, reader.Entry.Key);
                if (target == null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target);
                reader.WriteEntryTo(output);
            }
            return;
        }
        using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions { Password = password });
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.Key == null) continue;
            var target = SafeTarget(destDir, entry.Key);
            if (target == null) continue;                    // ochrana proti ../
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.WriteToFile(target, new ExtractionOptions { Overwrite = true, ExtractFullPath = false });
        }
    }

    private static string? SafeTarget(string destDir, string key)
    {
        var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(destDir, key.Replace('/', Path.DirectorySeparatorChar)));
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    public static bool IsEncrypted(string archivePath)
    {
        try
        {
            using var archive = ArchiveFactory.Open(archivePath);
            return archive.Entries.Any(e => e.IsEncrypted);
        }
        catch { return false; }
    }

    /// <summary>Přidá soubory do existujícího zipu (existující položky stejného jména přepíše).</summary>
    public static void AddToZip(string zipPath, string baseDir, IEnumerable<string> paths, CancellationToken ct)
    {
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Update);
        foreach (var (full, rel, isDir) in ExpandFiles(baseDir, paths))
        {
            ct.ThrowIfCancellationRequested();
            if (isDir) { if (zip.GetEntry(rel.TrimEnd('/') + "/") == null) zip.CreateEntry(rel.TrimEnd('/') + "/"); continue; }
            zip.GetEntry(rel)?.Delete();
            zip.CreateEntryFromFile(full, rel, CompressionLevel.Optimal);
        }
    }
}
