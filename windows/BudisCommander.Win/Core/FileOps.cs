using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace BudisCommander.Core;

/// <summary>Kopírování, přesun a mazání lokálních souborů s průběhem a rušením.</summary>
public static class FileOps
{
    public static async Task CopyAsync(string source, string dest, bool overwrite,
                                       IProgress<(string Text, double Fraction)>? progress, CancellationToken ct)
    {
        long total = Math.Max(LocalFs.TotalSize(source, ct), 1);
        long done = 0;
        await CopyTreeAsync(source, dest, overwrite, n =>
        {
            done += n;
            progress?.Report((Path.GetFileName(source), Math.Min((double)done / total, 1)));
        }, ct);
    }

    private static async Task CopyTreeAsync(string source, string dest, bool overwrite, Action<long> onBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(dest);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                await CopyTreeAsync(entry, Path.Combine(dest, Path.GetFileName(entry)), overwrite, onBytes, ct);
            try { Directory.SetLastWriteTime(dest, Directory.GetLastWriteTime(source)); } catch { }
            return;
        }
        if (File.Exists(dest) && !overwrite) return;
        if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true))
        await using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = await input.ReadAsync(buf, ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), ct);
                onBytes(n);
            }
        }
        try
        {
            File.SetLastWriteTime(dest, File.GetLastWriteTime(source));
            File.SetAttributes(dest, File.GetAttributes(source));
        }
        catch { }
    }

    /// <summary>Přesun: na stejném disku přejmenováním, jinak kopírováním a smazáním zdroje.</summary>
    public static async Task MoveAsync(string source, string dest, bool overwrite,
                                       IProgress<(string Text, double Fraction)>? progress, CancellationToken ct)
    {
        bool isDir = Directory.Exists(source);
        if (overwrite)
        {
            if (File.Exists(dest)) { File.SetAttributes(dest, FileAttributes.Normal); File.Delete(dest); }
            else if (Directory.Exists(dest) && !isDir) Directory.Delete(dest, true);
        }
        if (LocalFs.SameVolume(source, dest) && !(isDir && Directory.Exists(dest)))
        {
            if (isDir) Directory.Move(source, dest); else File.Move(source, dest);
            return;
        }
        await CopyAsync(source, dest, overwrite, progress, ct);
        ct.ThrowIfCancellationRequested();
        DeletePermanently(source);
    }

    public static void DeletePermanently(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(path, true);
        }
        else if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    /// <summary>Přesune soubor nebo složku do Koše (mimo Windows ji smaže natrvalo).</summary>
    public static void DeleteToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows()) { DeletePermanently(path); return; }
        if (Directory.Exists(path))
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    public static string UniqueName(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path) && !Directory.Exists(path)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!File.Exists(Path.Combine(dir, candidate)) && !Directory.Exists(Path.Combine(dir, candidate))) return candidate;
        }
    }
}
