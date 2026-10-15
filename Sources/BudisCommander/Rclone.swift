import SwiftUI
import AppKit

private final class RcloneProcBox: @unchecked Sendable {
    private let lock = NSLock()
    private var proc: Process?
    private(set) var cancelled = false

    func set(_ p: Process) {
        lock.lock(); defer { lock.unlock() }
        proc = p
        if cancelled { p.terminate() }
    }

    func cancel() {
        lock.lock(); defer { lock.unlock() }
        cancelled = true
        proc?.terminate()
    }
}

private final class RcloneTextBox: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()

    func append(_ chunk: Data) {
        lock.lock(); defer { lock.unlock() }
        if data.count < 20_000 { data.append(chunk) }
    }

    var text: String {
        lock.lock(); defer { lock.unlock() }
        return String(decoding: data, as: UTF8.self)
    }
}

/// Pomocné funkce kolem programu rclone (cloudová úložiště: Dropbox, Google Drive, OneDrive a další).
enum Rclone {
    struct Output {
        let status: Int32
        let out: Data
        let err: String
    }

    /// Najde rclone: nejdřív cesta z nastavení, pak PATH a obvyklá místa (aplikace z Finderu nemá v PATH Homebrew).
    static func findExecutable(custom: String = "") -> String? {
        let fm = FileManager.default
        let own = (custom as NSString).expandingTildeInPath
        if !own.isEmpty, fm.isExecutableFile(atPath: own) { return own }
        if fm.isExecutableFile(atPath: RcloneSetup.installedExe) { return RcloneSetup.installedExe }
        var dirs = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
        dirs += ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "/usr/bin"]
        for d in dirs {
            let f = d + "/rclone"
            if fm.isExecutableFile(atPath: f) { return f }
        }
        return nil
    }

    /// Spustí rclone. `onErrLine` dostává řádky z chybového výstupu (průběh přenosu).
    static func run(_ exe: String, _ args: [String],
                    onErrLine: (@Sendable (String) -> Void)? = nil) async throws -> Output {
        let box = RcloneProcBox()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Output, Error>) in
                DispatchQueue.global().async {
                    let p = Process()
                    p.executableURL = URL(fileURLWithPath: exe)
                    p.arguments = args
                    let outPipe = Pipe(), errPipe = Pipe()
                    p.standardOutput = outPipe
                    p.standardError = errPipe
                    p.standardInput = FileHandle.nullDevice
                    do { try p.run() } catch { cont.resume(throwing: error); return }
                    box.set(p)

                    let errText = RcloneTextBox()
                    let group = DispatchGroup()
                    group.enter()
                    DispatchQueue.global().async {
                        var pending = Data()
                        let handle = errPipe.fileHandleForReading
                        while true {
                            let chunk = handle.availableData
                            if chunk.isEmpty { break }
                            errText.append(chunk)
                            if let cb = onErrLine {
                                pending.append(chunk)
                                while let nl = pending.firstIndex(of: 10) {
                                    cb(String(decoding: pending[pending.startIndex..<nl], as: UTF8.self))
                                    pending.removeSubrange(pending.startIndex...nl)
                                }
                            }
                        }
                        group.leave()
                    }
                    let out = outPipe.fileHandleForReading.readDataToEndOfFile()
                    p.waitUntilExit()
                    group.wait()
                    if box.cancelled {
                        cont.resume(throwing: CancellationError())
                    } else {
                        cont.resume(returning: Output(status: p.terminationStatus, out: out, err: errText.text))
                    }
                }
            }
        } onCancel: {
            box.cancel()
        }
    }

    /// Názvy nastavených úložišť (bez dvojtečky).
    static func listRemotes(_ exe: String) async throws -> [String] {
        let r = try await run(exe, ["listremotes"])
        guard r.status == 0 else { throw RemoteError(message: message(from: r.err, fallback: L("rclone listremotes selhal."))) }
        return String(decoding: r.out, as: UTF8.self)
            .split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: ":")) }
            .filter { !$0.isEmpty }
    }

    /// Z výstupu příkazu rclone link vybere odkaz (první řádek, který začíná http).
    static func parseLink(_ output: String) -> String? {
        output.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }
            .first { $0.lowercased().hasPrefix("http://") || $0.lowercased().hasPrefix("https://") }
    }

    static func message(from err: String, fallback: String) -> String {
        let lines = err.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let pick = lines.last(where: { $0.contains("ERROR") || $0.contains("Failed") || $0.contains("error") }) ?? lines.last
        return pick ?? fallback
    }

    /// Z řádku JSON logu rclone vytáhne podíl přenesených bajtů (0…1).
    static func progressParser(_ callback: (@Sendable (Double) -> Void)?) -> (@Sendable (String) -> Void)? {
        guard let callback else { return nil }
        return { line in
            guard line.contains("\"stats\""), let data = line.data(using: .utf8),
                  let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let stats = obj["stats"] as? [String: Any],
                  let total = (stats["totalBytes"] as? NSNumber)?.doubleValue, total > 0,
                  let bytes = (stats["bytes"] as? NSNumber)?.doubleValue else { return }
            callback(min(max(bytes / total, 0), 1))
        }
    }

    /// rclone vrací čas jako „2024-05-01T10:00:00.123456789Z“; zlomek sekundy se odřízne.
    static func parseDate(_ s: String) -> Date? {
        var base = s
        if let dot = s.firstIndex(of: ".") {
            let zone = s[dot...].drop(while: { $0 == "." || $0.isNumber })
            base = String(s[..<dot]) + String(zone)
        }
        return ISO8601DateFormatter().date(from: base)
    }
}

/// Cloudové úložiště: operace RemoteConnection s protokolem `.rclone` se provádějí příkazy rclone.
extension RemoteConnection {
    private func spec(_ path: String) -> String {
        host + ":" + String(path.drop(while: { $0 == "/" }))
    }

    private func rclone(_ args: [String], onErrLine: (@Sendable (String) -> Void)? = nil) async throws -> Rclone.Output {
        let r = try await Rclone.run(rcloneExe, args, onErrLine: onErrLine)
        if r.status != 0 {
            throw RemoteError(message: Rclone.message(from: r.err, fallback: L("rclone skončil s kódem \(r.status)")))
        }
        return r
    }

    func rcloneList(_ path: String) async throws -> [FileItem] {
        let r = try await rclone(["lsjson", spec(path)])
        let data = r.out.isEmpty ? Data("[]".utf8) : r.out
        guard let array = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
        var items: [FileItem] = []
        for e in array {
            guard let name = e["Name"] as? String, !name.isEmpty else { continue }
            let isDir = (e["IsDir"] as? Bool) ?? false
            let size = isDir ? 0 : Int64((e["Size"] as? NSNumber)?.int64Value ?? 0)
            let date = (e["ModTime"] as? String).flatMap { Rclone.parseDate($0) }
            let full = RemotePath.child(path, name)
            items.append(FileItem(url: URL(fileURLWithPath: full), name: name, isDirectory: isDir,
                                  size: max(size, 0), modified: date, isParent: false, remotePath: full))
        }
        return items
    }

    func rcloneDownloadFile(remote: String, to local: URL, maxBytes: Int?,
                            onProgress: (@Sendable (Double) -> Void)?) async throws {
        try FileManager.default.createDirectory(at: local.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let maxBytes {
            let r = try await rclone(["cat", "--count", String(maxBytes), spec(remote)])
            try r.out.write(to: local)
            return
        }
        _ = try await rclone(["copyto", "--use-json-log", "--stats", "300ms", "-v", spec(remote), local.path],
                             onErrLine: Rclone.progressParser(onProgress))
        onProgress?(1)
    }

    func rcloneUploadFile(local: URL, to path: String, onProgress: (@Sendable (Double) -> Void)?) async throws {
        _ = try await rclone(["copyto", "--use-json-log", "--stats", "300ms", "-v", local.path, spec(path)],
                             onErrLine: Rclone.progressParser(onProgress))
        onProgress?(1)
    }

    /// Vytvoří veřejný odkaz na soubor nebo složku (rclone link). Ne každé úložiště odkazy umí.
    func rcloneLink(_ path: String) async throws -> String {
        let r = try await Rclone.run(rcloneExe, ["link", spec(path)])
        guard r.status == 0, let link = Rclone.parseLink(String(decoding: r.out, as: UTF8.self)) else {
            throw RemoteError(message: Rclone.message(from: r.err, fallback: L("rclone odkaz nevytvořil.")))
        }
        return link
    }

    func rcloneMkdir(_ path: String) async throws { _ = try await rclone(["mkdir", spec(path)]) }

    func rcloneDelete(_ path: String, isDirectory: Bool) async throws {
        _ = try await rclone([isDirectory ? "purge" : "deletefile", spec(path)])
    }

    func rcloneRename(from: String, to: String) async throws {
        _ = try await rclone(["moveto", spec(from), spec(to)])
    }
}

struct CloudSheet: View {
    @ObservedObject var model: AppModel
    @ObservedObject var settings = Settings.shared
    @Environment(\.dismiss) private var dismiss

    @State private var remotes: [String] = []
    @State private var selected = ""
    @State private var status = ""
    @State private var exe: String?
    @State private var installing = false
    @State private var providerID = CloudProvider.all[0].id
    @State private var newName = CloudProvider.all[0].id
    @State private var values: [String: String] = [:]

    private var provider: CloudProvider { CloudProvider.all.first { $0.id == providerID } ?? CloudProvider.all[0] }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Cloudová úložiště (rclone)")).font(.headline)
            Text(L("Vyberte úložiště a připojte ho do aktivního panelu. Nové přidáte níže; program rclone si aplikace umí sama stáhnout."))
                .font(.system(size: 12)).foregroundStyle(.secondary)
            if !remotes.isEmpty {
                Picker(L("Úložiště"), selection: $selected) {
                    ForEach(remotes, id: \.self) { Text($0).tag($0) }
                }
            }
            if !status.isEmpty {
                Text(status).font(.system(size: 12)).foregroundStyle(.secondary)
            }
            if exe == nil {
                HStack {
                    Button(L("Nainstalovat rclone")) { Task { await install() } }
                        .disabled(installing)
                    if installing { ProgressView().controlSize(.small) }
                }
            }
            HStack {
                Button(L("Obnovit")) { Task { await refresh() } }
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("Připojit")) {
                    let remote = selected
                    dismiss()
                    Task { await model.connectCloud(remote) }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(selected.isEmpty)
            }
            Divider()
            Text(L("Přidat úložiště")).font(.system(size: 13, weight: .semibold))
            Picker(L("Služba"), selection: $providerID) {
                ForEach(CloudProvider.all) { Text(L($0.title)).tag($0.id) }
            }
            .onChange(of: providerID) { _, new in
                newName = new
                values = Dictionary(uniqueKeysWithValues: provider.fields.map { ($0.key, $0.defaultValue) })
            }
            TextField(L("název úložiště (písmena a číslice)"), text: $newName).textFieldStyle(.roundedBorder)
            if provider.browser {
                Text(L("Otevře se Terminál s programem rclone a prohlížeč s přihlášením ke službě. Po dokončení klikněte na Obnovit."))
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            } else {
                ForEach(provider.fields) { f in
                    if f.secret {
                        SecureField(L(f.label), text: binding(f.key)).textFieldStyle(.roundedBorder)
                    } else {
                        TextField(L(f.label), text: binding(f.key)).textFieldStyle(.roundedBorder)
                    }
                }
            }
            HStack {
                Button(L("Přidat úložiště")) { Task { await add() } }.disabled(exe == nil)
                Button(L("Pokročilé nastavení (rclone config)…")) { openConfig() }.disabled(exe == nil)
                Spacer()
            }
            TextField(L("cesta k rclone (volitelné, jinak se hledá v PATH a v Homebrew)"), text: $settings.rclonePath)
                .textFieldStyle(.roundedBorder)
        }
        .padding(16)
        .frame(width: 540)
        .task { await refresh() }
    }

    private func binding(_ key: String) -> Binding<String> {
        Binding(get: { values[key] ?? provider.fields.first { $0.key == key }?.defaultValue ?? "" },
                set: { values[key] = $0 })
    }

    private func install() async {
        installing = true
        status = L("Stahuji rclone z oficiálního webu rclone.org…")
        do {
            _ = try await RcloneSetup.install()
        } catch {
            status = L("Instalace se nezdařila: \(error.localizedDescription)")
        }
        installing = false
        await refresh()
    }

    private func add() async {
        guard let exe else { status = L("Nejdřív nainstalujte rclone."); return }
        let name = newName.trimmingCharacters(in: .whitespaces)
        guard RcloneSetup.isValidName(name) else {
            status = L("Název smí obsahovat jen písmena, číslice, pomlčku a podtržítko.")
            return
        }
        if remotes.contains(name) { status = L("Úložiště „\(name)“ už existuje."); return }
        do {
            if provider.browser {
                try RcloneSetup.addWithBrowser(exe: exe, name: name, provider: provider)
                status = L("Dokončete přihlášení v Terminálu a prohlížeči, pak klikněte na Obnovit.")
                return
            }
            var all = Dictionary(uniqueKeysWithValues: provider.fields.map { ($0.key, $0.defaultValue) })
            for (k, v) in values { all[k] = v }
            try await RcloneSetup.addWithFields(exe: exe, name: name, provider: provider, values: all)
            status = L("Úložiště „\(name)“ přidáno.")
        } catch {
            status = error.localizedDescription
        }
        await refresh(keepStatus: true)
    }

    private func refresh(keepStatus: Bool = false) async {
        exe = Rclone.findExecutable(custom: settings.rclonePath)
        guard let exe else {
            remotes = []
            selected = ""
            status = L("Program rclone zatím není nainstalovaný. Klikněte na „Nainstalovat rclone“.")
            return
        }
        do {
            remotes = try await Rclone.listRemotes(exe)
            if !remotes.contains(selected) { selected = remotes.first ?? "" }
            if !keepStatus {
                status = remotes.isEmpty ? L("Zatím není nastavené žádné úložiště. Přidejte ho níže.") : "rclone: \(exe)"
            }
        } catch {
            status = error.localizedDescription
        }
    }

    /// Otevře Terminál a spustí v něm `rclone config`.
    private func openConfig() {
        guard let exe else { return }
        let command = "\"" + exe.replacingOccurrences(of: "\"", with: "\\\"") + "\" config"
        let escaped = command.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", "tell application \"Terminal\" to activate",
                       "-e", "tell application \"Terminal\" to do script \"\(escaped)\""]
        try? p.run()
    }
}

extension AppModel {
    /// Připojí aktivní panel k cloudovému úložišti nastavenému v rclone.
    func connectCloud(_ remote: String) async {
        guard let exe = Rclone.findExecutable(custom: Settings.shared.rclonePath) else {
            Dialogs.error(L("Program rclone nebyl nalezen. Nainstalujte ho příkazem „brew install rclone“ nebo zadejte jeho cestu."))
            return
        }
        let conn = RemoteConnection(proto: .rclone, host: remote, port: 0, user: "", password: "",
                                    insecure: false, rcloneExe: exe)
        progressText = L("Připojuji \(conn.displayName)…")
        progress = 0
        do {
            _ = try await conn.list(conn.startPath)
        } catch {
            progress = nil
            progressText = ""
            if RcloneSetup.needsReconnect(error.localizedDescription),
               Dialogs.confirm(L("Nastavení úložiště „\(remote)“ není dokončené"),
                               info: L("Chybí výběr disku. Otevře se Terminál s rclone, ve kterém se znovu přihlásíte a disk vyberete (u OneDrive vyberte disk pojmenovaný OneDrive, ne první v seznamu). Potom se k úložišti připojte znovu."),
                               ok: L("Opravit")) {
                do { try RcloneSetup.reconnect(exe: exe, remote: remote) } catch { Dialogs.error(error.localizedDescription) }
                return
            }
            Dialogs.error(L("Připojení k úložišti „\(remote)“ se nezdařilo:\n\n\(error.localizedDescription)"))
            return
        }
        progress = nil
        progressText = ""
        _ = await connect(conn)
    }
}
