import SwiftUI

enum ShortcutAction: String, CaseIterable, Identifiable {
    case newTab, closeTab, refresh, markAll, hidden, mirror, connect, network
    case compare, batchRename, pack, unpack, search, settings
    case sync, commandLine, favorites, back, forward
    case diff, compareContent, checksum, branch, dirSizes, attributes, quickView
    case split, combine, symlink, userMenu, resumeTransfer, thumbnails
    case undo, copyFiles, cutFiles, pasteFiles, copyPath, copyName, copyDirPath
    case quickLook, duplicates, addToArchive, tags
    case tabSets, cloud, checkUpdate, sortByType, processes, transfers, syncTasks, phone, terminal
    case markAdvanced, markSameExt, invertMarks, rollbackVersion, history, trashRestore, shareLink, connections

    var id: String { rawValue }

    var title: String {
        switch self {
        case .newTab: return L("Nová záložka")
        case .closeTab: return L("Zavřít záložku")
        case .refresh: return L("Obnovit")
        case .markAll: return L("Označit vše")
        case .hidden: return L("Skryté soubory")
        case .mirror: return L("Zrcadlit adresář do druhého panelu")
        case .connect: return L("Připojit k serveru")
        case .network: return L("Síť a disky")
        case .compare: return L("Porovnat adresáře")
        case .batchRename: return L("Hromadné přejmenování")
        case .pack: return L("Zabalit do archivu")
        case .unpack: return L("Rozbalit archiv")
        case .search: return L("Hledat soubory")
        case .settings: return L("Nastavení")
        case .sync: return L("Zrcadlit adresář do druhého panelu")
        case .commandLine: return L("Příkazový řádek")
        case .favorites: return L("Oblíbené a poslední složky")
        case .back: return L("Zpět v historii")
        case .forward: return L("Vpřed v historii")
        case .diff: return L("Porovnat dva soubory (obsah)")
        case .compareContent: return L("Porovnat adresáře podle obsahu")
        case .checksum: return L("Kontrolní součty (MD5, SHA)")
        case .branch: return L("Všechny podsložky najednou (Branch view)")
        case .dirSizes: return L("Spočítat velikosti složek")
        case .attributes: return L("Atributy a časy")
        case .quickView: return L("Panel rychlého náhledu")
        case .split: return L("Rozdělit soubor na díly")
        case .combine: return L("Slepit díly souboru")
        case .symlink: return L("Vytvořit symbolický odkaz")
        case .userMenu: return L("Uživatelské příkazy…")
        case .resumeTransfer: return L("Pokračovat v přerušeném přenosu")
        case .thumbnails: return L("Miniatury souborů")
        case .undo: return L("Vrátit poslední operaci")
        case .copyFiles: return L("Kopírovat soubory do schránky")
        case .cutFiles: return L("Vyjmout soubory do schránky")
        case .pasteFiles: return L("Vložit soubory ze schránky")
        case .copyPath: return L("Kopírovat cestu")
        case .copyName: return L("Kopírovat název")
        case .copyDirPath: return L("Kopírovat cestu složky")
        case .quickLook: return L("Systémový Quick Look")
        case .duplicates: return L("Hledat duplicity")
        case .addToArchive: return L("Přidat do archivu v druhém panelu")
        case .tags: return L("Štítky Finderu…")
        case .tabSets: return L("Sady záložek…")
        case .cloud: return L("Cloudová úložiště (rclone)…")
        case .checkUpdate: return L("Aktualizovat aplikaci…")
        case .sortByType: return L("Třídit podle typu souboru")
        case .processes: return L("Správce procesů…")
        case .transfers: return L("Správce přenosů…")
        case .syncTasks: return L("Synchronizace na pozadí…")
        case .phone: return L("Telefon (Android)…")
        case .terminal: return L("Terminál")
        case .markAdvanced: return L("Označit podle podmínek…")
        case .markSameExt: return L("Označit stejnou příponu")
        case .invertMarks: return L("Převrátit označení")
        case .rollbackVersion: return L("Vrátit předchozí verzi…")
        case .shareLink: return L("Sdílet odkazem (cloud)")
        case .connections: return L("Správce připojení…")
        case .history: return L("Historie operací…")
        case .trashRestore: return L("Obnovit z Koše…")
        }
    }

    var defaultChar: String {
        switch self {
        case .newTab: return "t"
        case .closeTab: return "w"
        case .refresh: return "r"
        case .markAll: return "a"
        case .hidden: return "."
        case .mirror: return "u"
        case .connect: return "k"
        case .network: return "l"
        case .compare: return "d"
        case .batchRename: return "m"
        case .pack: return "p"
        case .unpack: return "e"
        case .search: return "f"
        case .settings: return ","
        case .sync: return "y"
        case .commandLine: return "j"
        case .favorites: return "b"
        case .back: return "["
        case .forward: return "]"
        case .diff: return "i"
        case .checksum: return "h"
        case .branch: return "g"
        case .dirSizes: return "s"
        case .attributes: return "o"
        case .quickView: return "n"
        case .undo: return "z"
        case .compareContent, .split, .combine, .symlink, .userMenu, .resumeTransfer, .thumbnails: return ""
        case .copyFiles, .cutFiles, .pasteFiles, .copyPath, .copyName, .copyDirPath: return ""
        case .quickLook, .duplicates, .addToArchive, .tags: return ""
        case .tabSets, .cloud, .checkUpdate, .sortByType, .processes, .transfers, .syncTasks, .phone: return ""
        case .terminal: return "'"
        case .markAdvanced, .markSameExt, .invertMarks, .rollbackVersion, .history, .trashRestore, .shareLink, .connections: return ""
        }
    }
}

@MainActor
final class Settings: ObservableObject {
    static let shared = Settings()

    /// Systémová menu (Soubor, Úpravy, Zobrazení, Okno, Nápověda) se překládají podle jazyka aplikace; projeví se při příštím spuštění.
    nonisolated static func applySystemLanguage(_ language: String) {
        UserDefaults.standard.set([language == "en" ? "en" : "cs"], forKey: "AppleLanguages")
    }

    @Published var fontSize: Double {
        didSet { UserDefaults.standard.set(fontSize, forKey: "fontSize") }
    }
    /// "system", "light" nebo "dark"
    @Published var theme: String {
        didSet { UserDefaults.standard.set(theme, forKey: "theme") }
    }
    @Published var showExt: Bool {
        didSet { UserDefaults.standard.set(showExt, forKey: "showExt") }
    }
    /// Výchozí rozložení sloupců pro složky bez vlastního (nil = základní).
    @Published var defaultColumns: ColumnLayout? {
        didSet {
            if let data = defaultColumns.flatMap({ try? JSONEncoder().encode($0) }) {
                UserDefaults.standard.set(data, forKey: "defaultColumns")
            } else {
                UserDefaults.standard.removeObject(forKey: "defaultColumns")
            }
        }
    }
    @Published var showThumbs: Bool {
        didSet { UserDefaults.standard.set(showThumbs, forKey: "showThumbs") }
    }
    @Published var autoDirSizes: Bool {
        didSet { UserDefaults.standard.set(autoDirSizes, forKey: "autoDirSizes") }
    }
    @Published var showPerms: Bool {
        didSet { UserDefaults.standard.set(showPerms, forKey: "showPerms") }
    }
    @Published var showOwner: Bool {
        didSet { UserDefaults.standard.set(showOwner, forKey: "showOwner") }
    }
    @Published var showMedia: Bool {
        didSet { UserDefaults.standard.set(showMedia, forKey: "showMedia") }
    }
    @Published var showTags: Bool {
        didSet { UserDefaults.standard.set(showTags, forKey: "showTags") }
    }
    @Published var showButtonBar: Bool {
        didSet { UserDefaults.standard.set(showButtonBar, forKey: "showButtonBar") }
    }
    /// Průhledné okno ve stylu skla.
    @Published var glass: Bool {
        didSet { UserDefaults.standard.set(glass, forKey: "glass") }
    }
    /// Barevné téma (id z ColorTheme.all).
    @Published var colorTheme: String {
        didSet { UserDefaults.standard.set(colorTheme, forKey: "colorTheme") }
    }
    var palette: ColorTheme { ColorTheme.get(colorTheme) }
    /// Jazyk rozhraní: "cs" nebo "en" (změna se projeví po restartu aplikace).
    @Published var language: String {
        didSet {
            UserDefaults.standard.set(language, forKey: "language")
            Settings.applySystemLanguage(language)
        }
    }
    /// Při startu se zeptat GitHubu na novou verzi (nejvýš jednou za 20 hodin).
    /// Novou verzi stáhnout sama a nainstalovat při ukončení aplikace.
    @Published var autoInstallUpdates: Bool {
        didSet { UserDefaults.standard.set(autoInstallUpdates, forKey: "autoInstallUpdates") }
    }
    @Published var autoCheckUpdates: Bool {
        didSet { UserDefaults.standard.set(autoCheckUpdates, forKey: "autoCheckUpdates") }
    }
    var lastUpdateCheck: Double {
        get { UserDefaults.standard.double(forKey: "lastUpdateCheck") }
        set { UserDefaults.standard.set(newValue, forKey: "lastUpdateCheck") }
    }
    /// Volitelná cesta k programu adb (Android).
    @Published var adbPath: String {
        didSet { UserDefaults.standard.set(adbPath, forKey: "adbPath") }
    }
    /// Volitelná cesta k programu rclone.
    @Published var rclonePath: String {
        didSet { UserDefaults.standard.set(rclonePath, forKey: "rclonePath") }
    }
    @Published var userCommands: [UserCommand] {
        didSet {
            if let data = try? JSONEncoder().encode(userCommands) {
                UserDefaults.standard.set(data, forKey: "userCommands")
            }
        }
    }
    @Published private(set) var shortcuts: [String: String] {
        didSet { UserDefaults.standard.set(shortcuts, forKey: "shortcuts") }
    }

    private init() {
        let d = UserDefaults.standard
        let size = d.double(forKey: "fontSize")
        fontSize = size == 0 ? 12 : size
        theme = d.string(forKey: "theme") ?? "system"
        shortcuts = (d.dictionary(forKey: "shortcuts") as? [String: String]) ?? [:]
        showExt = d.bool(forKey: "showExt")
        showThumbs = d.bool(forKey: "showThumbs")
        defaultColumns = d.data(forKey: "defaultColumns").flatMap { try? JSONDecoder().decode(ColumnLayout.self, from: $0) }
        showButtonBar = d.object(forKey: "showButtonBar") as? Bool ?? true
        autoDirSizes = d.bool(forKey: "autoDirSizes")
        showPerms = d.bool(forKey: "showPerms")
        showOwner = d.bool(forKey: "showOwner")
        showMedia = d.bool(forKey: "showMedia")
        showTags = d.object(forKey: "showTags") as? Bool ?? true
        rclonePath = d.string(forKey: "rclonePath") ?? ""
        adbPath = d.string(forKey: "adbPath") ?? ""
        language = d.string(forKey: "language") ?? "cs"
        glass = d.object(forKey: "glass") as? Bool ?? true
        colorTheme = d.string(forKey: "colorTheme") ?? "default"
        autoCheckUpdates = d.object(forKey: "autoCheckUpdates") as? Bool ?? true
        autoInstallUpdates = d.object(forKey: "autoInstallUpdates") as? Bool ?? false
        if let data = d.data(forKey: "userCommands"), let list = try? JSONDecoder().decode([UserCommand].self, from: data) {
            // Starý výchozí příkaz „du -sh“ nahradila vestavěná funkce (⌘S, sloupec Velikost).
            userCommands = list.filter { !($0.name == "Velikost složky" && $0.command == "du -sh %F") }
        } else {
            userCommands = UserCommand.defaults
        }
    }

    var colorScheme: ColorScheme? {
        switch theme {
        case "light": return .light
        case "dark": return .dark
        default: return nil
        }
    }

    func char(for action: ShortcutAction) -> String {
        shortcuts[action.rawValue] ?? action.defaultChar
    }

    func setChar(_ value: String, for action: ShortcutAction) {
        guard let last = value.lowercased().last else { return }
        shortcuts[action.rawValue] = String(last)
    }

    func action(for char: String) -> ShortcutAction? {
        ShortcutAction.allCases.first { self.char(for: $0) == char }
    }

    func isDuplicate(_ action: ShortcutAction) -> Bool {
        let c = char(for: action)
        if c.isEmpty { return false }
        return ShortcutAction.allCases.filter { char(for: $0) == c }.count > 1
    }

    func resetShortcuts() { shortcuts = [:] }
}

struct SettingsSheet: View {
    @ObservedObject var settings = Settings.shared
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("Nastavení")).font(.headline)

            Picker(L("Vzhled"), selection: $settings.theme) {
                Text(L("Podle systému")).tag("system")
                Text(L("Světlý")).tag("light")
                Text(L("Tmavý")).tag("dark")
            }
            .pickerStyle(.segmented)

            Picker(L("Barevné téma"), selection: $settings.colorTheme) {
                ForEach(ColorTheme.all) { Text(L($0.title)).tag($0.id) }
            }

            Picker(L("Jazyk (po restartu)"), selection: $settings.language) {
                Text("Čeština").tag("cs")
                Text("English").tag("en")
            }
            .pickerStyle(.segmented)

            Stepper(L("Velikost písma: \(Int(settings.fontSize)) pt"), value: $settings.fontSize, in: 10...20, step: 1)
            Toggle(L("Samostatný sloupec s příponou"), isOn: $settings.showExt)
            Toggle(L("Sloupec Práva"), isOn: $settings.showPerms)
            Toggle(L("Sloupec Vlastník"), isOn: $settings.showOwner)
            Toggle(L("Sloupec Rozměry obrázku / délka zvuku a videa"), isOn: $settings.showMedia)
            Toggle(L("Barevné štítky Finderu u názvů"), isOn: $settings.showTags)
            Toggle(L("Miniatury souborů místo ikon"), isOn: $settings.showThumbs)
            Toggle(L("Automaticky počítat velikosti složek (na pozadí, jen lokální složky)"), isOn: $settings.autoDirSizes)
            Toggle(L("Průhledné okno ve stylu skla"), isOn: $settings.glass)
            Toggle(L("Tlačítková lišta uživatelských příkazů"), isOn: $settings.showButtonBar)
            Toggle(L("Při startu zkontrolovat, jestli je dostupná nová verze"), isOn: $settings.autoCheckUpdates)
            Toggle(L("Novou verzi stáhnout a nainstalovat automaticky (při ukončení aplikace)"), isOn: $settings.autoInstallUpdates)

            Divider()
            Text(L("Klávesové zkratky (⌘ + znak)")).font(.subheadline).foregroundStyle(.secondary)
            ScrollView {
                VStack(spacing: 6) {
                    ForEach(ShortcutAction.allCases) { action in
                        HStack {
                            Text(action.title)
                            Spacer()
                            if settings.isDuplicate(action) {
                                Text(L("použito vícekrát")).font(.system(size: 11)).foregroundStyle(.red)
                            }
                            Text("⌘")
                            TextField("", text: Binding(
                                get: { settings.char(for: action) },
                                set: { settings.setChar($0, for: action) }))
                                .frame(width: 36)
                                .multilineTextAlignment(.center)
                                .textFieldStyle(.roundedBorder)
                        }
                    }
                }
                .padding(.trailing, 8)
            }
            .frame(height: 200)
            Text(L("Klávesy F2–F8, Tab, šipky a ⌘1–9 (záložky) jsou pevné. Změna zkratek se projeví hned."))
                .font(.system(size: 11)).foregroundStyle(.secondary)

            HStack {
                Button(L("Obnovit výchozí zkratky")) { settings.resetShortcuts() }
                Spacer()
                Button(L("Hotovo")) { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 440)
    }
}
