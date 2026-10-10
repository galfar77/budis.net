using Avalonia.Input;
using BudisCommander.Core;

namespace BudisCommander.Ui;

public sealed record ActionDef(string Id, string Title, string? DefaultGesture, string Menu);

/// <summary>Všechny akce aplikace: název, výchozí zkratka a menu, do kterého patří.</summary>
public static class Actions
{
    public const string File = "Soubor", Mark = "Označit", Tools = "Nástroje", View = "Zobrazit", Servers = "Servery";

    public static readonly IReadOnlyList<ActionDef> All = new List<ActionDef>
    {
        new("view", "Zobrazit", "F3", File),
        new("edit", "Editovat", "F4", File),
        new("copy", "Kopírovat do druhého panelu", "F5", File),
        new("move", "Přesunout do druhého panelu", "F6", File),
        new("mkdir", "Nová složka", "F7", File),
        new("delete", "Smazat do Koše", "F8", File),
        new("deleteAlt", "Smazat do Koše (Delete)", "Delete", ""),
        new("deletePermanent", "Smazat natrvalo", "Shift+Delete", File),
        new("rename", "Přejmenovat", "F2", File),
        new("renameAlt", "Přejmenovat (Shift+F6)", "Shift+F6", ""),
        new("attributes", "Atributy a časy", "Alt+Enter", File),
        new("symlink", "Vytvořit symbolický odkaz", null, File),
        new("pack", "Zabalit do archivu", "Alt+F5", File),
        new("unpack", "Rozbalit archiv", "Alt+F9", File),
        new("addToArchive", "Přidat do archivu v druhém panelu", null, File),
        new("split", "Rozdělit soubor na díly", null, File),
        new("combine", "Slepit díly souboru", null, File),
        new("checksum", "Kontrolní součty (MD5, SHA)", "Ctrl+Shift+H", File),
        new("diff", "Porovnat dva soubory", "Ctrl+Shift+I", File),
        new("undo", "Vrátit poslední operaci", "Ctrl+Z", File),
        new("quit", "Konec", "Alt+F4", File),

        new("markAll", "Označit vše", "Ctrl+A", Mark),
        new("unmarkAll", "Zrušit označení", "Ctrl+Shift+A", Mark),
        new("invertMarks", "Převrátit označení", null, Mark),
        new("markMask", "Označit podle masky…", null, Mark),
        new("unmarkMask", "Odznačit podle masky…", null, Mark),
        new("compare", "Porovnat adresáře", "Ctrl+Shift+D", Mark),
        new("compareContent", "Porovnat adresáře podle obsahu", null, Mark),
        new("sync", "Zrcadlit adresář do druhého panelu", "Ctrl+Shift+Y", Mark),
        new("copyFiles", "Kopírovat soubory do schránky", "Ctrl+C", Mark),
        new("cutFiles", "Vyjmout soubory do schránky", "Ctrl+X", Mark),
        new("pasteFiles", "Vložit soubory ze schránky", "Ctrl+V", Mark),
        new("copyPath", "Kopírovat cestu", "Ctrl+Shift+C", Mark),
        new("copyName", "Kopírovat název", null, Mark),
        new("copyDirPath", "Kopírovat cestu složky", null, Mark),

        new("search", "Hledat soubory…", "Alt+F7", Tools),
        new("duplicates", "Hledat duplicity…", null, Tools),
        new("batchRename", "Hromadné přejmenování…", "Ctrl+M", Tools),
        new("commandLine", "Příkazový řádek", "Ctrl+J", Tools),
        new("dirSizes", "Spočítat velikosti složek", "Alt+Shift+Enter", Tools),
        new("userMenu", "Uživatelské příkazy…", null, Tools),
        new("resumeTransfer", "Pokračovat v přerušeném přenosu", null, Tools),
        new("shortcuts", "Vytvořit zástupce na ploše a v nabídce Start", null, Tools),
        new("update", "Aktualizovat aplikaci…", null, Tools),
        new("settings", "Nastavení…", "Ctrl+OemComma", Tools),

        new("refresh", "Obnovit", "Ctrl+R", View),
        new("hidden", "Skryté a systémové soubory", "Ctrl+H", View),
        new("branch", "Všechny podsložky najednou", "Ctrl+B", View),
        new("quickView", "Panel rychlého náhledu", "Ctrl+Q", View),
        new("filter", "Filtr panelu", "Ctrl+F", View),
        new("favorites", "Oblíbené a poslední složky…", "Ctrl+D", View),
        new("back", "Zpět v historii", "Alt+Left", View),
        new("forward", "Vpřed v historii", "Alt+Right", View),
        new("mirror", "Zrcadlit složku do druhého panelu", "Ctrl+Shift+U", View),
        new("swapPanels", "Prohodit panely", "Ctrl+U", View),
        new("newTab", "Nová záložka", "Ctrl+T", View),
        new("closeTab", "Zavřít záložku", "Ctrl+W", View),
        new("nextTab", "Další záložka", "Ctrl+Tab", View),
        new("prevTab", "Předchozí záložka", "Ctrl+Shift+Tab", View),
        new("tabSets", "Sady záložek…", "Ctrl+Shift+B", View),
        new("toggleTheme", "Přepnout vzhled (systém, světlý, tmavý)", null, View),
        new("sortType", "Třídit podle typu souboru", null, View),
        new("focusPath", "Přejít do pole s cestou", "Alt+D", View),

        new("connect", "Připojit k serveru FTP/SFTP…", "Ctrl+N", Servers),
        new("cloud", "Cloudová úložiště (rclone)…", null, Servers),
        new("network", "Síť a disky…", "Ctrl+Shift+N", Servers),
    };

    public static ActionDef? Get(string id) => All.FirstOrDefault(a => a.Id == id);

    public static KeyGesture? GestureFor(ActionDef a, AppSettings settings)
    {
        var text = settings.Gestures.TryGetValue(a.Id, out var custom) ? custom : a.DefaultGesture;
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return KeyGesture.Parse(text); } catch { return null; }
    }

    public static string GestureText(ActionDef a, AppSettings settings) =>
        settings.Gestures.TryGetValue(a.Id, out var custom) ? custom : a.DefaultGesture ?? "";
}
