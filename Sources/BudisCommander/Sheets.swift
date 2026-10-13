import SwiftUI
import AppKit

struct SavedServer: Codable, Identifiable, Hashable {
    var proto: RemoteProtocol
    var host: String
    var port: Int
    var user: String
    var insecure: Bool
    var keyPath: String?
    var passwordSaved: Bool?
    /// Vlastní název v seznamu (prázdný = jen adresa).
    var name: String? = nil
    /// Složka ve správci připojení (prázdná = bez skupiny).
    var group: String? = nil

    var id: String { title }
    var isManaged: Bool {
        !(name ?? "").trimmingCharacters(in: .whitespaces).isEmpty || !(group ?? "").trimmingCharacters(in: .whitespaces).isEmpty
    }
    var displayName: String {
        let n = (name ?? "").trimmingCharacters(in: .whitespaces)
        return n.isEmpty ? title : "\(n)   (\(title))"
    }
    static let maxUnmanaged = 20
    var title: String { "\(proto.rawValue)  \(user.isEmpty ? "" : user + "@")\(host):\(port)" }

    private static let key = "savedServers"

    static func loadAll() -> [SavedServer] {
        guard let data = UserDefaults.standard.data(forKey: key),
              let list = try? JSONDecoder().decode([SavedServer].self, from: data) else { return [] }
        return list
    }

    /// Servery bez skupiny jako první, pak skupiny podle abecedy; uvnitř skupiny podle názvu.
    static func tree(_ servers: [SavedServer]) -> [(group: String, servers: [SavedServer])] {
        var byGroup: [String: [SavedServer]] = [:]
        var order: [String] = []
        for s in servers {
            let g = (s.group ?? "").trimmingCharacters(in: .whitespaces)
            let key = order.first { $0.caseInsensitiveCompare(g) == .orderedSame } ?? g
            if byGroup[key] == nil { byGroup[key] = []; order.append(key) }
            byGroup[key]!.append(s)
        }
        let sorted = order.sorted { a, b in
            if a.isEmpty != b.isEmpty { return a.isEmpty }
            return a.localizedCaseInsensitiveCompare(b) == .orderedAscending
        }
        return sorted.map { g in
            (g, byGroup[g]!.sorted { $0.displayName.localizedCaseInsensitiveCompare($1.displayName) == .orderedAscending })
        }
    }

    /// Zapamatuje server: stejný server se nahradí na svém místě a zůstane mu název i skupina, nový jde na začátek.
    /// Nespravované položky se ořežou na `maxUnmanaged`.
    static func remember(_ server: SavedServer, in list: [SavedServer]) -> [SavedServer] {
        var list = list
        var server = server
        if let i = list.firstIndex(where: { $0.id == server.id }) {
            if (server.name ?? "").isEmpty { server.name = list[i].name }
            if (server.group ?? "").isEmpty { server.group = list[i].group }
            list[i] = server
        } else {
            list.insert(server, at: 0)
        }
        var seen = 0
        return list.filter { s in
            if s.isManaged { return true }
            seen += 1
            return seen <= maxUnmanaged
        }
    }

    static func saveAll(_ list: [SavedServer]) {
        if let data = try? JSONEncoder().encode(list) { UserDefaults.standard.set(data, forKey: key) }
    }

    func save() {
        Self.saveAll(Self.remember(self, in: Self.loadAll()))
    }
}

struct ConnectSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss

    @State private var proto = RemoteProtocol.sftp
    @State private var host = ""
    @State private var port = ""
    @State private var user = ""
    @State private var password = ""
    @State private var insecure = false
    @State private var keyPath = ""
    @State private var remember = false
    @State private var busy = false
    @State private var name = ""
    @State private var group = ""
    @State private var saved = SavedServer.loadAll()

    private var portValue: Int { Int(port) ?? proto.defaultPort }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(L("Připojit k serveru")).font(.headline)
                Spacer()
                if !saved.isEmpty {
                    Menu(L("Uložené servery")) {
                        ForEach(saved) { s in
                            Button(s.displayName) { fill(s) }
                        }
                    }
                    .fixedSize()
                }
            }

            Picker(L("Protokol"), selection: $proto) {
                ForEach(RemoteProtocol.allCases.filter { $0 != .rclone && $0 != .adb }) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 8) {
                GridRow {
                    Text(L("Server"))
                    HStack {
                        TextField(L("např. ftp.example.com"), text: $host)
                        TextField("\(proto.defaultPort)", text: $port).frame(width: 60)
                    }
                }
                GridRow {
                    Text(L("Uživatel"))
                    TextField(proto == .sftp ? L("povinné") : L("prázdné = anonymní"), text: $user)
                }
                if proto == .sftp {
                    GridRow {
                        Text(L("Klíč"))
                        HStack {
                            TextField(L("volitelné, např. ~/.ssh/id_rsa"), text: $keyPath)
                            Button(L("Vybrat…")) { chooseKey() }
                        }
                    }
                }
                GridRow {
                    Text(proto == .sftp && !keyPath.isEmpty ? L("Heslo klíče") : L("Heslo"))
                    SecureField("", text: $password)
                }
                GridRow { Text(L("Název")); TextField(L("volitelné, např. Web firmy"), text: $name) }
                GridRow { Text(L("Skupina")); TextField(L("volitelné, např. Práce"), text: $group) }
            }
            .textFieldStyle(.roundedBorder)

            Toggle(L("Důvěřovat serveru bez ověření klíče/certifikátu"), isOn: $insecure)
                .font(.system(size: 12))
            Toggle(L("Uložit heslo do Klíčenky"), isOn: $remember)
                .font(.system(size: 12))
            Text(L("U SFTP lze zadat soukromý klíč (RSA/ECDSA); heslo je pak heslem ke klíči."))
                .font(.system(size: 11)).foregroundStyle(.secondary)

            HStack {
                if busy { ProgressView().controlSize(.small) }
                Spacer()
                Button(L("Zrušit")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("Jen uložit")) { saveOnly() }
                    .disabled(host.trimmingCharacters(in: .whitespaces).isEmpty || busy)
                Button(L("Připojit")) { connect() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(host.trimmingCharacters(in: .whitespaces).isEmpty || busy)
            }
        }
        .padding(16)
        .frame(width: 440)
        .onAppear {
            if let s = model.prefillServer { fill(s); model.prefillServer = nil }
        }
    }

    private func makeServer() -> SavedServer {
        SavedServer(proto: proto, host: host.trimmingCharacters(in: .whitespaces), port: portValue, user: user, insecure: insecure,
                    keyPath: keyPath.isEmpty ? nil : keyPath, passwordSaved: remember,
                    name: name.trimmingCharacters(in: .whitespaces).isEmpty ? nil : name.trimmingCharacters(in: .whitespaces),
                    group: group.trimmingCharacters(in: .whitespaces).isEmpty ? nil : group.trimmingCharacters(in: .whitespaces))
    }

    /// Uloží server do seznamu bez připojení.
    private func saveOnly() {
        let server = makeServer()
        server.save()
        if remember { Keychain.set(password, account: server.id) } else { Keychain.delete(account: server.id) }
        model.serversRevision += 1
        dismiss()
    }

    private func fill(_ s: SavedServer) {
        proto = s.proto; host = s.host; port = String(s.port); user = s.user
        name = s.name ?? ""; group = s.group ?? ""
        insecure = s.insecure; keyPath = s.keyPath ?? ""
        remember = s.passwordSaved ?? false
        password = remember ? (Keychain.get(account: s.id) ?? "") : ""
    }

    private func chooseKey() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.showsHiddenFiles = true
        panel.directoryURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".ssh")
        panel.message = L("Vyberte soukromý SSH klíč")
        if panel.runModal() == .OK, let url = panel.url { keyPath = url.path }
    }

    private func connect() {
        let h = host.trimmingCharacters(in: .whitespaces)
        let conn = RemoteConnection(proto: proto, host: h, port: portValue, user: user,
                                    password: password, insecure: insecure,
                                    keyPath: proto == .sftp ? keyPath.trimmingCharacters(in: .whitespaces) : "")
        busy = true
        Task {
            let ok = await model.connect(conn)
            busy = false
            if ok {
                let server = makeServer()
                server.save()
                model.serversRevision += 1
                if remember { Keychain.set(password, account: server.id) } else { Keychain.delete(account: server.id) }
                dismiss()
            }
        }
    }
}

struct NetworkSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @StateObject private var lan = LANBrowser()
    @State private var volumes = AppModel.mountedVolumes()
    @State private var address = "smb://"

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Síť a disky")).font(.headline)

            Text(L("Připojené disky")).font(.subheadline).foregroundStyle(.secondary)
            List(volumes, id: \.self) { v in
                Button {
                    model.active.navigate(to: URL(fileURLWithPath: "/Volumes/\(v)"))
                    dismiss()
                } label: {
                    Label(v, systemImage: "externaldrive").frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
            }
            .frame(height: 110)

            Text(L("Počítače v síti (SMB)")).font(.subheadline).foregroundStyle(.secondary)
            List(lan.hosts) { h in
                Button {
                    mount("smb://\(h.address)")
                } label: {
                    Label(h.name, systemImage: "desktopcomputer").frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
            }
            .frame(height: 130)
            .overlay {
                if lan.hosts.isEmpty { Text(L("Hledám…")).foregroundStyle(.secondary) }
            }

            Text(L("Adresa sdílení")).font(.subheadline).foregroundStyle(.secondary)
            HStack {
                TextField(L("smb://server/sdílená-složka"), text: $address)
                    .textFieldStyle(.roundedBorder)
                Button(L("Připojit")) { mount(address) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(address.count < 7)
            }
            Text(L("Podporované: smb://, afp://, nfs://. O přihlášení se postará systém."))
                .font(.system(size: 11)).foregroundStyle(.secondary)

            HStack {
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 460)
        .onAppear { lan.start() }
        .onDisappear { lan.stop() }
    }

    private func mount(_ address: String) {
        dismiss()
        Task { await model.mount(address) }
    }
}

/// Správce připojení: uložené servery ve skupinách, připojení dvojklikem nebo tlačítkem.
struct ConnectionManagerSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var servers = SavedServer.loadAll()
    @State private var selection: String?
    @State private var status = ""

    private var selected: SavedServer? { servers.first { $0.id == selection } }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Správce připojení")).font(.headline)
            List(selection: $selection) {
                ForEach(SavedServer.tree(servers), id: \.group) { section in
                    Section(section.group.isEmpty ? L("Bez skupiny") : section.group) {
                        ForEach(section.servers) { s in
                            Text(s.displayName).lineLimit(1).tag(s.id)
                                .onTapGesture(count: 2) { selection = s.id; connect() }
                        }
                    }
                }
            }
            .frame(height: 300)
            .overlay { if servers.isEmpty { Text(L("Zatím žádný uložený server. Přidejte ho tlačítkem Nový.")).foregroundStyle(.secondary) } }
            Text(status).font(.system(size: 11)).foregroundStyle(.secondary)
            HStack {
                Button(L("Připojit")) { connect() }.disabled(selected == nil).keyboardShortcut(.defaultAction)
                Button(L("Nový…")) { open(nil) }
                Button(L("Upravit…")) { open(selected) }.disabled(selected == nil)
                Button(L("Smazat")) { delete() }.disabled(selected == nil)
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
            Text(L("Název a skupinu nastavíte v okně Upravit; servery ve skupinách se samy nemažou. Rychlé připojení je i v menu Nástroje."))
                .font(.system(size: 11)).foregroundStyle(.secondary)
        }
        .padding(16)
        .frame(width: 600)
    }

    private func open(_ server: SavedServer?) {
        model.prefillServer = server
        model.sheet = .server
    }

    private func connect() {
        guard let s = selected else { return }
        model.quickConnect(s)
    }

    private func delete() {
        guard let s = selected, Dialogs.confirm(L("Smazat uložený server „\(s.displayName)“?"), ok: L("Smazat")) else { return }
        Keychain.delete(account: s.id)
        servers.removeAll { $0.id == s.id }
        SavedServer.saveAll(servers)
        model.serversRevision += 1
    }
}
