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
 (jen pro čtení)	 (read-only)
(jen lokální složky)	(local folders only)
Adresa sdílení	Share address
Adresář nelze otevřít:\n{}\n\n{}	The directory cannot be opened:\n{}\n\n{}
Aktualizace funguje jen v aplikaci .app (ne při spuštění příkazem swift run).	Updates work only in the .app application (not when started with swift run).
Aktualizace se nezdařila:\n\n{}	The update failed:\n\n{}
Aktualizovat	Update
Aktualizovat aplikaci…	Update application…
Aplikace	Applications
Aplikace neběží jako balíček .app (spuštěná příkazem swift run).	The application is not running as an .app bundle (started with swift run).
Archiv	Archive
Archiv je chráněný heslem	The archive is password protected
Archiv je otevřený jen pro čtení, nelze do něj kopírovat.	The archive is open read-only, you cannot copy into it.
Archiv je otevřený jen pro čtení.	The archive is open read-only.
Archiv se zrcadlit nedá.	An archive cannot be mirrored.
Archiv vytvořen: {}	Archive created: {}
Archiv {}	Archive {}
Archiv „{}“ se nepodařilo otevřít:\n{}	Could not open archive "{}":\n{}
Archiv „{}“ už existuje. Přepsat?	Archive "{}" already exists. Overwrite?
Atributy a časy	Attributes and times
Atributy a časy ({} položek)	Attributes and times ({} items)
Atributy lze měnit jen u souborů na lokálním disku.	Attributes can only be changed for files on a local disk.
Atributy nastaveny.	Attributes set.
Automaticky počítat velikosti složek (na pozadí, jen lokální složky)	Calculate folder sizes automatically (in the background, local folders only)
Balení funguje jen na lokálním disku. Soubory ze serveru nejdřív zkopírujte (F5).	Packing works only on a local disk. Copy the files from the server first (F5).
Balím {}…	Packing {}…
Barevné štítky Finderu u názvů	Colored Finder tags next to names
Beze změny	No change
Binární soubory jsou shodné.	The binary files are identical.
Binární soubory se liší.	The binary files differ.
Budis Commander	Budis Commander
Chcete existující položku přepsat?	Do you want to overwrite the existing item?
Chyba	Error
Cloudová úložiště (rclone)	Cloud storage (rclone)
Cloudová úložiště (rclone)…	Cloud storage (rclone)…
Co udělat s {}?	What to do with {}?
Cíl: {}	Destination: {}
Další štítky (oddělené čárkou)	Other tags (comma separated)
Disk se nepodařilo připojit:\n{}	The disk could not be connected:\n{}
Disky	Volumes
Do koše	To Trash
Do koše ({})	To Trash ({})
Do koše: 	To Trash: 
Do koše: {}	To Trash: {}
Do složky „{}“ nelze zapisovat. Přesuňte aplikaci třeba do složky Aplikace ve vašem domovském adresáři.	The folder "{}" is not writable. Move the application to, for example, the Applications folder in your home directory.
Do: {}\nPodporováno: .zip, .tar.gz	To: {}\nSupported: .zip, .tar.gz
Dokumenty	Documents
Domů	Home
Dělení zrušeno.	Splitting cancelled.
Dělím {}…	Splitting {}…
Důvěřovat serveru bez ověření klíče/certifikátu	Trust the server without verifying its key/certificate
Editace „{}“: po uložení se změny nahrají na server	Editing "{}": changes are uploaded to the server after saving
Editovat	Edit
Filtr „{}“: {} z {} položek (Esc zruší)	Filter "{}": {} of {} items (Esc cancels)
GitHub vrátil kód {}.	GitHub returned code {}.
Heslo	Password
Heslo archivu (volitelné)	Archive password (optional)
Heslo klíče	Key password
Heslo pro přidané soubory	Password for the added files
Hledat	Search
Hledat duplicity	Find duplicates
Hledat i ve složce druhého panelu	Also search the other panel's folder
Hledat soubory	Search files
Hledám…	Searching…
Hledání duplicit	Duplicate search
Hledání duplicit funguje jen v lokálních složkách.	Duplicate search works only in local folders.
Hotovo	Done
Hromadné přejmenování	Batch rename
Hromadné přejmenování ({} položek)	Batch rename ({} items)
Jazyk (po restartu)	Language (after restart)
Je dostupná nová verze	A new version is available
Je dostupná nová verze Budis Commanderu. Nainstalujete ji v menu Nástroje → Aktualizovat aplikaci.	A new version of Budis Commander is available. Install it from the Tools menu → Update application.
Jen rozdíly	Differences only
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
Kopírovat nebo přepsat: {} souborů ({})\nVytvořit složek: {}\n	Copy or overwrite: {} files ({})\nCreate folders: {}\n
Kopírovat název	Copy name
Kopírovat soubory do schránky	Copy files to clipboard
Kopírovat vše	Copy all
Kopíruji: {}	Copying: {}
Maska	Mask
Maska: [N] = název bez přípony, [E] = přípona, [C] = čítač.	Mask: [N] = name without extension, [E] = extension, [C] = counter.
Mažu {}	Deleting {}
Mažu: 	Deleting: 
Miniatury souborů	File thumbnails
Miniatury souborů místo ikon	File thumbnails instead of icons
Máte nejnovější verzi.	You have the latest version.
Nahradit	Replace
Nahrát	Upload
Nahrát změny na server?	Upload changes to the server?
Nahrávám změny „{}“ na server…	Uploading changes of "{}" to the server…
Nahrávám: 	Uploading: 
Nalezeno skupin: {}	Groups found: {}
Nalezeno {}, prohledaných složek {}	Found {}, folders searched {}
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
Nejmenší velikost (kB)	Minimum size (kB)
Není co vracet.	Nothing to undo.
Není žádný přerušený přenos.	There is no interrupted transfer.
Neshoduje se	Does not match
Nic k zobrazení.	Nothing to show.
Nová záložka	New tab
Nová záložka (⌘T)	New tab (⌘T)
Nový	New
Nový adr.	New dir.
Nový adresář	New directory
Náhled souboru na serveru: stiskněte F3.	Preview of a file on the server: press F3.
Nástroje	Tools
Název	Name
Název archivu musí končit na .zip, .tar.gz nebo .tgz.	The archive name must end with .zip, .tar.gz or .tgz.
Oblíbené	Favorites
Oblíbené a poslední složky	Favorites and recent folders
Obnovit	Refresh
Obnovit výchozí zkratky	Restore default shortcuts
Obsahuje	Contains
Odebrat z oblíbených	Remove from favorites
Odpojit od serveru	Disconnect from server
Odznačit podle masky	Unmark by mask
Otevřít v aplikaci	Open in application
Označeno {} z {}, {}	Marked {} of {}, {}
Označit podle masky	Mark by mask
Označit vše	Mark all
Označte soubory v aktivním panelu a v druhém panelu postavte kurzor na archiv .zip.	Mark files in the active panel and place the cursor on a .zip archive in the other panel.
Očekávaný součet (vložte pro ověření)	Expected sum (paste to verify)
Panel rychlého náhledu	Quick view panel
Panely mezitím přešly do jiných složek. Vraťte je do původních a pokračujte z menu Nástroje.	The panels have moved to other folders in the meantime. Return them to the original ones and continue from the Tools menu.
Plocha	Desktop
Podle systému	Follow system
Podporované: smb://, afp://, nfs://. O přihlášení se postará systém.	Supported: smb://, afp://, nfs://. The system takes care of the login.
Pokračovat	Resume
Pokračovat v přerušeném přenosu	Resume interrupted transfer
Porovnat adresáře	Compare directories
Porovnat adresáře podle obsahu	Compare directories by content
Porovnat dva soubory (obsah)	Compare two files (content)
Porovnání souborů	File comparison
Porovnání souborů funguje jen mezi lokálními soubory.	File comparison works only between local files.
Porovnání: vlevo označeno {}, vpravo {} (chybějící, novější nebo jiné). Zkopírujte je klávesou F5.	Comparison: {} marked on the left, {} on the right (missing, newer or different). Copy them with F5.
Porovnávám obsah ({}/{})…	Comparing content ({}/{})…
Porovnávám obsah {} souborů…	Comparing the content of {} files…
Porovnávám se serverem…	Comparing with the server…
Porovnávám…	Comparing…
Postavte kurzor na první díl souboru (název končí na .001).	Place the cursor on the first part of the file (the name ends with .001).
Postavte kurzor v obou panelech na soubor, který chcete porovnat.	Place the cursor on the file you want to compare in both panels.
Použít	Apply
Počítače v síti (SMB)	Computers on the network (SMB)
Počítám rozdíly…	Calculating differences…
Procházím složky…	Browsing folders…
Program rclone nebyl nalezen. Nainstalujte ho příkazem „brew install rclone“ nebo zadejte jeho cestu.	rclone was not found. Install it with "brew install rclone" or enter its path.
Program rclone nebyl nalezen. Nainstalujte ho v Terminálu příkazem „brew install rclone“ (nebo z rclone.org) a klikněte na Obnovit.	rclone was not found. Install it in Terminal with "brew install rclone" (or from rclone.org) and click Refresh.
Prohledá se: 	Searched: 
Prohledáno souborů: {}	Files searched: {}
Protokol	Protocol
Práva	Permissions
Práva zadejte osmičkově, např. 644.	Enter the permissions in octal, e.g. 644.
Právě probíhá jiná operace.	Another operation is running.
Prázdný soubor.	Empty file.
Písmena	Letters
Přejmenovat	Rename
Přejmenovávám {}	Renaming {}
Přenos zrušen. Zbylé položky ({}) jde dokončit z menu Nástroje.	The transfer was cancelled. The remaining items ({}) can be finished from the Tools menu.
Přepsat	Overwrite
Přepsat vše	Overwrite all
Přeskočit	Skip
Přesunout	Move
Přesunout do koše (jen v cíli): {}	Move to Trash (destination only): {}
Přesunout {} do koše?	Move {} to the Trash?
Přesunout {} souborů do koše?	Move {} files to the Trash?
Při startu zkontrolovat, jestli je dostupná nová verze	Check for a new version at startup
Přidat	Add
Přidat aktuální složku	Add current folder
Přidat do archivu v druhém panelu	Add to the archive in the other panel
Přidat příkaz	Add command
Přidat {} do „{}“?	Add {} to "{}"?
Přidáno do archivu: {}	Added to archive: {}
Přidáno do fronty (ve frontě: {})	Added to the queue (in queue: {})
Přidávám do {}…	Adding to {}…
Připojené disky	Connected disks
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
Rychlý náhled	Quick view
Rychlý přístup	Quick access
Sada záložek „{}“ otevřena.	Tab set "{}" opened.
Sady záložek	Tab sets
Sady záložek…	Tab sets…
Samostatný sloupec s příponou	Separate column with the extension
Schránka neobsahuje soubory.	The clipboard contains no files.
Schránka souborů funguje jen pro lokální soubory.	The file clipboard works only for local files.
Server	Server
Server: {}\n\n{}	Server: {}\n\n{}
Shoduje se	Matches
Skryté soubory	Hidden files
Skrytý	Hidden
Skrýt	Hide
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
Smazat	Delete
Smazat na serveru: {}	Delete on server: {}
Smazat sadu	Delete the set
Soubor je příliš velký pro náhled (nad 100 MB). Zkopírujte ho do lokálního panelu.	The file is too large for a preview (over 100 MB). Copy it to a local panel.
Soubor nelze přečíst.	The file cannot be read.
Soubor rozdělen na {} dílů.	The file was split into {} parts.
Soubor se nepodařilo přečíst.	The file could not be read.
Soubor slepen: {}	File joined: {}
Soubory jsou příliš velké pro porovnání po řádcích (nad 3 MB nebo 8000 řádků) a liší se.	The files are too large for a line comparison (over 3 MB or 8000 lines) and they differ.
Soubory jsou příliš velké pro porovnání po řádcích, ale jsou shodné.	The files are too large for a line comparison, but they are identical.
Soubory jsou shodné.	The files are identical.
Spočítat velikosti složek	Calculate folder sizes
Stahuji aktualizaci…	Downloading the update…
Stahuji náhled {}…	Downloading preview of {}…
Stahuji {}…	Downloading {}…
Stahuji: 	Downloading: 
Stažené	Downloads
Stažení selhalo (kód {}).	The download failed (code {}).
Stažený archiv neobsahuje aplikaci.	The downloaded archive does not contain the application.
Stáhne se {} a aplikace se po výměně restartuje.	{} will be downloaded and the application restarts after the swap.
Stáhnout	Download
Stáhnout změny ze serveru?	Download changes from the server?
Světlý	Light
Symbolický odkaz jde vytvořit jen na lokální soubor nebo složku.	A symbolic link can only be created for a local file or folder.
Symbolický odkaz na „{}“	Symbolic link to "{}"
Systémový Quick Look	System Quick Look
Síť	Network
Síť a disky	Network and drives
Terminál zde	Terminal here
Teď	Now
Tisk…	Print…
Tlačítková lišta uživatelských příkazů	Button bar of user commands
Tmavý	Dark
Trvale smazat {} ze serveru?	Delete {} from the server permanently?
Tuto akci nelze vrátit zpět.	This action cannot be undone.
Třídit podle typu souboru	Sort by file type
U SFTP lze zadat soukromý klíč (RSA/ECDSA); heslo je pak heslem ke klíči.	For SFTP you can enter a private key (RSA/ECDSA); the password is then the key's passphrase.
Ukázat	Show
Ukázat ve Finderu	Show in Finder
Uloženo na server: {}	Saved to server: {}
Uložené servery	Saved servers
Uložit	Save
Uložit .sha256 vedle souborů	Save .sha256 next to the files
Uložit aktuální…	Save current…
Uložit heslo do Klíčenky	Save the password to the Keychain
Uložit sadu záložek	Save tab set
Uživatel	User
Uživatelské příkazy	User commands
Uživatelské příkazy fungují jen v lokálních složkách.	User commands work only in local folders.
Uživatelské příkazy…	User commands…
VELKÁ	UPPER
Velikost	Size
Velikost jednoho dílu v MB (např. 100, 700, 4000).\nDíly se uloží do {}	Size of one part in MB (e.g. 100, 700, 4000).\nThe parts are saved to {}
Velikost písma: {} pt	Font size: {} pt
Velikost složky	Folder size
Velikosti	Sizes
Vlastník	Owner
Vložit soubory ze schránky	Paste files from clipboard
Vpřed v historii	Forward in history
Vráceno: {}	Undone: {}
Vrácení se nepodařilo:\n{}	Undo failed:\n{}
Vrátit poslední operaci	Undo last operation
Vrátit: {}	Undo: {}
Vyberte aspoň jeden soubor.	Select at least one file.
Vyberte lokální soubor s archivem (zip, tar, tar.gz, tar.bz2, 7z…).	Select a local archive file (zip, tar, tar.gz, tar.bz2, 7z…).
Vyberte lokální soubor, který chcete rozdělit.	Select the local file you want to split.
Vyberte soukromý SSH klíč	Select a private SSH key
Vybrané duplicity půjde vrátit příkazem Vrátit poslední operaci.	Selected duplicates can be restored with Undo last operation.
Vybrat přebytečné (ponechat nejstarší)	Select redundant (keep the oldest)
Vybrat…	Choose…
Vyjmout soubory do schránky	Cut files to clipboard
Vyjmuto: {} položek. Vložte klávesou ⌘V.	Cut: {} items. Paste with ⌘V.
Vytvořit	Create
Vytvořit symbolický odkaz	Create symbolic link
Vytvoří se v {}	It will be created in {}
Vzhled	Appearance
Výchozí	Default
Včetně obsahu složek	Including folder contents
Včetně skrytých	Including hidden
Včetně skrytých souborů	Including hidden files
Všechny podsložky najednou (Branch view)	All subfolders at once (Branch view)
Z archivu lze soubory jen kopírovat (F5), ne přesouvat.	Files can only be copied (F5) from an archive, not moved.
Zabalit	Pack
Zabalit do archivu	Pack into archive
Zadejte velikost dílu jako číslo v MB.	Enter the part size as a number in MB.
Zapíše záložky obou panelů (záložky na serveru se vynechají).	Saves the tabs of both panels (tabs on a server are left out).
Zastaveno.	Stopped.
Zastavit	Stop
Zatím není nastavené žádné úložiště. Klikněte na „Nastavit úložiště…“.	No storage is configured yet. Click "Set up storage…".
Zatím není uložená žádná sada. Nastavte záložky v obou panelech a klikněte na „Uložit aktuální…“.	No set is saved yet. Set up the tabs in both panels and click "Save current…".
Zavřít	Close
Zavřít záložku	Close tab
Začít v	Start at
Zdrojová a cílová složka jsou stejné.	The source and destination folders are the same.
Zdrojová a cílová složka se nesmí překrývat.	The source and destination folders must not overlap.
Zdrojový a cílový adresář jsou stejné.	The source and destination directories are the same.
Zjišťuji, jestli je nová verze…	Checking for a new version…
Zkopírováno do schránky.	Copied to clipboard.
Zkopírováno: {} položek. Vložte klávesou ⌘V.	Copied: {} items. Paste with ⌘V.
Změny „{}“ se nepodařilo nahrát:\n{}	Changes to "{}" could not be uploaded:\n{}
Změní se jen štítky, které mají všechny vybrané položky společné; ostatní zůstanou.	Only tags that all selected items have in common are changed; the others stay.
Změněno	Modified
Zobrazit	View
Zpět a vpřed v historii panelu: ⌘[ a ⌘].	Back and forward in the panel history: ⌘[ and ⌘].
Zpět v historii	Back in history
Zrcadlení hotovo.	Mirroring finished.
Zrcadlení mezi dvěma servery není podporováno. Jeden z panelů musí být místní složka.	Mirroring between two servers is not supported. One of the panels must be a local folder.
Zrcadlení zrušeno.	Mirroring cancelled.
Zrcadlit	Mirror
Zrcadlit adresář do druhého panelu	Mirror the directory to the other panel
Zrcadlit „{}“ do „{}“?	Mirror "{}" to "{}"?
Zrušit	Cancel
Zrušit filtr (Esc)	Clear filter (Esc)
Zrušit výběr	Clear selection
Zrušit vše	Cancel all
Zástupné znaky: %f soubor pod kurzorem, %n jeho název, %d složka panelu, %o složka druhého panelu, %F označené soubory (nebo soubor pod kurzorem), %% znak procenta. Cesty se samy uzavřou do uvozovek.	Placeholders: %f file under the cursor, %n its name, %d panel folder, %o other panel folder, %F marked files (or the file under the cursor), %% a percent sign. Paths are quoted automatically.
cesta k rclone (volitelné, jinak se hledá v PATH a v Homebrew)	path to rclone (optional, otherwise PATH and Homebrew are searched)
curl skončil s kódem {}	curl finished with code {}
kopírovat	copy
kopírování	copying
malá	lower
na serveru jen podle názvu	on the server by name only
např. *.jpg nebo část názvu	e.g. *.jpg or part of a name
např. ftp.example.com	e.g. ftp.example.com
použito vícekrát	used more than once
povinné	required
prázdné = anonymní	empty = anonymous
přejmenování „{}“	rename of "{}"
přesun	move
přesunout	move
příkaz (Enter spustí, Esc zavře)	command (Enter runs, Esc closes)
rclone listremotes selhal.	rclone listremotes failed.
rclone skončil s kódem {}	rclone finished with code {}
smazání {}	deleting {}
smb://server/sdílená-složka	smb://server/shared-folder
text v souboru (volitelné)	text in the file (optional)
ve frontě: {}	in queue: {}
volitelné, např. ~/.ssh/id_rsa	optional, e.g. ~/.ssh/id_rsa
vytvoření složky „{}“	creating folder "{}"
{} položek	{} items
{} položek, {}	{} items, {}
{} {} položek	{} {} items
{}: nelze {} adresář do sebe sama	{}: cannot {} a directory into itself
Úložiště	Storage
Úložiště se nastavují programem rclone (příkaz „rclone config“). Tady stačí vybrat jedno z nich a připojit ho do aktivního panelu.	Storage is set up with the rclone program (the "rclone config" command). Here you just pick one and connect it to the active panel.
Červeně označené názvy jsou prázdné, duplicitní nebo už existují.	Names marked in red are empty, duplicate or already exist.
Čítač	Counter
číslic {}	digits {}
Štítky Finderu ({} položek)	Finder tags ({} items)
Štítky Finderu jde nastavit jen u lokálních souborů.	Finder tags can only be set for local files.
Štítky Finderu…	Finder tags…
Žádné duplicity.	No duplicates.
„{}“ již v cíli existuje	"{}" already exists in the destination
„{}“ už existuje. Přepsat?	"{}" already exists. Overwrite?
⌘V zavře	⌘V closes
"""#
}

/// Zkratka pro texty rozhraní: v angličtině vrátí překlad, jinak text beze změny.
func L(_ s: String) -> String { Tr.t(s) }
