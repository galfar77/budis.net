namespace BudisCommander.Core;

/// <summary>Co je pod ukazatelem při kliknutí pravým tlačítkem.</summary>
public enum ContextKind { Empty, File, Archive, Folder, Multiple }

/// <summary>Obsah místní nabídky (pravé tlačítko) v seznamu souborů: identifikátory akcí, „-“ je oddělovač.</summary>
public static class ContextMenus
{
    public const string Separator = "-";

    public static IReadOnlyList<string> For(ContextKind kind, bool remote, bool executable = false)
    {
        var list = new List<string>();
        void Add(params string[] ids) => list.AddRange(ids);
        void Sep() { if (list.Count > 0 && list[^1] != Separator) list.Add(Separator); }
        void Local(params string[] ids) { if (!remote) list.AddRange(ids); }
        void Windows(params string[] ids) { if (!remote && OperatingSystem.IsWindows()) list.AddRange(ids); }

        switch (kind)
        {
            case ContextKind.Empty:
                Add("mkdir"); Local("pasteFiles"); Sep();
                Add("refresh", "markAll", "invertMarks"); Sep();
                Add("hidden"); Local("dirSizes", "terminal"); Sep();
                Add("favorites");
                break;
            case ContextKind.Multiple:
                Add("copy", "move"); Sep();
                Local("copyFiles", "cutFiles"); Add("copyPath", "copyName"); Sep();
                Local("batchRename"); Add("delete"); Sep();
                Local("pack", "attributes"); Sep();
                Add("invertMarks", "unmarkAll");
                break;
            case ContextKind.Folder:
                Add("open"); Sep();
                Add("copy", "move"); Sep();
                Local("copyFiles", "cutFiles", "pasteFiles"); Add("copyPath"); Sep();
                Add("rename", "delete"); Sep();
                Local("pack", "dirSizes", "attributes");
                break;
            case ContextKind.Archive:
                Add("open"); Windows("openWith"); Sep();
                Add("copy", "move"); Sep();
                Local("copyFiles", "cutFiles"); Add("copyPath", "copyName"); Sep();
                Add("rename", "delete"); Sep();
                Local("unpack", "pack", "checksum", "attributes");
                break;
            default:    // soubor
                Add("open", "view", "edit"); Windows("openWith"); if (executable) Windows("runAsAdmin"); Sep();
                Add("copy", "move"); Sep();
                Local("copyFiles", "cutFiles"); Add("copyPath", "copyName"); Sep();
                Add("rename", "delete"); Sep();
                Local("pack", "checksum", "attributes");
                break;
        }
        while (list.Count > 0 && list[^1] == Separator) list.RemoveAt(list.Count - 1);
        return list;
    }

    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".bat", ".cmd", ".msi", ".ps1", ".com", ".vbs", ".msc" };

    /// <summary>Jestli jde soubor spustit (a tedy „jako správce“).</summary>
    public static bool IsRunnable(string name) => Runnable.Contains(Path.GetExtension(name));

    /// <summary>Určí druh obsahu pro označené položky a položku pod ukazatelem.</summary>
    public static ContextKind KindOf(int markedCount, bool underIsMarked, FileEntry? under)
    {
        if (markedCount > 1 && (under == null || underIsMarked)) return ContextKind.Multiple;
        if (under == null || under.IsParent) return ContextKind.Empty;
        if (under.IsDirectory) return ContextKind.Folder;
        return ArchiveService.IsArchive(under.Name) ? ContextKind.Archive : ContextKind.File;
    }
}
