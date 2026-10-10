import Foundation

/// Texty pro nabídku Nápověda: co je nového a přehled funkcí (anglické překlady jsou ve slovníku Tr).
enum InfoTexts {
    static let news: [String] = [
        L("# Nové"),
        L("Řazení podle typu souboru: klikněte na záhlaví Přípona nebo použijte menu."),
        L("Řazení se pamatuje zvlášť pro každou složku (např. fotky podle data, hudba podle názvu)."),
        L("Filtr podle typu souboru: tlačítko v panelu ukáže jen obrázky, dokumenty, hudbu, video nebo archivy."),
        L("Sady záložek: uložte rozložení obou panelů pod jménem a otevřete ho jedním klikem."),
        L("Zrcadlení mezi složkou a serverem (FTP, SFTP, cloud) v obou směrech."),
        L("Cloudová úložiště (Dropbox, Google Drive, OneDrive…) přes program rclone."),
        L("Čeština a angličtina (Nastavení, platí po restartu)."),
        L("Aktualizace z GitHubu: menu Nápověda → Aktualizovat aplikaci; při startu se jen oznámí nová verze."),
        L("# Dříve"),
        L("Dva panely, záložky, ovládání z klávesnice, F2 až F8, označování, rychlé hledání, vrácení operací."),
        L("FTP, FTP+TLS a SFTP (heslo i klíč), síťové disky a sdílené složky."),
        L("Archivy (zip, tar.gz, 7z, rar…) jako složky, balení a rozbalení."),
        L("Hledání souborů podle názvu a obsahu, hledání duplicit."),
        L("Porovnání adresářů a souborů, zrcadlení, hromadné přejmenování."),
        L("Rychlý náhled, hex, kontrolní součty, dělení a slepování souborů, příkazový řádek.")
    ]

    static let features: [String] = [
        L("# Základy"),
        L("Dva panely, Tab přepíná mezi nimi. Šipky, Enter a Backspace se pohybují po složkách."),
        L("F3 zobrazí soubor, F4 ho upraví (na serveru se změny po uložení nahrají zpět)."),
        L("F5 a F6 kopírují a přesouvají do druhého panelu, F7 vytvoří složku, F8 smaže do koše."),
        L("Zkratky všech funkcí jsou vidět v menu a jdou změnit v Nastavení."),
        L("# Panely"),
        L("Záložky v každém panelu a sady záložek, které si pamatují rozložení obou panelů."),
        L("Rychlé hledání psaním písmen, filtr názvů a filtr podle typu souboru (tlačítko v panelu)."),
        L("Řazení klikem na záhlaví sloupce; řazení se pamatuje zvlášť pro každou složku."),
        L("Označování souborů, označení podle masky a vrácení posledních operací."),
        L("# Servery a cloud"),
        L("Připojení k FTP, FTP+TLS a SFTP serverům, hesla se ukládají bezpečně."),
        L("Cloudová úložiště (Dropbox, Google Drive, OneDrive…) přes program rclone."),
        L("Zrcadlení složky do druhého panelu, i mezi složkou a serverem."),
        L("# Nástroje"),
        L("Hledání souborů podle názvu a obsahu, hledání duplicit."),
        L("Porovnání adresářů a souborů, hromadné přejmenování, kontrolní součty."),
        L("Archivy jako složky, balení a rozbalení, dělení a slepování souborů."),
        L("Rychlý náhled, příkazový řádek a vlastní příkazy v menu."),
        L("# Aplikace a jazyk"),
        L("Čeština a angličtina, světlý a tmavý vzhled, nastavení sloupců a písma."),
        L("Aktualizace z GitHubu v menu Nápověda; při startu se jen oznámí nová verze.")
    ]
}
