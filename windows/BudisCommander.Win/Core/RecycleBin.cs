using System.Text;

namespace BudisCommander.Core;

public sealed record RecycledItem(string OriginalPath, string RecycledPath, string InfoPath, DateTime Deleted, long Size, bool IsDirectory)
{
    public string Name => Path.GetFileName(OriginalPath.TrimEnd('\\', '/'));
}

/// <summary>Čtení Koše ve Windows ze složky $Recycle.Bin (soubory $I s původní cestou a $R s obsahem) a obnovení položek.</summary>
public static class RecycleBin
{
    /// <summary>Složky Koše aktuálního uživatele na všech pevných discích.</summary>
    public static IEnumerable<string> DefaultRoots()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string? sid = null;
        try { sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; } catch { }
        if (sid == null) yield break;
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed) continue;
            var dir = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid);
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    /// <summary>Přečte soubor $I. Verze 1 (Windows Vista až 8) má cestu v pevných 520 bajtech, verze 2 (Windows 10 a novější) má před cestou její délku.</summary>
    public static RecycledItem? ParseInfo(string infoPath)
    {
        try
        {
            var b = File.ReadAllBytes(infoPath);
            if (b.Length < 28) return null;
            var version = BitConverter.ToInt64(b, 0);
            var size = BitConverter.ToInt64(b, 8);
            var filetime = BitConverter.ToInt64(b, 16);
            string path;
            if (version == 2)
            {
                var chars = BitConverter.ToInt32(b, 24);
                if (chars <= 0 || 28 + chars * 2 > b.Length) return null;
                path = Encoding.Unicode.GetString(b, 28, chars * 2).TrimEnd('\0');
            }
            else if (version == 1) path = Encoding.Unicode.GetString(b, 24, b.Length - 24).TrimEnd('\0');
            else return null;
            var name = Path.GetFileName(infoPath);
            if (!name.StartsWith("$I", StringComparison.OrdinalIgnoreCase)) return null;
            var recycled = Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + name[2..]);
            var isDir = Directory.Exists(recycled);
            if (!isDir && !File.Exists(recycled)) return null;
            DateTime deleted;
            try { deleted = DateTime.FromFileTimeUtc(filetime).ToLocalTime(); } catch { deleted = DateTime.MinValue; }
            return new RecycledItem(path, recycled, infoPath, deleted, size, isDir);
        }
        catch { return null; }
    }

    /// <summary>Položky v Koši od nejnověji smazané.</summary>
    public static List<RecycledItem> List(IEnumerable<string>? roots = null)
    {
        var items = new List<RecycledItem>();
        foreach (var root in roots ?? DefaultRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "$I*"))
                if (ParseInfo(f) is { } item) items.Add(item);
        }
        return items.OrderByDescending(i => i.Deleted).ToList();
    }

    /// <summary>Vrátí položku na původní místo (chybějící složky vytvoří). Když tam už něco je, přidá k názvu číslo.</summary>
    public static string Restore(RecycledItem item)
    {
        var dir = Path.GetDirectoryName(item.OriginalPath.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(dir)) throw new IOException("Původní umístění není známé.");
        Directory.CreateDirectory(dir);
        var name = FileOps.UniqueName(dir, item.Name);
        var target = Path.Combine(dir, name);
        if (item.IsDirectory) Directory.Move(item.RecycledPath, target); else File.Move(item.RecycledPath, target);
        try { File.Delete(item.InfoPath); } catch { }
        return target;
    }

    /// <summary>Obnoví z Koše poslední smazanou položku s danou původní cestou. Vrací, jestli se to povedlo.</summary>
    public static bool RestoreByOriginalPath(string originalPath, IEnumerable<string>? roots = null)
    {
        var hit = List(roots).FirstOrDefault(i => string.Equals(i.OriginalPath.TrimEnd('\\', '/'), originalPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (hit == null) return false;
        Restore(hit);
        return true;
    }
}
