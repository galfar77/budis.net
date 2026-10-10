using System.Security.Cryptography;
using System.Text;

namespace BudisCommander.Core;

/// <summary>Práce s lokálním souborovým systémem bez závislosti na rozhraní.</summary>
public static class LocalFs
{
    public static readonly EnumerationOptions ListOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
    };

    public static List<FileEntry> List(string dir, bool showHidden)
    {
        var result = new List<FileEntry>();
        var di = new DirectoryInfo(dir);
        foreach (var fi in di.EnumerateFileSystemInfos("*", ListOptions))
        {
            try
            {
                var a = fi.Attributes;
                if (!showHidden && (a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                bool isDir = (a & FileAttributes.Directory) != 0;
                result.Add(new FileEntry
                {
                    Name = fi.Name,
                    FullPath = fi.FullName,
                    IsDirectory = isDir,
                    Size = fi is FileInfo f ? SafeLength(f) : 0,
                    Modified = SafeTime(fi),
                    Attributes = a,
                });
            }
            catch { /* nečitelná položka se přeskočí */ }
        }
        return result;
    }

    private static long SafeLength(FileInfo f) { try { return f.Length; } catch { return 0; } }
    private static DateTime? SafeTime(FileSystemInfo f) { try { return f.LastWriteTime; } catch { return null; } }

    /// <summary>Všechny soubory ze všech podsložek (nejvýš <paramref name="limit"/>).</summary>
    public static List<FileEntry> ListBranch(string dir, bool showHidden, int limit = 20000)
    {
        var result = new List<FileEntry>();
        var prefix = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
        foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", opts))
        {
            try
            {
                var a = fi.Attributes;
                if (!showHidden && (a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                var rel = fi.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? fi.FullName[prefix.Length..] : fi.Name;
                result.Add(new FileEntry
                {
                    Name = fi.Name, FullPath = fi.FullName, IsDirectory = false, Size = SafeLength(fi),
                    Modified = SafeTime(fi), Attributes = a, SubPath = rel,
                });
                if (result.Count >= limit) break;
            }
            catch { }
        }
        return result;
    }

    /// <summary>Součet velikostí souborů (rekurzivně u složek).</summary>
    public static long TotalSize(string path, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long sum = 0;
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                try { sum += f.Length; } catch { }
            }
            return sum;
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }
    }

    public static bool SameVolume(string a, string b)
    {
        try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Zda mají dva soubory shodný obsah.</summary>
    public static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var ba = new byte[1 << 20]; var bb = new byte[1 << 20];
            while (true)
            {
                int ra = ReadFull(sa, ba), rb = ReadFull(sb, bb);
                if (ra != rb) return false;
                if (ra == 0) return true;
                if (!ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb))) return false;
            }
        }
        catch { return false; }
    }

    private static int ReadFull(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf, total, buf.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // --- hex výpis ---------------------------------------------------------------

    public static string HexDump(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (int off = 0; off < data.Length; off += 16)
        {
            var chunk = data.Slice(off, Math.Min(16, data.Length - off));
            sb.Append(off.ToString("x8")).Append("  ");
            var ascii = new StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                if (i < chunk.Length)
                {
                    sb.Append(chunk[i].ToString("x2")).Append(' ');
                    ascii.Append(chunk[i] is >= 32 and < 127 ? (char)chunk[i] : '.');
                }
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |").Append(ascii).Append("|\n");
        }
        return sb.ToString();
    }

    public static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        var n = Math.Min(data.Length, 4096);
        for (int i = 0; i < n; i++) if (data[i] == 0) return true;
        return false;
    }

    // --- kontrolní součty ---------------------------------------------------------

    public sealed record Hashes(string Md5, string Sha1, string Sha256);

    public static Hashes ComputeHashes(string path, CancellationToken ct = default)
    {
        using var md5 = MD5.Create(); using var sha1 = SHA1.Create(); using var sha256 = SHA256.Create();
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var buf = new byte[1 << 20];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            md5.TransformBlock(buf, 0, n, null, 0);
            sha1.TransformBlock(buf, 0, n, null, 0);
            sha256.TransformBlock(buf, 0, n, null, 0);
        }
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return new Hashes(Convert.ToHexString(md5.Hash!).ToLowerInvariant(),
                          Convert.ToHexString(sha1.Hash!).ToLowerInvariant(),
                          Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
    }

    public static string Sha256Of(string path, long limit = long.MaxValue, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var buf = new byte[1 << 20];
        long remaining = limit;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = s.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (n <= 0) break;
            sha.TransformBlock(buf, 0, n, null, 0);
            remaining -= n;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    // --- dělení a slepování --------------------------------------------------------

    /// <summary>Rozdělí soubor na díly name.001, name.002… Vrací počet dílů; při zrušení díly smaže.</summary>
    public static int Split(string file, long partSize, string destDir, Action<double>? progress, CancellationToken ct)
    {
        var created = new List<string>();
        try
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            long total = Math.Max(input.Length, 1), done = 0;
            var buf = new byte[1 << 20];
            int part = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                part++;
                var dest = Path.Combine(destDir, Path.GetFileName(file) + "." + part.ToString("000"));
                created.Add(dest);
                long written = 0;
                using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    while (written < partSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = input.Read(buf, 0, (int)Math.Min(buf.Length, partSize - written));
                        if (n <= 0) break;
                        output.Write(buf, 0, n);
                        written += n; done += n;
                        progress?.Invoke((double)done / total);
                    }
                }
                if (written == 0) { File.Delete(dest); created.RemoveAt(created.Count - 1); break; }
                if (written < partSize) break;
            }
            return created.Count;
        }
        catch
        {
            foreach (var c in created) { try { File.Delete(c); } catch { } }
            throw;
        }
    }

    /// <summary>Spojí díly name.001, name.002… do souboru <paramref name="dest"/>.</summary>
    public static void Combine(string firstPart, string dest, Action<double>? progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(firstPart)!;
        var baseName = firstPart[..^4];
        baseName = Path.GetFileName(baseName);
        var parts = new List<string>();
        while (true)
        {
            var p = Path.Combine(dir, baseName + "." + (parts.Count + 1).ToString("000"));
            if (!File.Exists(p)) break;
            parts.Add(p);
        }
        long total = Math.Max(parts.Sum(p => new FileInfo(p).Length), 1), done = 0;
        try
        {
            using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buf = new byte[1 << 20];
            foreach (var p in parts)
            {
                using var input = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buf, 0, n);
                    done += n;
                    progress?.Invoke((double)done / total);
                }
            }
        }
        catch
        {
            try { File.Delete(dest); } catch { }
            throw;
        }
    }

    // --- atributy -------------------------------------------------------------------

    public sealed class AttributeChange
    {
        public DateTime? Modified { get; set; }
        public bool? ReadOnly { get; set; }
        public bool? Hidden { get; set; }
        public bool? System { get; set; }
        public bool? Archive { get; set; }
        public bool Recursive { get; set; }
    }

    public static List<string> ApplyAttributes(AttributeChange change, IEnumerable<string> paths)
    {
        var errors = new List<string>();
        void One(string p)
        {
            try
            {
                var a = File.GetAttributes(p);
                a = Set(a, FileAttributes.ReadOnly, change.ReadOnly);
                a = Set(a, FileAttributes.Hidden, change.Hidden);
                a = Set(a, FileAttributes.System, change.System);
                a = Set(a, FileAttributes.Archive, change.Archive);
                if (change.Modified.HasValue)
                {
                    // před změnou data se musí zrušit atribut jen pro čtení
                    if ((File.GetAttributes(p) & FileAttributes.ReadOnly) != 0) File.SetAttributes(p, File.GetAttributes(p) & ~FileAttributes.ReadOnly);
                    if (Directory.Exists(p)) Directory.SetLastWriteTime(p, change.Modified.Value);
                    else File.SetLastWriteTime(p, change.Modified.Value);
                }
                File.SetAttributes(p, a);
            }
            catch (Exception e) { errors.Add($"{Path.GetFileName(p)}: {e.Message}"); }
        }
        foreach (var p in paths)
        {
            One(p);
            if (change.Recursive && Directory.Exists(p))
            {
                var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
                foreach (var child in Directory.EnumerateFileSystemEntries(p, "*", opts)) One(child);
            }
        }
        return errors;
    }

    private static FileAttributes Set(FileAttributes a, FileAttributes flag, bool? on) =>
        on == null ? a : on.Value ? a | flag : a & ~flag;
}
