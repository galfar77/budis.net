import SwiftUI

enum ShortcutAction: String, CaseIterable, Identifiable {
    case newTab, closeTab, refresh, markAll, hidden, mirror, connect, network
    case compare, batchRename, pack, unpack, search, settings
    case sync, commandLine, favorites, back, forward
    case diff, compareContent, checksum, branch, dirSizes, attributes, quickView
    case split, combine, symlink, userMenu, resumeTransfer, thumbnails
    case undo, copyFiles, cutFiles, pasteFiles, copyPath, copyName, copyDirPath
    case quickLook, duplicates, addToArchive, tags
    case tabSets, cloud, checkUpdate

    var id: String { rawValue }

    var title: String {
        switch self {
        case .newTab: return "Nová záložka"
        case .closeTab: return "Zavřít záložku"
        case .refresh: return "Obnovit"
        case .markAll: return "Označit vše"
        case .hidden: return "Skryté soubory"
        case .mirror: return "Zrcadlit adresář do druhého panelu"
        case .connect: return "Připojit k serveru"
        case .network: return "Síť a disky"
        case .compare: return "Porovnat adresáře"
        case .batchRename: return "Hromadné přejmenování"
        case .pack: return "Zabalit do archivu"
        case .unpack: return "Rozbalit archiv"
        case .search: return "Hledat soubory"
        case .settings: return "Nastavení"
        case .sync: return "Zrcadlit adresář do druhého panelu"
        case .commandLine: return "Příkazový řádek"
        case .favorites: return "Oblíbené a poslední složky"
        case .back: return "Zpět v historii"
        case .forward: return "Vpřed v historii"
        case .diff: return "Porovnat dva soubory (obsah)"
        case .compareContent: return "Porovnat adresáře podle obsahu"
        case .checksum: return "Kontrolní součty (MD5, SHA)"
        case .branch: return "Všechny podsložky najednou (Branch view)"
        case .dirSizes: return "Spočítat velikosti složek"
        case .attributes: return "Atributy a časy"
        case .quickView: return "Panel rychlého náhledu"
        case .split: return "Rozdělit soubor na díly"
        case .combine: return "Slepit díly souboru"
        case .symlink: return "Vytvořit symbolický odkaz"
        case .userMenu: return "Uživatelské příkazy…"
        case .resumeTransfer: return "Pokračovat v přerušeném přenosu"
        case .thumbnails: return "Miniatury souborů"
        case .undo: return "Vrátit poslední operaci"
        case .copyFiles: return "Kopírovat soubory do schránky"
        case .cutFiles: return "Vyjmout soubory do schránky"
        case .pasteFiles: return "Vložit soubory ze schránky"
        case .copyPath: return "Kopírovat cestu"
        case .copyName: return "Kopírovat název"
        case .copyDirPath: return "Kopírovat cestu složky"
        case .quickLook: return "Systémový Quick Look"
        case .duplicates: return "Hledat duplicity"
        case .addToArchive: return "Přidat do archivu v druhém panelu"
        case .tags: return "Štítky Finderu…"
        case .tabSets: return "Sady záložek…"
        case .cloud: return "Cloudová úložiště (rclone)…"
        case .checkUpdate: return "Aktualizovat aplikaci…"
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
        case .tabSets, .cloud, .checkUpdate: return ""
        }
    }
}

@MainActor
final class Settings: ObservableObject {
    static let shared = Settings()

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
    /// Při startu se zeptat GitHubu na novou verzi (nejvýš jednou za 20 hodin).
    @Published var autoCheckUpdates: Bool {
        didSet { UserDefaults.standard.set(autoCheckUpdates, forKey: "autoCheckUpdates") }
    }
    var lastUpdateCheck: Double {
        get { UserDefaults.standard.double(forKey: "lastUpdateCheck") }
        set { UserDefaults.standard.set(newValue, forKey: "lastUpdateCheck") }
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
        showButtonBar = d.object(forKey: "showButtonBar") as? Bool ?? true
        autoDirSizes = d.bool(forKey: "autoDirSizes")
        showPerms = d.bool(forKey: "showPerms")
        showOwner = d.bool(forKey: "showOwner")
        showMedia = d.bool(forKey: "showMedia")
        showTags = d.object(forKey: "showTags") as? Bool ?? true
        rclonePath = d.string(forKey: "rclonePath") ?? ""
        autoCheckUpdates = d.object(forKey: "autoCheckUpdates") as? Bool ?? true
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
            Text("Nastavení").font(.headline)

            Picker("Vzhled", selection: $settings.theme) {
                Text("Podle systému").tag("system")
                Text("Světlý").tag("light")
                Text("Tmavý").tag("dark")
            }
            .pickerStyle(.segmented)

            Stepper("Velikost písma: \(Int(settings.fontSize)) pt", value: $settings.fontSize, in: 10...20, step: 1)
            Toggle("Samostatný sloupec s příponou", isOn: $settings.showExt)
            Toggle("Sloupec Práva", isOn: $settings.showPerms)
            Toggle("Sloupec Vlastník", isOn: $settings.showOwner)
            Toggle("Sloupec Rozměry obrázku / délka zvuku a videa", isOn: $settings.showMedia)
            Toggle("Barevné štítky Finderu u názvů", isOn: $settings.showTags)
            Toggle("Miniatury souborů místo ikon", isOn: $settings.showThumbs)
            Toggle("Automaticky počítat velikosti složek (na pozadí, jen lokální složky)", isOn: $settings.autoDirSizes)
            Toggle("Tlačítková lišta uživatelských příkazů", isOn: $settings.showButtonBar)
            Toggle("Při startu zkontrolovat, jestli je dostupná nová verze", isOn: $settings.autoCheckUpdates)

            Divider()
            Text("Klávesové zkratky (⌘ + znak)").font(.subheadline).foregroundStyle(.secondary)
            ScrollView {
                VStack(spacing: 6) {
                    ForEach(ShortcutAction.allCases) { action in
                        HStack {
                            Text(action.title)
                            Spacer()
                            if settings.isDuplicate(action) {
                                Text("použito vícekrát").font(.system(size: 11)).foregroundStyle(.red)
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
            Text("Klávesy F2–F8, Tab, šipky a ⌘1–9 (záložky) jsou pevné. Změna zkratek se projeví hned.")
                .font(.system(size: 11)).foregroundStyle(.secondary)

            HStack {
                Button("Obnovit výchozí zkratky") { settings.resetShortcuts() }
                Spacer()
                Button("Hotovo") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 440)
    }
}
