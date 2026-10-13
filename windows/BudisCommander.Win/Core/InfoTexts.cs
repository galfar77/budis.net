namespace BudisCommander.Core;

/// <summary>Texty pro nabídku Nápověda: co je nového a přehled funkcí (anglické překlady jsou ve slovníku).</summary>
public static class InfoTexts
{
    public static IReadOnlyList<string> News
    {
        get
        {
            var list = new List<string>
            {
        "# Nové",
            "Správce připojení: uložené servery ve skupinách, rychlé připojení z menu.",
            "Úprava souboru uvnitř zip archivu: F4 ho otevře v editoru a po uložení zapíše zpět do archivu.",
            "Sdílení odkazem: na cloudu pravé tlačítko → Sdílet odkazem, odkaz se zkopíruje do schránky.",
            "Synchronizace na pozadí umí i obousměrný režim (nové a novější soubory na obě strany, nic se nemaže).",
            "Hromadné přejmenování: značky data a času změny, nadřazené složky a rozsahu znaků, volby bez diakritiky a mezer.",
            "Hledání počítačů v síti a jejich sdílených složek (Servery → Síť a disky).",
            "Historie operací: seznam kopírování, přesunů, mazání a přejmenování; kopírování jde zopakovat, ostatní vrátit zpět (menu Nástroje).",
            "Obnovení z Koše přímo v aplikaci (Windows: celý Koš, Mac: soubory smazané aplikací).",
            "Šířku sloupců změníte tažením za záhlaví, pořadí pravým tlačítkem na záhlaví; rozložení se pamatuje pro každou složku.",
            "Pravé tlačítko: Otevřít v programu nebo aplikaci; ve Windows i Spustit jako správce.",
            "Hledání: už při psaní z indexu, uvnitř archivů i na serveru a v cloudu.",
            "Přetahování pravým tlačítkem: po pustění nabídka Kopírovat sem nebo Přesunout sem (Windows).",
            "Miniatury souborů ve Windows (Nastavení): obrázky, videa, PDF a dokumenty.",
            "Pravé tlačítko myši: nabídka s funkcemi podle toho, co je pod ukazatelem (soubor, složka, archiv, více označených, prázdné místo).",
            "Vestavěný terminál ve Windows je plnohodnotný (ConPTY): fungují i vim, top, ssh a další celoobrazovkové programy.",
            "Rozšířené označování: podle masky, druhu, velikosti a data změny, stejná přípona, převrácení označení.",
            "Hledání uvnitř archivů a v obsahu souborů na serveru a v cloudu.",
            "Automatická instalace aktualizací (volitelně v Nastavení) a návrat na předchozí verzi (menu Nápověda).",
            "Správce procesů: přehled běžících programů, řazení a ukončení (menu Nástroje).",
            "Správce přenosů: kopírování a přesuny běží na pozadí, jde je zrušit a zopakovat.",
            "Synchronizace na pozadí: uložené úlohy se spouštějí podle časovače.",
            "Vestavěný terminál v okně aplikace (Ctrl+`).",
            "Telefon s Androidem: procházení a přenos souborů přes adb (iPhone není podporován).",
            "Barevná témata: šest vzhledů v Nastavení.",
            "Rychlé hledání přes index (Spotlight na Macu, Windows Search ve Windows): stačí nechat zaškrtnuté v okně hledání.",
            "Cloud bez ručního nastavování: aplikace si sama stáhne rclone a přidá Google Disk, Dropbox, OneDrive, Box, S3, WebDAV nebo B2.",
            "Průhledné okno ve stylu skla (jde vypnout v Nastavení).",
            "Řazení podle typu souboru: klikněte na záhlaví Přípona nebo použijte menu.",
            "Řazení se pamatuje zvlášť pro každou složku (např. fotky podle data, hudba podle názvu).",
            "Filtr podle typu souboru: tlačítko v panelu ukáže jen obrázky, dokumenty, hudbu, video nebo archivy.",
            "Sady záložek: uložte rozložení obou panelů pod jménem a otevřete ho jedním klikem.",
            "Zrcadlení mezi složkou a serverem (FTP, SFTP, cloud) v obou směrech.",
            "Cloudová úložiště (Dropbox, Google Drive, OneDrive…) přes program rclone.",
            "Čeština a angličtina (Nastavení, platí po restartu).",
                "Zástupci na ploše a v nabídce Start (menu Nástroje).",
        "Aktualizace z GitHubu: menu Nápověda → Aktualizovat aplikaci; při startu se jen oznámí nová verze.",
            "# Dříve",
            "Dva panely, záložky, ovládání z klávesnice, F2 až F8, označování, rychlé hledání, vrácení operací.",
            "FTP, FTP+TLS a SFTP (heslo i klíč), síťové disky a sdílené složky.",
            "Archivy (zip, tar.gz, 7z, rar…) jako složky, balení a rozbalení.",
            "Hledání souborů podle názvu a obsahu, hledání duplicit.",
            "Porovnání adresářů a souborů, zrcadlení, hromadné přejmenování.",
            "Rychlý náhled, hex, kontrolní součty, dělení a slepování souborů, příkazový řádek."
            };
            return list;
        }
    }

    public static readonly IReadOnlyList<string> Features = new[]
    {
        "# Základy",
        "Dva panely, Tab přepíná mezi nimi. Šipky, Enter a Backspace se pohybují po složkách.",
        "F3 zobrazí soubor, F4 ho upraví (na serveru se změny po uložení nahrají zpět).",
        "F5 a F6 kopírují a přesouvají do druhého panelu, F7 vytvoří složku, F8 smaže do koše.",
        "Zkratky všech funkcí jsou vidět v menu a jdou změnit v Nastavení.",
        "# Panely",
        "Záložky v každém panelu a sady záložek, které si pamatují rozložení obou panelů.",
        "Rychlé hledání psaním písmen, filtr názvů a filtr podle typu souboru (tlačítko v panelu).",
        "Řazení klikem na záhlaví sloupce; řazení se pamatuje zvlášť pro každou složku.",
        "Označování souborů, označení podle masky a vrácení posledních operací.",
        "# Servery a cloud",
        "Připojení k FTP, FTP+TLS a SFTP serverům, hesla se ukládají bezpečně.",
        "Cloudová úložiště (Dropbox, Google Drive, OneDrive…) přes program rclone.",
        "Zrcadlení složky do druhého panelu, i mezi složkou a serverem.",
        "# Nástroje",
        "Hledání souborů podle názvu a obsahu, hledání duplicit.",
        "Porovnání adresářů a souborů, hromadné přejmenování, kontrolní součty.",
        "Archivy jako složky, balení a rozbalení, dělení a slepování souborů.",
        "Rychlý náhled, příkazový řádek a vlastní příkazy v menu.",
        "# Aplikace a jazyk",
        "Čeština a angličtina, světlý a tmavý vzhled, nastavení sloupců a písma.",
        "Aktualizace z GitHubu v menu Nápověda; při startu se jen oznámí nová verze."
    };
}
