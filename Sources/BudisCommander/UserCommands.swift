import SwiftUI

/// Uživatelský příkaz z menu a tlačítkové lišty.
/// Zástupné znaky: %f soubor pod kurzorem, %n jeho název, %d složka panelu, %o složka druhého panelu,
/// %F označené soubory (nebo ten pod kurzorem), %% znak procenta.
struct UserCommand: Codable, Identifiable, Hashable {
    var id = UUID()
    var name: String
    var command: String

    static let defaults: [UserCommand] = [
        UserCommand(name: "Terminál zde", command: "open -a Terminal %d"),
        UserCommand(name: "Ukázat ve Finderu", command: "open -R %f"),
    ]
}

struct UserMenuSheet: View {
    @ObservedObject var settings = Settings.shared
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Uživatelské příkazy").font(.headline)
            Text("Zástupné znaky: %f soubor pod kurzorem, %n jeho název, %d složka panelu, %o složka druhého panelu, %F označené soubory (nebo soubor pod kurzorem), %% znak procenta. Cesty se samy uzavřou do uvozovek.")
                .font(.system(size: 11)).foregroundStyle(.secondary)
            ScrollView {
                VStack(spacing: 6) {
                    ForEach($settings.userCommands) { $cmd in
                        HStack {
                            TextField("Název", text: $cmd.name).frame(width: 150)
                            TextField("Příkaz", text: $cmd.command)
                                .font(.system(size: 12, design: .monospaced))
                            Button {
                                settings.userCommands.removeAll { $0.id == cmd.id }
                            } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.plain)
                        }
                    }
                }
                .textFieldStyle(.roundedBorder)
            }
            .frame(height: 220)
            HStack {
                Button("Přidat příkaz") { settings.userCommands.append(UserCommand(name: "Nový", command: "")) }
                Button("Výchozí") { settings.userCommands = UserCommand.defaults }
                Spacer()
                Button("Hotovo") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 600)
    }
}

struct AttributesSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var setDate = false
    @State private var date = Date()
    @State private var setPerms = false
    @State private var perms = "644"
    @State private var hiddenChoice = 0   // 0 beze změny, 1 skrýt, 2 zobrazit
    @State private var recursive = false

    private var items: [FileItem] { model.attributeItems }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Atributy a časy (\(items.count) položek)").font(.headline)
            Toggle("Nastavit datum a čas změny", isOn: $setDate)
            DatePicker("", selection: $date, displayedComponents: [.date, .hourAndMinute])
                .labelsHidden().disabled(!setDate)
            HStack {
                Button("Teď") { date = Date(); setDate = true }
                Spacer()
            }
            Toggle("Nastavit práva (osmičkově, např. 644 nebo 755)", isOn: $setPerms)
            TextField("644", text: $perms).frame(width: 80).textFieldStyle(.roundedBorder).disabled(!setPerms)
            Picker("Skrytý", selection: $hiddenChoice) {
                Text("Beze změny").tag(0)
                Text("Skrýt").tag(1)
                Text("Zobrazit").tag(2)
            }
            .pickerStyle(.segmented)
            Toggle("Včetně obsahu složek", isOn: $recursive)
            HStack {
                Spacer()
                Button("Zrušit") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Použít") { apply() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 420)
        .onAppear {
            if let first = items.first {
                date = first.modified ?? Date()
                if let n = (try? FileManager.default.attributesOfItem(atPath: first.url.path))?[.posixPermissions] as? NSNumber {
                    perms = String(n.intValue, radix: 8)
                }
            }
        }
    }

    private func apply() {
        var mode: Int?
        if setPerms {
            guard let m = Int(perms, radix: 8), (0...0o7777).contains(m) else {
                Dialogs.error("Práva zadejte osmičkově, např. 644.")
                return
            }
            mode = m
        }
        let change = LocalFS.AttributeChange(date: setDate ? date : nil, permissions: mode,
                                             hidden: hiddenChoice == 0 ? nil : hiddenChoice == 1,
                                             recursive: recursive)
        let urls = items.map(\.url)
        dismiss()
        Task { await model.applyAttributes(change, to: urls) }
    }
}
