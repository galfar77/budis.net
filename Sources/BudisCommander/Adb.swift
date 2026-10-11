import SwiftUI

/// Android telefon přes program adb (ladění přes USB): soubory v telefonu jako další panel.
enum Adb {
    struct Device: Identifiable, Equatable {
        let serial: String
        let state: String
        let model: String

        var id: String { serial }
        var ready: Bool { state == "device" }
        var title: String {
            "\(model.isEmpty ? serial : model.replacingOccurrences(of: "_", with: " "))   [\(serial)]   \(state)"
        }
    }

    static let defaultBaseURL = "https://dl.google.com/android/repository"

    static var installDir: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/BudisCommander/platform-tools")
    }
    static var installedExe: String { installDir.appendingPathComponent("adb").path }

    static func findExecutable(custom: String = "") -> String? {
        let fm = FileManager.default
        let own = (custom as NSString).expandingTildeInPath
        if !own.isEmpty, fm.isExecutableFile(atPath: own) { return own }
        if fm.isExecutableFile(atPath: installedExe) { return installedExe }
        var dirs = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
        for key in ["ANDROID_HOME", "ANDROID_SDK_ROOT"] {
            if let sdk = ProcessInfo.processInfo.environment[key], !sdk.isEmpty { dirs.append(sdk + "/platform-tools") }
        }
        dirs.append(NSHomeDirectory() + "/Library/Android/sdk/platform-tools")
        dirs += ["/opt/homebrew/bin", "/usr/local/bin"]
        for d in dirs {
            let f = d + "/adb"
            if fm.isExecutableFile(atPath: f) { return f }
        }
        return nil
    }

    /// Stáhne Android platform-tools od Google a rozbalí je do složky aplikace.
    static func install(base: String = defaultBaseURL, platform: String = "darwin",
                        into dir: URL = installDir) async throws -> String {
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("budis-adb-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: work) }
        let (tmp, response) = try await URLSession.shared.download(from: URL(string: "\(base)/platform-tools-latest-\(platform).zip")!)
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw RemoteError(message: L("Stažení selhalo (kód \(http.statusCode))."))
        }
        let zip = work.appendingPathComponent("platform-tools.zip")
        try FileManager.default.moveItem(at: tmp, to: zip)
        let out = work.appendingPathComponent("extract")
        try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        let status: Int32 = await Task.detached { () -> Int32 in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            p.arguments = ["-x", "-k", zip.path, out.path]
            do { try p.run() } catch { return 1 }
            p.waitUntilExit()
            return p.terminationStatus
        }.value
        guard status == 0 else { throw RemoteError(message: "Rozbalení staženého archivu selhalo.") }
        let source = out.appendingPathComponent("platform-tools")
        guard FileManager.default.fileExists(atPath: source.appendingPathComponent("adb").path) else {
            throw RemoteError(message: "Archiv neobsahuje program adb.")
        }
        try? FileManager.default.removeItem(at: dir)
        try FileManager.default.createDirectory(at: dir.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: source, to: dir)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: installedExe)
        return installedExe
    }

    static func parseDevices(_ text: String) -> [Device] {
        var list: [Device] = []
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("List of devices") || line.hasPrefix("*") { continue }
            let parts = line.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
            guard parts.count >= 2 else { continue }
            let model = parts.first { $0.hasPrefix("model:") }.map { String($0.dropFirst("model:".count)) } ?? ""
            list.append(Device(serial: parts[0], state: parts[1], model: model))
        }
        return list
    }

    static func listDevices(exe: String) async throws -> [Device] {
        let r = try await Rclone.run(exe, ["devices", "-l"])
        guard r.status == 0 else { throw RemoteError(message: Rclone.message(from: r.err, fallback: "adb devices selhalo.")) }
        return parseDevices(String(decoding: r.out, as: UTF8.self))
    }

    private static let entryRegex = try! NSRegularExpression(
        pattern: #"^([-dlbcps])[rwxsStT\-]{9}[.+@]?\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})\s+(.+?)\s*$"#)

    /// Zpracuje výstup „ls -la“ z telefonu (toybox i GNU ls s ISO daty).
    static func parseListing(_ text: String, in dir: String) -> [FileItem] {
        var items: [FileItem] = []
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd HH:mm"
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = String(raw)
            let ns = line as NSString
            guard let m = entryRegex.firstMatch(in: line, range: NSRange(location: 0, length: ns.length)) else { continue }
            func group(_ i: Int) -> String { ns.substring(with: m.range(at: i)) }
            var name = group(5)
            if let arrow = name.range(of: " -> ") { name = String(name[..<arrow.lowerBound]) }
            if name == "." || name == ".." { continue }
            let type = group(1)
            let isDir = type == "d" || type == "l"
            let path = RemotePath.child(dir, name)
            items.append(FileItem(url: URL(fileURLWithPath: path), name: name, isDirectory: isDir,
                                  size: isDir ? 0 : Int64(group(2)) ?? 0,
                                  modified: formatter.date(from: group(3) + " " + group(4)),
                                  isParent: false, remotePath: path))
        }
        return items
    }

    static func quote(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }
}

/// Telefon: operace RemoteConnection s protokolem `.adb` se provádějí příkazy adb (`host` je sériové číslo, `user` model).
extension RemoteConnection {
    private func adb(_ args: [String], onErrLine: (@Sendable (String) -> Void)? = nil) async throws -> Rclone.Output {
        let r = try await Rclone.run(rcloneExe, ["-s", host] + args, onErrLine: onErrLine)
        return r
    }

    private func adbShell(_ command: String) async throws {
        let r = try await adb(["shell", command])
        if r.status != 0 {
            let text = r.err.isEmpty ? String(decoding: r.out, as: UTF8.self) : r.err
            throw RemoteError(message: Rclone.message(from: text, fallback: "Příkaz v telefonu selhal."))
        }
    }

    func adbConnect() async throws {
        let r = try await adb(["get-state"])
        let state = String(decoding: r.out, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        if r.status != 0 || state != "device" {
            if state == "unauthorized" || r.err.contains("unauthorized") {
                throw RemoteError(message: "Telefon čeká na povolení: odemkněte ho a potvrďte dotaz „Povolit ladění přes USB“.")
            }
            let text = r.err.isEmpty ? state : r.err
            throw RemoteError(message: Rclone.message(from: text, fallback: "Telefon není připojený nebo nemá zapnuté ladění přes USB."))
        }
    }

    func adbList(_ path: String) async throws -> [FileItem] {
        let dir = path.hasSuffix("/") ? path : path + "/"
        let r = try await adb(["shell", "ls -la " + Adb.quote(dir)])
        let text = String(decoding: r.out, as: UTF8.self)
        let items = Adb.parseListing(text, in: path)
        if items.isEmpty && (r.status != 0 || text.contains("ls:") || text.contains("No such file")) {
            throw RemoteError(message: Rclone.message(from: text.isEmpty ? r.err : text, fallback: "Složku v telefonu nelze přečíst."))
        }
        return items
    }

    func adbDownloadFile(remote: String, to local: URL, maxBytes: Int?, onProgress: (@Sendable (Double) -> Void)?) async throws {
        try FileManager.default.createDirectory(at: local.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let maxBytes {
            let r = try await adb(["shell", "head -c \(maxBytes) " + Adb.quote(remote)])
            try r.out.write(to: local)
            return
        }
        let r = try await adb(["pull", remote, local.path])
        if r.status != 0 {
            let text = r.err.isEmpty ? String(decoding: r.out, as: UTF8.self) : r.err
            throw RemoteError(message: Rclone.message(from: text, fallback: "Stažení z telefonu selhalo."))
        }
        onProgress?(1)
    }

    func adbUploadFile(local: URL, to path: String, onProgress: (@Sendable (Double) -> Void)?) async throws {
        let r = try await adb(["push", local.path, path])
        if r.status != 0 {
            let text = r.err.isEmpty ? String(decoding: r.out, as: UTF8.self) : r.err
            throw RemoteError(message: Rclone.message(from: text, fallback: "Nahrání do telefonu selhalo."))
        }
        onProgress?(1)
    }

    func adbMkdir(_ path: String) async throws { try await adbShell("mkdir -p " + Adb.quote(path)) }

    func adbDelete(_ path: String, isDirectory: Bool) async throws {
        try await adbShell((isDirectory ? "rm -rf " : "rm -f ") + Adb.quote(path))
    }

    func adbRename(from: String, to: String) async throws {
        try await adbShell("mv \(Adb.quote(from)) \(Adb.quote(to))")
    }
}

struct PhoneSheet: View {
    @ObservedObject var model: AppModel
    @ObservedObject var settings = Settings.shared
    @Environment(\.dismiss) private var dismiss

    @State private var devices: [Adb.Device] = []
    @State private var selected: String?
    @State private var status = ""
    @State private var exe: String?
    @State private var installing = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Telefon (Android)")).font(.headline)
            Text(L("V telefonu zapněte Nastavení → Možnosti pro vývojáře → Ladění přes USB, připojte ho kabelem a potvrďte v telefonu dotaz na povolení. Soubory telefonu se pak otevřou v aktivním panelu."))
                .font(.system(size: 12)).foregroundStyle(.secondary)
            List(devices, selection: $selected) { d in Text(d.title) }
                .frame(height: 120)
            if !status.isEmpty { Text(status).font(.system(size: 12)).foregroundStyle(.secondary) }
            if exe == nil {
                HStack {
                    Button(L("Nainstalovat adb")) { Task { await install() } }.disabled(installing)
                    if installing { ProgressView().controlSize(.small) }
                }
            }
            HStack {
                Button(L("Obnovit")) { Task { await refresh() } }
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("Připojit")) {
                    guard let d = devices.first(where: { $0.serial == selected }) else { return }
                    dismiss()
                    Task { await model.connectPhone(d) }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(selected == nil)
            }
            Text(L("iPhone a iPad: Apple neumožňuje přímý přístup k souborům přes kabel, použijte AirDrop nebo iCloud."))
                .font(.system(size: 11)).foregroundStyle(.secondary)
        }
        .padding(16)
        .frame(width: 540)
        .task { await refresh() }
    }

    private func install() async {
        installing = true
        status = L("Stahuji Android platform-tools z dl.google.com…")
        do { _ = try await Adb.install() } catch { status = L("Instalace se nezdařila: \(error.localizedDescription)") }
        installing = false
        await refresh()
    }

    private func refresh() async {
        exe = Adb.findExecutable(custom: settings.adbPath)
        guard let exe else {
            devices = []
            status = L("Program adb zatím není nainstalovaný. Klikněte na „Nainstalovat adb“ (stáhne se od Googlu).")
            return
        }
        do {
            devices = try await Adb.listDevices(exe: exe)
            if selected == nil || !devices.contains(where: { $0.serial == selected }) { selected = devices.first?.serial }
            status = devices.isEmpty ? L("Žádný telefon nenalezen. Zkontrolujte kabel a ladění přes USB.") : ""
        } catch {
            status = error.localizedDescription
        }
    }
}

extension AppModel {
    /// Připojí aktivní panel k Androidu přes adb (soubory v telefonu).
    func connectPhone(_ device: Adb.Device) async {
        guard let exe = Adb.findExecutable(custom: Settings.shared.adbPath) else {
            Dialogs.error(L("Program adb nebyl nalezen. Nainstalujte ho v okně Telefon."))
            return
        }
        let conn = RemoteConnection(proto: .adb, host: device.serial, port: 0, user: device.model, password: "",
                                    insecure: false, rcloneExe: exe)
        progressText = L("Připojuji \(conn.displayName)…")
        progress = 0
        do {
            try await conn.adbConnect()
        } catch {
            progress = nil
            progressText = ""
            Dialogs.error(L("Připojení k telefonu se nezdařilo:\n\n\(error.localizedDescription)"))
            return
        }
        progress = nil
        progressText = ""
        _ = await connect(conn)
    }
}
