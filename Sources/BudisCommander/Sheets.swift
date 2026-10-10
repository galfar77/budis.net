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

    var id: String { title }
    var title: String { "\(proto.rawValue)  \(user.isEmpty ? "" : user + "@")\(host):\(port)" }

    private static let key = "savedServers"

    static func loadAll() -> [SavedServer] {
        guard let data = UserDefaults.standard.data(forKey: key),
              let list = try? JSONDecoder().decode([SavedServer].self, from: data) else { return [] }
        return list
    }

    func save() {
        var list = Self.loadAll().filter { $0.id != id }
        list.insert(self, at: 0)
        if let data = try? JSONEncoder().encode(Array(list.prefix(20))) {
            UserDefaults.standard.set(data, forKey: Self.key)
        }
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
    @State private var saved = SavedServer.loadAll()

    private var portValue: Int { Int(port) ?? proto.defaultPort }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Připojit k serveru").font(.headline)
                Spacer()
                if !saved.isEmpty {
                    Menu("Uložené servery") {
                        ForEach(saved) { s in
                            Button(s.title) { fill(s) }
                        }
                    }
                    .fixedSize()
                }
            }

            Picker("Protokol", selection: $proto) {
                ForEach(RemoteProtocol.allCases.filter { $0 != .rclone }) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 8) {
                GridRow {
                    Text("Server")
                    HStack {
                        TextField("např. ftp.example.com", text: $host)
                        TextField("\(proto.defaultPort)", text: $port).frame(width: 60)
                    }
                }
                GridRow {
                    Text("Uživatel")
                    TextField(proto == .sftp ? "povinné" : "prázdné = anonymní", text: $user)
                }
                if proto == .sftp {
                    GridRow {
                        Text("Klíč")
                        HStack {
                            TextField("volitelné, např. ~/.ssh/id_rsa", text: $keyPath)
                            Button("Vybrat…") { chooseKey() }
                        }
                    }
                }
                GridRow {
                    Text(proto == .sftp && !keyPath.isEmpty ? "Heslo klíče" : "Heslo")
                    SecureField("", text: $password)
                }
            }
            .textFieldStyle(.roundedBorder)

            Toggle("Důvěřovat serveru bez ověření klíče/certifikátu", isOn: $insecure)
                .font(.system(size: 12))
            Toggle("Uložit heslo do Klíčenky", isOn: $remember)
                .font(.system(size: 12))
            Text("U SFTP lze zadat soukromý klíč (RSA/ECDSA); heslo je pak heslem ke klíči.")
                .font(.system(size: 11)).foregroundStyle(.secondary)

            HStack {
                if busy { ProgressView().controlSize(.small) }
                Spacer()
                Button("Zrušit") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Připojit") { connect() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(host.trimmingCharacters(in: .whitespaces).isEmpty || busy)
            }
        }
        .padding(16)
        .frame(width: 440)
    }

    private func fill(_ s: SavedServer) {
        proto = s.proto; host = s.host; port = String(s.port); user = s.user
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
        panel.message = "Vyberte soukromý SSH klíč"
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
                let server = SavedServer(proto: proto, host: h, port: portValue, user: user, insecure: insecure,
                                         keyPath: keyPath.isEmpty ? nil : keyPath, passwordSaved: remember)
                server.save()
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
            Text("Síť a disky").font(.headline)

            Text("Připojené disky").font(.subheadline).foregroundStyle(.secondary)
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

            Text("Počítače v síti (SMB)").font(.subheadline).foregroundStyle(.secondary)
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
                if lan.hosts.isEmpty { Text("Hledám…").foregroundStyle(.secondary) }
            }

            Text("Adresa sdílení").font(.subheadline).foregroundStyle(.secondary)
            HStack {
                TextField("smb://server/sdílená-složka", text: $address)
                    .textFieldStyle(.roundedBorder)
                Button("Připojit") { mount(address) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(address.count < 7)
            }
            Text("Podporované: smb://, afp://, nfs://. O přihlášení se postará systém.")
                .font(.system(size: 11)).foregroundStyle(.secondary)

            HStack {
                Spacer()
                Button("Zavřít") { dismiss() }.keyboardShortcut(.cancelAction)
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
