import Foundation

/// Překlad textů za běhu. Zdrojové texty jsou česky; při jazyce „en“ se hledá přesná shoda, vzor s vloženými
/// hodnotami ({} v klíči) nebo začátek textu (klíče končící mezerou nebo dvojtečkou). Co se nenajde, zůstane česky.
enum Tr {
    /// "cs" nebo "en"; čte se z nastavení hned při prvním použití (změna se projeví po restartu).
    static var language: String = UserDefaults.standard.string(forKey: "language") ?? "cs"
    static var isEnglish: Bool { language == "en" }

    private final class Tables {
        var exact: [String: String] = [:]
        var patterns: [(NSRegularExpression, String)] = []
        var prefixes: [(String, String)] = []

        init() {
            var patternList: [(Int, NSRegularExpression, String)] = []
            for line in Tr.data.split(separator: "\n", omittingEmptySubsequences: true) {
                let parts = line.components(separatedBy: "\t")
                guard parts.count == 2 else { continue }
                let cs = Tr.unescape(parts[0]), en = Tr.unescape(parts[1])
                if cs.contains("{}") {
                    let pieces = cs.components(separatedBy: "{}")
                    let pattern = "^" + pieces.map { NSRegularExpression.escapedPattern(for: $0) }.joined(separator: "(.*?)") + "$"
                    if let rx = try? NSRegularExpression(pattern: pattern, options: [.dotMatchesLineSeparators]) {
                        patternList.append((pieces.reduce(0) { $0 + $1.count }, rx, en))
                    }
                } else {
                    exact[cs] = en
                    if cs.count >= 4 && (cs.hasSuffix(" ") || cs.hasSuffix(": ")) { prefixes.append((cs, en)) }
                }
            }
            patterns = patternList.sorted { $0.0 > $1.0 }.map { ($0.1, $0.2) }
            prefixes.sort { $0.0.count > $1.0.count }
        }
    }

    private static let tables = Tables()
    private static let lock = NSLock()
    private static var cache: [String: String] = [:]

    private static func unescape(_ s: String) -> String {
        guard s.contains("\\") else { return s }
        var out = ""
        var escaped = false
        for ch in s {
            if escaped {
                out.append(ch == "n" ? "\n" : (ch == "t" ? "\t" : ch))
                escaped = false
            } else if ch == "\\" {
                escaped = true
            } else {
                out.append(ch)
            }
        }
        return out
    }

    /// Přeloží český text do aktuálního jazyka (při češtině vrací beze změny).
    static func t(_ cs: String) -> String {
        guard isEnglish, !cs.isEmpty else { return cs }
        lock.lock()
        if let hit = cache[cs] { lock.unlock(); return hit }
        lock.unlock()
        let result = translate(cs)
        lock.lock()
        if cache.count > 5000 { cache.removeAll() }
        cache[cs] = result
        lock.unlock()
        return result
    }

    private static func translate(_ cs: String) -> String {
        let tables = Self.tables
        if let e = tables.exact[cs] { return e }
        let ns = cs as NSString
        let whole = NSRange(location: 0, length: ns.length)
        for (rx, english) in tables.patterns {
            guard let m = rx.firstMatch(in: cs, options: [], range: whole) else { continue }
            return fill(english, ns, m)
        }
        for (prefix, english) in tables.prefixes where cs.count > prefix.count && cs.hasPrefix(prefix) {
            return english + String(cs.dropFirst(prefix.count))
        }
        return cs
    }

    /// Dosadí do anglické šablony zachycené hodnoty: {} postupně, {2} podle čísla.
    private static func fill(_ english: String, _ ns: NSString, _ m: NSTextCheckingResult) -> String {
        var out = ""
        var next = 1
        var i = english.startIndex
        while i < english.endIndex {
            let ch = english[i]
            if ch == "{", let close = english[i...].firstIndex(of: "}") {
                let inner = english[english.index(after: i)..<close]
                if inner.allSatisfy({ $0.isNumber }) {
                    let idx: Int
                    if inner.isEmpty { idx = next; next += 1 } else { idx = Int(inner) ?? 0 }
                    if idx < m.numberOfRanges, m.range(at: idx).location != NSNotFound {
                        out += ns.substring(with: m.range(at: idx))
                    }
                    i = english.index(after: close)
                    continue
                }
            }
            out.append(ch)
            i = english.index(after: i)
        }
        return out
    }

    /// Český text a jeho anglický překlad (tabulátor), jeden řádek na text; \\n = nový řádek, {} = vložená hodnota.
    private static let data = #"""
\n\nCíl bude přesně odpovídat zdroji, i když je v něm některý soubor novější.	\n\nThe destination will match the source exactly, even if a file there is newer.
\n\nKlíč/certifikát serveru nelze ověřit. Pokud serveru věříte, zaškrtněte při připojení „Důvěřovat serveru bez ověření“.	\n\nThe server's key/certificate cannot be verified. If you trust the server, tick "Trust the server without verification" when connecting.
\n\nKopírují se soubory s jinou velikostí nebo novější než v cíli.	\n\nFiles with a different size or newer than the destination are copied.
  (všechny podsložky)	 (all subfolders)
 (další se kvůli limitu nečetly)	 (more were not read because of the limit)
 (jen pro čtení)	 (read-only)
# Aplikace a jazyk	# Application and language
# Dříve	# Earlier
# Nové	# New
# Nástroje	# Tools
# Panely	# Panels
# Servery a cloud	# Servers and cloud
# Základy	# Basics
(jen lokální složky)	(local folders only)
, chyb {}: {}	, errors {}: {}
, konflikty (přeskočeno) {}: {}	, conflicts (skipped) {}: {}
, přečteno souborů {}	, files read {}
, smazáno {}	, deleted {}
Adresa (https://…)	Address (https://…)
Adresa sdílení	Share address
Adresa serveru (jen mimo AWS)	Server address (not for AWS)
Adresář nelze otevřít:\n{}\n\n{}	The directory cannot be opened:\n{}\n\n{}
Akce	Action
Aktualizace funguje jen v aplikaci .app (ne při spuštění příkazem swift run).	Updates work only in the .app application (not when started with swift run).
Aktualizace se nezdařila:\n\n{}	The update failed:\n\n{}
Aktualizace z GitHubu v menu Nápověda; při startu se jen oznámí nová verze.	Updates from GitHub in the Help menu; at startup you are only told that a new version exists.
Aktualizace z GitHubu: menu Nápověda → Aktualizovat aplikaci; při startu se jen oznámí nová verze.	Updates from GitHub: Help menu → Update application; at startup you are only told that a new version exists.
Aktualizovat	Update
Aktualizovat aplikaci…	Update application…
Amazon S3 a kompatibilní	Amazon S3 and compatible
Aplikace	Applications
Aplikace neběží jako balíček .app (spuštěná příkazem swift run).	The application is not running as an .app bundle (started with swift run).
Aplikační klíč	Application key
Archiv	Archive
Archiv je chráněný heslem	The archive is password protected
Archiv je otevřený jen pro čtení, nelze do něj kopírovat.	The archive is open read-only, you cannot copy into it.
Archiv je otevřený jen pro čtení.	The archive is open read-only.
Archiv se zrcadlit nedá.	An archive cannot be mirrored.
Archiv vytvořen: {}	Archive created: {}
Archiv {}	Archive {}
Archiv „{}“ se nepodařilo otevřít:\n{}	Could not open archive "{}":\n{}
Archiv „{}“ už existuje. Přepsat?	Archive "{}" already exists. Overwrite?
Archivy (zip, tar.gz, 7z, rar…) jako složky, balení a rozbalení.	Archives (zip, tar.gz, 7z, rar…) as folders, packing and unpacking.
Archivy jako složky, balení a rozbalení, dělení a slepování souborů.	Archives as folders, packing and unpacking, splitting and joining files.
Atributy a časy	Attributes and times
Atributy a časy ({} položek)	Attributes and times ({} items)
Atributy lze měnit jen u souborů na lokálním disku.	Attributes can only be changed for files on a local disk.
Atributy nastaveny.	Attributes set.
Automaticky počítat velikosti složek (na pozadí, jen lokální složky)	Calculate folder sizes automatically (in the background, local folders only)
Automatická instalace aktualizace se nezdařila: {}	Automatic update installation failed: {}
Automatická instalace aktualizací (volitelně v Nastavení) a návrat na předchozí verzi (menu Nápověda).	Automatic installation of updates (optional, in Settings) and going back to the previous version (Help menu).
Balení funguje jen na lokálním disku. Soubory ze serveru nejdřív zkopírujte (F5).	Packing works only on a local disk. Copy the files from the server first (F5).
Balím {}…	Packing {}…
Barevná témata: šest vzhledů v Nastavení.	Colour themes: six looks in Settings.
Barevné téma	Color theme
Barevné štítky Finderu u názvů	Colored Finder tags next to names
Beze změny	No change
Binární soubory jsou shodné.	The binary files are identical.
Binární soubory se liší.	The binary files differ.
Budis Commander	Budis Commander
CPU	CPU
Chcete existující položku přepsat?	Do you want to overwrite the existing item?
Chyba	Error
Chyba: {}	Error: {}
Cloud bez ručního nastavování: aplikace si sama stáhne rclone a přidá Google Disk, Dropbox, OneDrive, Box, S3, WebDAV nebo B2.	Cloud without manual setup: the application downloads rclone by itself and adds Google Drive, Dropbox, OneDrive, Box, S3, WebDAV or B2.
Cloudová úložiště (Dropbox, Google Drive, OneDrive…) přes program rclone.	Cloud storage (Dropbox, Google Drive, OneDrive…) through the rclone program.
Cloudová úložiště (rclone)	Cloud storage (rclone)
Cloudová úložiště (rclone)…	Cloud storage (rclone)…
Co je nového	What's new
Co je nového…	What's new…
Co udělat s {}?	What to do with {}?
Cíl	Target
Cíl v cloudu zapište jako rclone:název:/cesta.	Write a cloud target as rclone:name:/path.
Cíl: {}	Destination: {}
Další štítky (oddělené čárkou)	Other tags (comma separated)
Datum zapište např. 31. 12. 2025 nebo 2025-12-31.	Enter a date such as 31. 12. 2025 or 2025-12-31.
Disk se nepodařilo připojit:\n{}	The disk could not be connected:\n{}
Disky	Volumes
Do koše	To Trash
Do koše ({})	To Trash ({})
Do koše: 	To Trash: 
Do koše: {}	To Trash: {}
Do složky „{}“ nelze zapisovat. Přesuňte aplikaci třeba do složky Aplikace ve vašem domovském adresáři.	The folder "{}" is not writable. Move the application to, for example, the Applications folder in your home directory.
Do: {}\nPodporováno: .zip, .tar.gz	To: {}\nSupported: .zip, .tar.gz
Dokončete přihlášení v Terminálu a prohlížeči, pak klikněte na Obnovit.	Finish the login in Terminal and the browser, then click Refresh.
Dokumenty	Documents
Domů	Home
Druh	Kind
Druh (nextcloud, owncloud, other)	Type (nextcloud, owncloud, other)
Dva panely, Tab přepíná mezi nimi. Šipky, Enter a Backspace se pohybují po složkách.	Two panels, Tab switches between them. Arrows, Enter and Backspace move through folders.
Dva panely, záložky, ovládání z klávesnice, F2 až F8, označování, rychlé hledání, vrácení operací.	Two panels, tabs, keyboard control, F2 to F8, marking, quick search, undo of operations.
Dvoupanelový správce souborů	Two-panel file manager
Dělení souboru	Splitting a file
Dělení zrušeno.	Splitting cancelled.
Dělím {}…	Splitting {}…
Důvěřovat serveru bez ověření klíče/certifikátu	Trust the server without verifying its key/certificate
Editace „{}“: po uložení se změny nahrají na server	Editing "{}": changes are uploaded to the server after saving
Editace „{}“: po uložení se změny zapíšou do archivu	Editing “{}”: changes are written to the archive when you save
Editovat	Edit
F3 zobrazí soubor, F4 ho upraví (na serveru se změny po uložení nahrají zpět).	F3 views a file, F4 edits it (on a server the changes are uploaded back after saving).
F5 a F6 kopírují a přesouvají do druhého panelu, F7 vytvoří složku, F8 smaže do koše.	F5 and F6 copy and move to the other panel, F7 creates a folder, F8 deletes to the trash.
FTP, FTP+TLS a SFTP (heslo i klíč), síťové disky a sdílené složky.	FTP, FTP+TLS and SFTP (password or key), network drives and shared folders.
Fialová	Violet
Filtr podle typu souboru	File type filter
Filtr podle typu souboru: tlačítko v panelu ukáže jen obrázky, dokumenty, hudbu, video nebo archivy.	File type filter: the button in the panel shows only images, documents, music, video or archives.
Filtr „{}“: {} z {} položek (Esc zruší)	Filter "{}": {} of {} items (Esc cancels)
GitHub vrátil kód {}.	GitHub returned code {}.
Google Disk	Google Drive
Grafit	Graphite
Heslo	Password
Heslo archivu (volitelné)	Archive password (optional)
Heslo klíče	Key password
Heslo pro přidané soubory	Password for the added files
Historie operací	Operation history
Historie operací: seznam kopírování, přesunů, mazání a přejmenování; kopírování jde zopakovat, ostatní vrátit zpět (menu Nástroje).	Operation history: a list of copies, moves, deletions and renames; copies can be repeated, the rest undone (Tools menu).
Historie operací…	Operation history…
Hledat	Search
Hledat duplicity	Find duplicates
Hledat i uvnitř archivů (stáhnou se archivy do 50 MB, nejvýše 15)	Also search inside archives (archives up to 50 MB are downloaded, at most 15)
Hledat i uvnitř archivů (zip, tar, 7z, rar…)	Also search inside archives (zip, tar, 7z, rar…)
Hledat i ve složce druhého panelu	Also search the other panel's folder
Hledat soubory	Search files
Hledat už při psaní (z rychlého indexu)	Search as you type (from the fast index)
Hledám…	Searching…
Hledání duplicit	Duplicate search
Hledání duplicit funguje jen v lokálních složkách.	Duplicate search works only in local folders.
Hledání souborů podle názvu a obsahu, hledání duplicit.	Searching files by name and content, finding duplicates.
Hledání uvnitř archivů a v obsahu souborů na serveru a v cloudu.	Search inside archives and in the contents of files on servers and in the cloud.
Hledání: už při psaní z indexu, uvnitř archivů i na serveru a v cloudu.	Search: as you type from the index, inside archives, also on servers and in the cloud.
Hotovo	Done
Hromadné přejmenování	Batch rename
Hromadné přejmenování ({} položek)	Batch rename ({} items)
ID účtu nebo klíče	Account or key ID
Instalace se nezdařila: {}	The installation failed: {}
Jazyk (po restartu)	Language (after restart)
Je dostupná nová verze	A new version is available
Je dostupná nová verze Budis Commanderu. Nainstalujete ji v menu Nápověda → Aktualizovat aplikaci.	A new version of Budis Commander is available. Install it from Help → Update the application.
Je dostupná nová verze Budis Commanderu. Nainstalujete ji v menu Nástroje → Aktualizovat aplikaci.	A new version of Budis Commander is available. Install it from the Tools menu → Update application.
Jednosměrně (zdroj → cíl)	One-way (source → target)
Jednosměrně: zdroj se kopíruje do cíle. Cíl v cloudu zapište jako rclone:název:/cesta (úložiště přidáte v menu Nástroje → Cloudová úložiště).	One way: the source is copied to the target. Write a cloud target as rclone:name:/path (add storage in the Tools menu → Cloud storage).
Jednou denně	Once a day
Jen archivy	Archives only
Jen dokumenty	Documents only
Jen hudba	Music only
Jen obrázky	Images only
Jen rozdíly	Differences only
Jen ručně	Manual only
Jen složky	Folders only
Jen smazané	Deleted only
Jen soubory	Files only
Jen video	Video only
Jiná aplikace…	Other application…
Každou hodinu	Every hour
Každých 15 minut	Every 15 minutes
Každých 30 minut	Every 30 minutes
Každých 5 minut	Every 5 minutes
Každých 6 hodin	Every 6 hours
Klepnutím sadu otevřete. Sada pamatuje záložky obou panelů.	Click a set to open it. A set remembers the tabs of both panels.
Kliknutím se v aktivním panelu otevře složka souboru.	Clicking opens the file's folder in the active panel.
Klávesové zkratky (⌘ + znak)	Keyboard shortcuts (⌘ + character)
Klávesy F2–F8, Tab, šipky a ⌘1–9 (záložky) jsou pevné. Změna zkratek se projeví hned.	The keys F2–F8, Tab, arrows and ⌘1–9 (tabs) are fixed. Shortcut changes apply immediately.
Klíč	Key
Kontrola aktualizací se nezdařila (je Mac online?):\n\n{}	The update check failed (is the Mac online?):\n\n{}
Kontrolní součty	Checksums
Kontrolní součty (MD5, SHA)	Checksums (MD5, SHA)
Kontrolní součty se počítají jen z lokálních souborů.	Checksums are calculated only for local files.
Kopírovat	Copy
Kopírovat cestu	Copy path
Kopírovat cestu složky	Copy folder path
Kopírovat do druhého panelu	Copy to the other panel
Kopírovat nebo přepsat: {} souborů ({})\nVytvořit složek: {}\n	Copy or overwrite: {} files ({})\nCreate folders: {}\n
Kopírovat název	Copy name
Kopírovat soubory do schránky	Copy files to clipboard
Kopírovat vše	Copy all
Kopírování	Copy
Kopírování {} do {}	Copy of {} to {}
Kopírování: {} → {}	Copy: {} → {}
Kopíruji: {}	Copying: {}
Les	Forest
Maska	Mask
Maska názvu	Name mask
Maska: [N] = název bez přípony, [E] = přípona, [C] = čítač.	Mask: [N] = name without extension, [E] = extension, [C] = counter.
Mazat v cíli soubory, které ve zdroji nejsou	Delete files in the target that are not in the source
Mazání	Deleting
Mažu {}	Deleting {}
Mažu: 	Deleting: 
Microsoft OneDrive	Microsoft OneDrive
Miniatury souborů	File thumbnails
Miniatury souborů místo ikon	File thumbnails instead of icons
Máte nejnovější verzi.	You have the latest version.
Nahradit	Replace
Nahrát	Upload
Nahrát změny na server?	Upload changes to the server?
Nahrávám změny „{}“ na server…	Uploading changes of "{}" to the server…
Nahrávám: 	Uploading: 
Nainstalovat adb	Install adb
Nainstalovat rclone	Install rclone
Nalezeno skupin: {}	Groups found: {}
Nalezeno {}, prohledaných složek {}	Found {}, folders searched {}
Nalezeno {}, prohledaných složek {}, přečteno souborů {}	Found {}, folders searched {}, files read {}
Nalezeno {}, prohledaných složek {}, přečteno souborů {} (další se kvůli limitu nečetly)	Found {}, folders searched {}, files read {} (more were not read because of the limit)
Nalezeno {}, prohledáno {}	Found {}, searched {}
Naposledy navštívené	Recently visited
Např. *.jpg	E.g. *.jpg
Nastavení	Settings
Nastavit datum a čas změny	Set modification date and time
Nastavit práva (osmičkově, např. 644 nebo 755)	Set permissions (octal, e.g. 644 or 755)
Nastavit úložiště…	Set up storage…
Nastavuji atributy…	Setting attributes…
Nechte prázdné pro archiv bez hesla. Zip používá starší šifrování (dobré proti náhodnému nahlédnutí, ne proti útočníkovi); heslo je po dobu balení vidět v seznamu procesů.	Leave empty for an archive without a password. Zip uses older encryption (good against casual peeking, not against an attacker); the password is visible in the process list while packing.
Nechte prázdné pro soubory bez hesla.	Leave empty for files without a password.
Nejdřív nainstalujte rclone.	Install rclone first.
Nejdřív zkusit rychlý index Spotlight (jen indexované složky)	Try the fast Spotlight index first (indexed folders only)
Nejmenší velikost (kB)	Minimum size (kB)
Není co vracet.	Nothing to undo.
Není žádný přerušený přenos.	There is no interrupted transfer.
Neshoduje se	Does not match
Nic k zobrazení.	Nothing to show.
Novou verzi stáhnout a nainstalovat automaticky (projeví se po restartu)	Download and install new versions automatically (takes effect after a restart)
Novou verzi stáhnout a nainstalovat automaticky (při ukončení aplikace)	Download and install new versions automatically (when the app quits)
Nová složka	New folder
Nová složka „{}“	New folder “{}”
Nová synchronizace	New sync
Nová verze je nainstalovaná, projeví se po restartu aplikace. Předchozí verzi vrátíte v menu Nápověda.	The new version is installed and takes effect after the app restarts. You can go back to the previous one in the Help menu.
Nová verze je stažená a nainstaluje se při ukončení aplikace. Předchozí verzi vrátíte v menu Nápověda.	The new version is downloaded and will be installed when the app quits. You can go back to the previous one in the Help menu.
Nová záložka	New tab
Nová záložka (⌘T)	New tab (⌘T)
Nové přenosy (F5, F6) se řadí do fronty a běží postupně. Zrušenou nebo selhanou úlohu lze spustit znovu.	New transfers (F5, F6) are queued and run one after another. A cancelled or failed job can be run again.
Nový	New
Nový adr.	New dir.
Nový adresář	New directory
Náhled souboru na serveru: stiskněte F3.	Preview of a file on the server: press F3.
Nápověda	Help
Nástroje	Tools
Návrat se nezdařil:\n\n	Going back failed:\n\n
Název	Name
Název archivu musí končit na .zip, .tar.gz nebo .tgz.	The archive name must end with .zip, .tar.gz or .tgz.
Název smí obsahovat jen písmena, číslice, pomlčku a podtržítko.	The name may only contain letters, digits, a hyphen and an underscore.
O aplikaci	About
O aplikaci…	About…
Oblíbené	Favorites
Oblíbené a poslední složky	Favorites and recent folders
Obnovení z Koše přímo v aplikaci (Windows: celý Koš, Mac: soubory smazané aplikací).	Restore from the Trash inside the app (Windows: the whole Recycle Bin, Mac: files deleted by the app).
Obnovit	Refresh
Obnovit výchozí zkratky	Restore default shortcuts
Obnovit z Koše	Restore from Trash
Obnovit z Koše…	Restore from Trash…
Obousměrně (nové a novější soubory na obě strany, nic se nemaže)	Two-way (new and newer files go both ways, nothing is deleted)
Obsahuje	Contains
Oceán	Ocean
Odebrat z oblíbených	Remove from favorites
Odkaz jde vytvořit jen v panelu připojeném ke cloudu (menu Nástroje → Cloudová úložiště).	A link can only be created in a panel connected to the cloud (Tools → Cloud storage).
Odkaz jde vytvořit jen v panelu připojeném ke cloudu (menu Servery → Cloudová úložiště).	A link can only be created in a panel connected to the cloud (Servers → Cloud storage).
Odkaz je ve schránce: {}	The link is in the clipboard: {}
Odkaz se nepodařilo vytvořit (úložiště ho možná neumí):\n\n{}	The link could not be created (the storage may not support it):\n\n{}
Odpojit od serveru	Disconnect from server
Odznačeno: 	Unmarked: 
Odznačit	Unmark
Odznačit podle masky	Unmark by mask
Operace	Operation
Otevře se Terminál s programem rclone a prohlížeč s přihlášením ke službě. Po dokončení klikněte na Obnovit.	Terminal with rclone and a browser with the service login will open. When done, click Refresh.
Otevřít	Open
Otevřít v aplikaci	Open with
Označeno {} z {}, {}	Marked {} of {}, {}
Označeno: 	Marked: 
Označit	Mark
Označit podle masky	Mark by mask
Označit podle podmínek	Mark by conditions
Označit podle podmínek…	Mark by conditions…
Označit stejnou příponu	Mark same extension
Označit vše	Mark all
Označování souborů, označení podle masky a vrácení posledních operací.	Marking files, marking by mask and undo of the last operations.
Označte soubory v aktivním panelu a v druhém panelu postavte kurzor na archiv .zip.	Mark files in the active panel and place the cursor on a .zip archive in the other panel.
Očekávaný součet (vložte pro ověření)	Expected sum (paste to verify)
Paměť	Memory
Panel rychlého náhledu	Quick view panel
Panely mezitím přešly do jiných složek. Vraťte je do původních a pokračujte z menu Nástroje.	The panels have moved to other folders in the meantime. Return them to the original ones and continue from the Tools menu.
Plocha	Desktop
Podle systému	Follow system
Podporované: smb://, afp://, nfs://. O přihlášení se postará systém.	Supported: smb://, afp://, nfs://. The system takes care of the login.
Pokračovat	Resume
Pokračovat v přerušeném přenosu	Resume interrupted transfer
Pokročilé nastavení (rclone config)…	Advanced setup (rclone config)…
Porovnat adresáře	Compare directories
Porovnat adresáře podle obsahu	Compare directories by content
Porovnat dva soubory (obsah)	Compare two files (content)
Porovnání adresářů a souborů, hromadné přejmenování, kontrolní součty.	Comparing directories and files, batch renaming, checksums.
Porovnání adresářů a souborů, zrcadlení, hromadné přejmenování.	Comparing directories and files, mirroring, batch renaming.
Porovnání souborů	File comparison
Porovnání souborů funguje jen mezi lokálními soubory.	File comparison works only between local files.
Porovnání: vlevo označeno {}, vpravo {} (chybějící, novější nebo jiné). Zkopírujte je klávesou F5.	Comparison: {} marked on the left, {} on the right (missing, newer or different). Copy them with F5.
Porovnávám obsah ({}/{})…	Comparing content ({}/{})…
Porovnávám obsah {} souborů…	Comparing the content of {} files…
Porovnávám se serverem…	Comparing with the server…
Porovnávám…	Comparing…
Poskytovatel (AWS, Minio, Wasabi…)	Provider (AWS, Minio, Wasabi…)
Postavte kurzor na první díl souboru (název končí na .001).	Place the cursor on the first part of the file (the name ends with .001).
Postavte kurzor v obou panelech na soubor, který chcete porovnat.	Place the cursor on the file you want to compare in both panels.
Posunout sloupec doleva	Move column left
Posunout sloupec doprava	Move column right
Použít	Apply
Použít toto rozložení pro všechny složky	Use this layout for all folders
Počítače v síti (SMB)	Computers on the network (SMB)
Počítám rozdíly…	Calculating differences…
Pravé tlačítko myši: nabídka s funkcemi podle toho, co je pod ukazatelem (soubor, složka, archiv, více označených, prázdné místo).	Right-click menu: functions depend on what is under the pointer (file, folder, archive, several marked items, empty space).
Pravé tlačítko: Otevřít v programu nebo aplikaci; ve Windows i Spustit jako správce.	Right-click: Open with a program or app; on Windows also Run as administrator.
Proces bude okamžitě ukončen. Neuložená práce se ztratí.	The process will be ended immediately. Unsaved work will be lost.
Procesu se pošle požadavek na ukončení.	The process is asked to quit.
Procházím složky…	Browsing folders…
Program adb nebyl nalezen. Nainstalujte ho v okně Telefon.	adb was not found. Install it in the Phone window.
Program adb zatím není nainstalovaný. Klikněte na „Nainstalovat adb“ (stáhne se od Googlu).	adb is not installed yet. Click "Install adb" (it is downloaded from Google).
Program rclone nebyl nalezen. Nainstalujte ho příkazem „brew install rclone“ nebo zadejte jeho cestu.	rclone was not found. Install it with "brew install rclone" or enter its path.
Program rclone nebyl nalezen. Nainstalujte ho v Terminálu příkazem „brew install rclone“ (nebo z rclone.org) a klikněte na Obnovit.	rclone was not found. Install it in Terminal with "brew install rclone" (or from rclone.org) and click Refresh.
Program rclone není nainstalovaný.	rclone is not installed.
Program rclone zatím není nainstalovaný. Klikněte na „Nainstalovat rclone“.	rclone is not installed yet. Click "Install rclone".
Prohledá se: 	Searched: 
Prohledáno souborů: {}	Files searched: {}
Projekt: {}	Project: {}
Protokol	Protocol
Provést	Run
Práva	Permissions
Práva zadejte osmičkově, např. 644.	Enter the permissions in octal, e.g. 644.
Právě probíhá jiná operace.	Another operation is running.
Prázdný soubor.	Empty file.
Průhledné okno ve stylu skla	Glass-style translucent window
Průhledné okno ve stylu skla (jde vypnout v Nastavení).	Glass-style translucent window (can be turned off in Settings).
Písmena	Letters
Předchozí verze je připravená	The previous version is ready
Předchozí verze není k dispozici. Uloží se při příští aktualizaci.	The previous version is not available. It is saved at the next update.
Přehled funkcí	Feature overview
Přehled funkcí…	Feature overview…
Přejmenovat	Rename
Přejmenování „{}“ na „{}“	Rename “{}” to “{}”
Přejmenovávám {}	Renaming {}
Přejít do složky panelu	Go to the panel folder
Přenos zrušen. Zbylé položky ({}) jde dokončit z menu Nástroje.	The transfer was cancelled. The remaining items ({}) can be finished from the Tools menu.
Přepsat	Overwrite
Přepsat vše	Overwrite all
Přeskočit	Skip
Přesun	Move
Přesun {} do {}	Move of {} to {}
Přesun: {} → {}	Move: {} → {}
Přesunout	Move
Přesunout do druhého panelu	Move to the other panel
Přesunout do koše (jen v cíli): {}	Move to Trash (destination only): {}
Přesunout {} do koše?	Move {} to the Trash?
Přesunout {} souborů do koše?	Move {} files to the Trash?
Převrátit označení	Invert marks
Při startu zkontrolovat, jestli je dostupná nová verze	Check for a new version at startup
Přidat	Add
Přidat aktuální složku	Add current folder
Přidat do archivu v druhém panelu	Add to the archive in the other panel
Přidat příkaz	Add command
Přidat {} do „{}“?	Add {} to "{}"?
Přidat úložiště	Add storage
Přidat…	Add…
Přidáno do archivu: {}	Added to archive: {}
Přidáno do fronty (ve frontě: {})	Added to the queue (in queue: {})
Přidávám do {}…	Adding to {}…
Připojené disky	Connected disks
Připojení k FTP, FTP+TLS a SFTP serverům, hesla se ukládají bezpečně.	Connecting to FTP, FTP+TLS and SFTP servers, passwords are stored securely.
Připojení k telefonu se nezdařilo:\n\n{}	Could not connect to the phone:\n\n{}
Připojení k telefonu se nezdařilo:\n\n{}	Could not connect to the phone:\n\n{}
Připojení k úložišti „{}“ se nezdařilo:\n\n{}	Connecting to storage "{}" failed:\n\n{}
Připojit	Connect
Připojit k serveru	Connect to server
Připojuji {}…	Connecting {}…
Příkaz	Command
Příkaz skončil s kódem {}	The command finished with code {}
Příkazový řádek	Command line
Příkazy fungují jen v lokální složce.\n	Commands work only in a local folder.\n
Přípona	Extension
Quick Look funguje jen u lokálních souborů.	Quick Look works only for local files.
Region (např. eu-central-1)	Region (e.g. eu-central-1)
Regulární výraz (v náhradě lze použít $1, $2…)	Regular expression (use $1, $2… in the replacement)
Rozbaleno do {}	Extracted to {}
Rozbalení stažené aplikace selhalo.	Unpacking the downloaded application failed.
Rozbalit	Extract
Rozbalit archiv	Extract archive
Rozbalit „{}“?	Extract "{}"?
Rozbaluji {}…	Extracting {}…
Rozdílných řádků: {}	Different lines: {}
Rozdělit	Split
Rozdělit soubor na díly	Split file into parts
Rozdělit „{}“	Split "{}"
Rozměry/délka	Dimensions/duration
Rozšířené označování: podle masky, druhu, velikosti a data změny, stejná přípona, převrácení označení.	Advanced marking: by mask, kind, size and modification date, same extension, invert marks.
Rychlé hledání psaním písmen, filtr názvů a filtr podle typu souboru (tlačítko v panelu).	Quick search by typing letters, a name filter and a file type filter (button in the panel).
Rychlé hledání přes index (Spotlight na Macu, Windows Search ve Windows): stačí nechat zaškrtnuté v okně hledání.	Fast search through the index (Spotlight on Mac, Windows Search on Windows): just leave it ticked in the search window.
Rychlý náhled	Quick view
Rychlý náhled, hex, kontrolní součty, dělení a slepování souborů, příkazový řádek.	Quick view, hex, checksums, splitting and joining files, command line.
Rychlý náhled, příkazový řádek a vlastní příkazy v menu.	Quick view, command line and your own commands in the menu.
Rychlý přístup	Quick access
Sada záložek „{}“ otevřena.	Tab set "{}" opened.
Sady záložek	Tab sets
Sady záložek: uložte rozložení obou panelů pod jménem a otevřete ho jedním klikem.	Tab sets: save the layout of both panels under a name and open it with one click.
Sady záložek…	Tab sets…
Samostatný sloupec s příponou	Separate column with the extension
Schránka neobsahuje soubory.	The clipboard contains no files.
Schránka souborů funguje jen pro lokální soubory.	The file clipboard works only for local files.
Sdílet odkazem (cloud)	Share link (cloud)
Server	Server
Server: {}\n\n{}	Server: {}\n\n{}
Sestavení z commitu: {}	Built from commit: {}
Shoduje se	Matches
Skryté soubory	Hidden files
Skrytý	Hidden
Skrýt	Hide
Skrýt terminál	Hide terminal
Slepení souboru	Joining a file
Slepit díly souboru	Join file parts
Slepování zrušeno.	Joining cancelled.
Slepuji {}…	Joining {}…
Sloupec Práva	Permissions column
Sloupec Rozměry obrázku / délka zvuku a videa	Image dimensions / audio and video duration column
Sloupec Vlastník	Owner column
Složka\n{} položek, {}	Folder\n{} items, {}
Složka „{}“ neexistuje.	Folder "{}" does not exist.
Složka: 	Folder: 
Složka: {}	Folder: {}
Složky jsou shodné, není co zrcadlit.	The folders are identical, there is nothing to mirror.
Služba	Service
Smazat	Delete
Smazat na serveru: {}	Delete on server: {}
Smazat sadu	Delete the set
Smazání do Koše: {}	Deleted to Trash: {}
Směr	Direction
Soubor je příliš velký pro náhled (nad 100 MB). Zkopírujte ho do lokálního panelu.	The file is too large for a preview (over 100 MB). Copy it to a local panel.
Soubor nelze přečíst.	The file cannot be read.
Soubor rozdělen na {} dílů.	The file was split into {} parts.
Soubor se nepodařilo přečíst.	The file could not be read.
Soubor slepen: {}	File joined: {}
Soubory i složky	Files and folders
Soubory jsou příliš velké pro porovnání po řádcích (nad 3 MB nebo 8000 řádků) a liší se.	The files are too large for a line comparison (over 3 MB or 8000 lines) and they differ.
Soubory jsou příliš velké pro porovnání po řádcích, ale jsou shodné.	The files are too large for a line comparison, but they are identical.
Soubory jsou shodné.	The files are identical.
Spouštět	Run
Spočítat velikosti složek	Calculate folder sizes
Správce procesů	Process manager
Správce procesů: přehled běžících programů, řazení a ukončení (menu Nástroje).	Process manager: running programs, sorting and quitting them (Tools menu).
Správce procesů…	Process manager…
Správce přenosů	Transfer manager
Správce přenosů: kopírování a přesuny běží na pozadí, jde je zrušit a zopakovat.	Transfer manager: copies and moves run in the background and can be cancelled or retried.
Správce přenosů…	Transfer manager…
Spustit teď	Run now
Spustit znovu	Run again
Stahuji Android platform-tools z dl.google.com…	Downloading Android platform-tools from dl.google.com…
Stahuji aktualizaci…	Downloading the update…
Stahuji náhled {}…	Downloading preview of {}…
Stahuji rclone z oficiálního webu rclone.org…	Downloading rclone from the official rclone.org site…
Stahuji {}…	Downloading {}…
Stahuji: 	Downloading: 
Stažená aplikace je prázdná.	The downloaded application is empty.
Stažené	Downloads
Stažení selhalo (kód {}).	The download failed (code {}).
Stažený archiv neobsahuje aplikaci.	The downloaded archive does not contain the application.
Stáhne se {} a aplikace se po výměně restartuje.	{} will be downloaded and the application restarts after the swap.
Stáhnout	Download
Stáhnout změny ze serveru?	Download changes from the server?
Světlý	Light
Symbolický odkaz jde vytvořit jen na lokální soubor nebo složku.	A symbolic link can only be created for a local file or folder.
Symbolický odkaz na „{}“	Symbolic link to "{}"
Synchronizace na pozadí	Background sync
Synchronizace na pozadí: uložené úlohy se spouštějí podle časovače.	Background sync: saved tasks run on a timer.
Synchronizace na pozadí…	Background sync…
Synchronizace „{}“: {}	Sync "{}": {}
Synchronizace: {}	Sync: {}
Synchronizuji…	Syncing…
Systémový Quick Look	System Quick Look
Síť	Network
Síť a disky	Network and drives
Telefon (Android)	Phone (Android)
Telefon (Android)…	Phone (Android)…
Telefon s Androidem: procházení a přenos souborů přes adb (iPhone není podporován).	Android phone: browse and transfer files via adb (iPhone is not supported).
Terminál	Terminal
Terminál zde	Terminal here
Teď	Now
Teď nainstalovaná verze se uloží a půjde na ni přepnout zpět stejným příkazem.	The currently installed version is kept, and you can switch back to it with the same command.
Teď nainstalovaná verze se uloží a půjde na ni přepnout zpět stejným příkazem. Aplikace se restartuje.	The currently installed version is kept, and you can switch back to it with the same command. The app will restart.
Tisk…	Print…
Tlačítková lišta uživatelských příkazů	Button bar of user commands
Tmavý	Dark
Trvale smazat {} ze serveru?	Delete {} from the server permanently?
Tuto akci nelze vrátit zpět.	This action cannot be undone.
Tuto aplikaci nelze vyměnit za sebe samu (nespustili jste samostatný BudisCommander.exe).	This application cannot replace itself (you did not run the standalone BudisCommander.exe).
Třídit podle typu souboru	Sort by file type
U SFTP lze zadat soukromý klíč (RSA/ECDSA); heslo je pak heslem ke klíči.	For SFTP you can enter a private key (RSA/ECDSA); the password is then the key's passphrase.
Ukončit	End
Ukončit „{}“ (PID {})?	End "{}" (PID {})?
Ukázat	Show
Ukázat ve Finderu	Show in Finder
Uloženo do archivu: {}	Saved to the archive: {}
Uloženo na server: {}	Saved to server: {}
Uložené servery	Saved servers
Uložit	Save
Uložit .sha256 vedle souborů	Save .sha256 next to the files
Uložit aktuální…	Save current…
Uložit heslo do Klíčenky	Save the password to the Keychain
Uložit sadu záložek	Save tab set
Upravit synchronizaci	Edit sync
Upravit…	Edit…
Upravovat soubory uvnitř archivu jde jen u zip (a jar). Ostatní formáty jsou otevřené jen pro čtení; soubor zkopírujte ven (F5).	Files inside an archive can only be edited in zip (and jar). Other formats are read-only; copy the file out (F5).
Uživatel	User
Uživatelské příkazy	User commands
Uživatelské příkazy fungují jen v lokálních složkách.	User commands work only in local folders.
Uživatelské příkazy…	User commands…
V telefonu zapněte Nastavení → Možnosti pro vývojáře → Ladění přes USB, připojte ho kabelem a potvrďte v telefonu dotaz na povolení. Soubory telefonu se pak otevřou v aktivním panelu.	On the phone turn on Settings → Developer options → USB debugging, connect it with a cable and confirm the permission prompt on the phone. The phone files then open in the active panel.
VELKÁ	UPPER
Velikost	Size
Velikost do	Size to
Velikost jednoho dílu v MB (např. 100, 700, 4000).\nDíly se uloží do {}	Size of one part in MB (e.g. 100, 700, 4000).\nThe parts are saved to {}
Velikost od	Size from
Velikost písma: {} pt	Font size: {} pt
Velikost složky	Folder size
Velikost zapište např. 500, 10 KB nebo 1,5 MB.	Enter a size such as 500, 10 KB or 1.5 MB.
Velikosti	Sizes
Vestavěný terminál v okně aplikace (Ctrl+`).	Built-in terminal inside the app window (Ctrl+`).
Vestavěný terminál ve Windows je plnohodnotný (ConPTY): fungují i vim, top, ssh a další celoobrazovkové programy.	The built-in terminal on Windows is a full terminal (ConPTY): vim, top, ssh and other full-screen programs work.
Vlastník	Owner
Vložit soubory ze schránky	Paste files from clipboard
Vpřed v historii	Forward in history
Vráceno položek: {}	Items reverted: {}
Vráceno: {}	Undone: {}
Vrácení se nepodařilo:\n{}	Undo failed:\n{}
Vrátit	Go back
Vrátit poslední operaci	Undo last operation
Vrátit předchozí verzi?	Go back to the previous version?
Vrátit předchozí verzi…	Go back to the previous version…
Vrátit zpět	Undo
Vrátit: {}	Undo: {}
Vyberte aplikaci	Choose an application
Vyberte aspoň jeden soubor.	Select at least one file.
Vyberte lokální soubor s archivem (zip, tar, tar.gz, tar.bz2, 7z…).	Select a local archive file (zip, tar, tar.gz, tar.bz2, 7z…).
Vyberte lokální soubor, který chcete rozdělit.	Select the local file you want to split.
Vyberte soukromý SSH klíč	Select a private SSH key
Vyberte úložiště a připojte ho do aktivního panelu. Nové přidáte níže; program rclone si aplikace umí sama stáhnout.	Pick a storage and connect it to the active panel. Add a new one below; the application can download rclone by itself.
Vybrané duplicity půjde vrátit příkazem Vrátit poslední operaci.	Selected duplicates can be restored with Undo last operation.
Vybrat přebytečné (ponechat nejstarší)	Select redundant (keep the oldest)
Vybrat…	Choose…
Vyjmout soubory do schránky	Cut files to clipboard
Vyjmuto: {} položek. Vložte klávesou ⌘V.	Cut: {} items. Paste with ⌘V.
Vynutit ukončení	Force quit
Vyplněné podmínky platí zároveň. Složky nemají velikost, takže při zadané velikosti se vynechají.	All filled-in conditions apply together. Folders have no size, so they are skipped when a size is given.
Vytvořit	Create
Vytvořit symbolický odkaz	Create symbolic link
Vytvoří se v {}	It will be created in {}
Vytvářím odkaz…	Creating the link…
Vyčistit historii	Clear history
Vyčistit hotové	Clear finished
Vzhled	Appearance
Výchozí	Default
Výchozí (modrá)	Default (blue)
Výchozí rozložení této složky	Default layout for this folder
Vývojové sestavení (bez označení verze)	Development build (no version mark)
Včetně obsahu složek	Including folder contents
Včetně skrytých	Including hidden
Včetně skrytých souborů	Including hidden files
Všechny operace	All operations
Všechny podsložky najednou (Branch view)	All subfolders at once (Branch view)
Všechny soubory	All files
WebDAV (Nextcloud, ownCloud…)	WebDAV (Nextcloud, ownCloud…)
Z archivu lze soubory jen kopírovat (F5), ne přesouvat.	Files can only be copied (F5) from an archive, not moved.
Zabalit	Pack
Zabalit do archivu	Pack into archive
Zadejte cíl.	Enter a target.
Zadejte název.	Enter a name.
Zadejte velikost dílu jako číslo v MB.	Enter the part size as a number in MB.
Zapíše záložky obou panelů (záložky na serveru se vynechají).	Saves the tabs of both panels (tabs on a server are left out).
Zastaveno.	Stopped.
Zastavit	Stop
Zatím není nastavené žádné úložiště. Klikněte na „Nastavit úložiště…“.	No storage is configured yet. Click "Set up storage…".
Zatím není nastavené žádné úložiště. Přidejte ho níže.	No storage is set up yet. Add one below.
Zatím není uložená žádná sada. Nastavte záložky v obou panelech a klikněte na „Uložit aktuální…“.	No set is saved yet. Set up the tabs in both panels and click "Save current…".
Zatím není žádná úloha. Klikněte na „Přidat…“.	There is no task yet. Click "Add…".
Zatím žádné přenosy.	No transfers yet.
Zavřít	Close
Zavřít ostatní záložky	Close other tabs
Zavřít záložku	Close tab
Začít v	Start at
Zdroj	Source
Zdrojová a cílová složka jsou stejné.	The source and destination folders are the same.
Zdrojová a cílová složka se nesmí překrývat.	The source and destination folders must not overlap.
Zdrojová složka neexistuje.	The source folder does not exist.
Zdrojová složka „{}“ neexistuje.	The source folder "{}" does not exist.
Zdrojový a cílový adresář jsou stejné.	The source and destination directories are the same.
Zjišťuji, jestli je nová verze…	Checking for a new version…
Zkontrolovat aktualizace	Check for updates
Zkopírováno do schránky.	Copied to clipboard.
Zkopírováno znovu: {}	Copied again: {}
Zkopírováno: {} položek. Vložte klávesou ⌘V.	Copied: {} items. Paste with ⌘V.
Zkratky všech funkcí jsou vidět v menu a jdou změnit v Nastavení.	The shortcuts of all functions are shown in the menus and can be changed in Settings.
Změny „{}“ se nepodařilo nahrát:\n{}	Changes to "{}" could not be uploaded:\n{}
Změny „{}“ se nepodařilo zapsat do archivu:\n{}	Changes to “{}” could not be written to the archive:\n{}
Změní se jen štítky, které mají všechny vybrané položky společné; ostatní zůstanou.	Only tags that all selected items have in common are changed; the others stay.
Změněno	Modified
Změněno do	Modified to
Změněno od	Modified from
Zobrazit	View
Zopakovat	Repeat
Zopakovat jde kopírování. Vrátit jde kopírování (kopie jde do Koše), přesun, přejmenování, nová složka a smazání do Koše. Z Koše jdou obnovit jen soubory smazané touto aplikací.	Copies can be repeated. Copies (the copy goes to the Trash), moves, renames, new folders and deletions to the Trash can be undone. Only files deleted by this app can be restored from the Trash.
Zpět a vpřed v historii panelu: ⌘[ a ⌘].	Back and forward in the panel history: ⌘[ and ⌘].
Zpět v historii	Back in history
Zrcadlení	Mirroring
Zrcadlení hotovo.	Mirroring finished.
Zrcadlení mezi dvěma servery není podporováno. Jeden z panelů musí být místní složka.	Mirroring between two servers is not supported. One of the panels must be a local folder.
Zrcadlení mezi složkou a serverem (FTP, SFTP, cloud) v obou směrech.	Mirroring between a folder and a server (FTP, SFTP, cloud) in both directions.
Zrcadlení složky do druhého panelu, i mezi složkou a serverem.	Mirroring a folder to the other panel, also between a folder and a server.
Zrcadlení zrušeno.	Mirroring cancelled.
Zrcadlit	Mirror
Zrcadlit adresář do druhého panelu	Mirror the directory to the other panel
Zrcadlit „{}“ do „{}“?	Mirror "{}" to "{}"?
Zrušit	Cancel
Zrušit filtr (Esc)	Clear filter (Esc)
Zrušit označení	Clear marks
Zrušit výběr	Clear selection
Zrušit vše	Cancel all
Záložky v každém panelu a sady záložek, které si pamatují rozložení obou panelů.	Tabs in each panel and tab sets that remember the layout of both panels.
Západ slunce	Sunset
Zástupné znaky: %f soubor pod kurzorem, %n jeho název, %d složka panelu, %o složka druhého panelu, %F označené soubory (nebo soubor pod kurzorem), %% znak procenta. Cesty se samy uzavřou do uvozovek.	Placeholders: %f file under the cursor, %n its name, %d panel folder, %o other panel folder, %F marked files (or the file under the cursor), %% a percent sign. Paths are quoted automatically.
[proces skončil, kód {}; Restartovat v liště nahoře]	[process ended, code {}; use Restart in the bar above]
beze změn	no changes
cesta k rclone (volitelné, jinak se hledá v PATH a v Homebrew)	path to rclone (optional, otherwise PATH and Homebrew are searched)
chyba	error
curl skončil s kódem {}	curl finished with code {}
hledat podle názvu nebo PID	search by name or PID
hotovo	done
iPhone a iPad: Apple neumožňuje přímý přístup k souborům přes kabel, použijte AirDrop nebo iCloud.	iPhone and iPad: Apple does not allow direct file access over a cable, use AirDrop or iCloud.
každých {} min	every {} min
kopírovat	copy
kopírování	copying
malá	lower
na cíl {}, ze cíle {} souborů ({})	to target {}, from target {} files ({})
na serveru jen podle názvu	on the server by name only
naposledy {}: {}	last {}: {}
např. *.jpg nebo část názvu	e.g. *.jpg or part of a name
např. *.jpg;*.png	e.g. *.jpg;*.png
např. 1 GB	e.g. 1 GB
např. 1. 1. 2025	e.g. 1 Jan 2025
např. 10 MB	e.g. 10 MB
např. 31. 12. 2025	e.g. 31 Dec 2025
např. Dokumenty na zálohu	e.g. Documents for backup
např. ftp.example.com	e.g. ftp.example.com
název úložiště (písmena a číslice)	storage name (letters and digits)
použito vícekrát	used more than once
povinné	required
probíhá	running
prázdné = anonymní	empty = anonymous
přejmenování „{}“	rename of "{}"
přesun	move
přesunout	move
příkaz (Enter spustí, Esc zavře)	command (Enter runs, Esc closes)
rclone listremotes selhal.	rclone listremotes failed.
rclone odkaz nevytvořil.	rclone did not create a link.
rclone skončil s kódem {}	rclone finished with code {}
ručně	manual
složka, nebo rclone:název:/cesta	folder, or rclone:name:/path
smazání {}	deleting {}
smb://server/sdílená-složka	smb://server/shared-folder
text v souboru (volitelné)	text in the file (optional)
text v souboru (čte se jen první MB, nejvýše 300 souborů)	text in file (only the first MB is read, at most 300 files)
ve frontě	queued
ve frontě: {}	in queue: {}
volitelné, např. ~/.ssh/id_rsa	optional, e.g. ~/.ssh/id_rsa
vráceno	reverted
vytvoření složky „{}“	creating folder "{}"
zkopírováno {} souborů ({})	copied {} files ({})
zrušeno	cancelled
{} položek	{} items
{} položek, {}	{} items, {}
{} z {} procesů	{} of {} processes
{} {} položek	{} {} items
{}: nelze {} adresář do sebe sama	{}: cannot {} a directory into itself
{}: {} z {} položek	{}: {} of {} items
Úlohy se spouštějí podle nastavené doby, dokud je aplikace spuštěná. Běh a výsledek je vidět ve správci přenosů.	Tasks run on the set schedule while the application is running. The run and its result can be seen in the transfer manager.
Úložiště	Storage
Úložiště se nastavují programem rclone (příkaz „rclone config“). Tady stačí vybrat jedno z nich a připojit ho do aktivního panelu.	Storage is set up with the rclone program (the "rclone config" command). Here you just pick one and connect it to the active panel.
Úložiště „{}“ přidáno.	Storage "{}" added.
Úložiště „{}“ už existuje.	Storage "{}" already exists.
Červeně označené názvy jsou prázdné, duplicitní nebo už existují.	Names marked in red are empty, duplicate or already exist.
Čeština a angličtina (Nastavení, platí po restartu).	Czech and English (Settings, applies after a restart).
Čeština a angličtina, světlý a tmavý vzhled, nastavení sloupců a písma.	Czech and English, light and dark appearance, column and font settings.
Čítač	Counter
číslic {}	digits {}
Řazení klikem na záhlaví sloupce; řazení se pamatuje zvlášť pro každou složku.	Sorting by clicking a column header; the sorting is remembered separately for each folder.
Řazení podle typu souboru: klikněte na záhlaví Přípona nebo použijte menu.	Sorting by file type: click the Extension header or use the menu.
Řazení se pamatuje zvlášť pro každou složku (např. fotky podle data, hudba podle názvu).	Sorting is remembered separately for each folder (e.g. photos by date, music by name).
Štítky Finderu ({} položek)	Finder tags ({} items)
Štítky Finderu jde nastavit jen u lokálních souborů.	Finder tags can only be set for local files.
Štítky Finderu…	Finder tags…
Šířku sloupců změníte tažením za záhlaví, pořadí pravým tlačítkem na záhlaví; rozložení se pamatuje pro každou složku.	Drag a column header edge to change its width, right-click a header to reorder; the layout is remembered per folder.
Žádné duplicity.	No duplicates.
Žádný telefon nenalezen. Zkontrolujte kabel a ladění přes USB.	No phone found. Check the cable and USB debugging.
„{}“ již v cíli existuje	"{}" already exists in the destination
„{}“ už existuje. Přepsat?	"{}" already exists. Overwrite?
⌘V zavře	⌘V closes
"""#
}

/// Zkratka pro texty rozhraní: v angličtině vrátí překlad, jinak text beze změny.
func L(_ s: String) -> String { Tr.t(s) }
