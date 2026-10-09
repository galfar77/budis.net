import SwiftUI

enum ShortcutAction: String, CaseIterable, Identifiable {
    case newTab, closeTab, refresh, markAll, hidden, mirror, connect, network
    case compare, batchRename, pack, unpack, search, settings

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
        case .pack: return "z"
        case .unpack: return "e"
        case .search: return "f"
        case .settings: return ","
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
    @Published private(set) var shortcuts: [String: String] {
        didSet { UserDefaults.standard.set(shortcuts, forKey: "shortcuts") }
    }

    private init() {
        let d = UserDefaults.standard
        let size = d.double(forKey: "fontSize")
        fontSize = size == 0 ? 12 : size
        theme = d.string(forKey: "theme") ?? "system"
        shortcuts = (d.dictionary(forKey: "shortcuts") as? [String: String]) ?? [:]
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
            .frame(height: 280)
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
