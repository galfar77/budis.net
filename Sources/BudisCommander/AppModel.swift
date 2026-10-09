import Foundation
import AppKit

struct ViewerContent: Identifiable {
    let id = UUID()
    let title: String
    let text: String
}

enum ActiveSheet: String, Identifiable {
    case server, network
    var id: String { rawValue }
}

@MainActor
final class AppModel: ObservableObject {
    let left: PaneState
    let right: PaneState
    @Published var activeIsLeft = true
    @Published var progress: Double?
    @Published var progressText = ""
    @Published var viewer: ViewerContent?
    @Published var sheet: ActiveSheet?

    var active: PaneState { activeIsLeft ? left : right }
    var other: PaneState { activeIsLeft ? right : left }

    init() {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first ?? home
        left = PaneState(url: home)
        right = PaneState(url: downloads)
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                // Vzdálené panely se při návratu do aplikace neobnovují (zbytečný provoz).
                if self?.left.connection == nil { self?.left.reload() }
                if self?.right.connection == nil { self?.right.reload() }
            }
        }
    }

    func switchPane() { activeIsLeft.toggle() }

    private func describe(_ items: [FileItem]) -> String {
        items.count == 1 ? "„\(items[0].name)“" : "\(items.count) položek"
    }

    private func exists(_ name: String, in pane: PaneState) -> Bool {
        if pane.connection != nil { return pane.items.contains { !$0.isParent && $0.name == name } }
        return FileManager.default.fileExists(atPath: pane.url.appendingPathComponent(name).path)
    }

    private func newTempDir() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    // MARK: F5 / F6

    func transfer(move: Bool) async {
        let src = active, dst = other
        let items = src.targets
        guard !items.isEmpty else { return }

        let sameFolder: Bool
        if src.connection == nil && dst.connection == nil { sameFolder = src.url == dst.url }
        else { sameFolder = src.connection === dst.connection && src.remotePath == dst.remotePath }
        if sameFolder {
            Dialogs.error("Zdrojový a cílový adresář jsou stejné.")
            return
        }
        let destTitle = dst.title
        let verb = move ? "Přesunout" : "Kopírovat"
        guard Dialogs.confirm("\(verb) \(describe(items))?", info: "Cíl: \(destTitle)", ok: verb) else { return }

        var overwriteAll = false
        var errors: [String] = []
        for (i, item) in items.enumerated() {
            progress = Double(i) / Double(items.count)
            progressText = "\(verb) \(item.name) (\(i + 1)/\(items.count))"

            if src.connection == nil && dst.connection == nil && item.isDirectory {
                let d = dst.url.path, s = item.url.path
                if d == s || d.hasPrefix(s + "/") {
                    errors.append("\(item.name): nelze \(move ? "přesunout" : "kopírovat") adresář do sebe sama")
                    continue
                }
            }
            var overwrite = false
            if exists(item.name, in: dst) {
                if overwriteAll {
                    overwrite = true
                } else {
                    switch Dialogs.conflict(item.name) {
                    case .overwrite: overwrite = true
                    case .overwriteAll: overwrite = true; overwriteAll = true
                    case .skip: continue
                    case .cancel:
                        finish(src, dst)
                        return
                    }
                }
            }
            do {
                try await transferOne(item, src: src, dst: dst, move: move, overwrite: overwrite)
            } catch {
                errors.append("\(item.name): \(error.localizedDescription)")
            }
        }
        src.marked = []
        finish(src, dst)
        if !errors.isEmpty { Dialogs.error(errors.joined(separator: "\n")) }
    }

    private func transferOne(_ item: FileItem, src: PaneState, dst: PaneState, move: Bool, overwrite: Bool) async throws {
        switch (src.connection, dst.connection) {
        case (nil, nil):
            let source = item.url
            let dest = dst.url.appendingPathComponent(item.name)
            try await Task.detached {
                let fm = FileManager.default
                if overwrite && fm.fileExists(atPath: dest.path) { try fm.removeItem(at: dest) }
                if move { try fm.moveItem(at: source, to: dest) } else { try fm.copyItem(at: source, to: dest) }
            }.value

        case (nil, let d?):
            try await d.upload(local: item.url, to: RemotePath.child(dst.remotePath, item.name))
            if move { try FileManager.default.removeItem(at: item.url) }

        case (let s?, nil):
            guard let path = item.remotePath else { return }
            let dest = dst.url.appendingPathComponent(item.name)
            if overwrite && FileManager.default.fileExists(atPath: dest.path) {
                try FileManager.default.removeItem(at: dest)
            }
            try await s.download(path: path, isDirectory: item.isDirectory, to: dest)
            if move { try await s.delete(path: path, isDirectory: item.isDirectory) }

        case (let s?, let d?):
            guard let path = item.remotePath else { return }
            let tmp = try newTempDir()
            defer { try? FileManager.default.removeItem(at: tmp) }
            let local = tmp.appendingPathComponent(item.name)
            try await s.download(path: path, isDirectory: item.isDirectory, to: local)
            try await d.upload(local: local, to: RemotePath.child(dst.remotePath, item.name))
            if move { try await s.delete(path: path, isDirectory: item.isDirectory) }
        }
    }

    private func finish(_ a: PaneState, _ b: PaneState) {
        progress = nil
        progressText = ""
        a.reload()
        b.reload()
    }

    // MARK: F8

    func delete() async {
        let pane = active
        let items = pane.targets
        guard !items.isEmpty else { return }
        let conn = pane.connection
        let ok = conn == nil
            ? Dialogs.confirm("Přesunout \(describe(items)) do koše?", ok: "Do koše")
            : Dialogs.confirm("Trvale smazat \(describe(items)) ze serveru?", info: "Tuto akci nelze vrátit zpět.", ok: "Smazat")
        guard ok else { return }

        var errors: [String] = []
        for (i, item) in items.enumerated() {
            progress = Double(i) / Double(items.count)
            progressText = "Mažu \(item.name)"
            do {
                if let conn, let path = item.remotePath {
                    try await conn.delete(path: path, isDirectory: item.isDirectory)
                } else {
                    let url = item.url
                    try await Task.detached { try FileManager.default.trashItem(at: url, resultingItemURL: nil) }.value
                }
            } catch {
                errors.append("\(item.name): \(error.localizedDescription)")
            }
        }
        pane.marked = []
        progress = nil
        progressText = ""
        pane.reload()
        if !errors.isEmpty { Dialogs.error(errors.joined(separator: "\n")) }
    }

    // MARK: F7, F2

    func makeDirectory() async {
        let pane = active
        guard let name = Dialogs.prompt("Nový adresář", info: "Vytvoří se v \(pane.title)", ok: "Vytvořit") else { return }
        do {
            if let conn = pane.connection {
                try await conn.mkdir(RemotePath.child(pane.remotePath, name))
                await pane.loadRemote(pane.remotePath)
            } else {
                try FileManager.default.createDirectory(at: pane.url.appendingPathComponent(name),
                                                        withIntermediateDirectories: true)
                pane.reload()
            }
            if let idx = pane.items.firstIndex(where: { $0.name == name }) { pane.cursor = idx }
        } catch {
            Dialogs.error(error.localizedDescription)
        }
    }

    func rename() async {
        let pane = active
        guard let item = pane.current, !item.isParent,
              let name = Dialogs.prompt("Přejmenovat", initial: item.name, ok: "Přejmenovat"),
              name != item.name else { return }
        do {
            if let conn = pane.connection, let path = item.remotePath {
                try await conn.rename(from: path, to: RemotePath.child(RemotePath.parent(path), name))
                await pane.loadRemote(pane.remotePath)
            } else {
                let dest = item.url.deletingLastPathComponent().appendingPathComponent(name)
                try FileManager.default.moveItem(at: item.url, to: dest)
                pane.reload()
            }
            if let idx = pane.items.firstIndex(where: { $0.name == name }) { pane.cursor = idx }
        } catch {
            Dialogs.error(error.localizedDescription)
        }
    }

    // MARK: F3 / F4

    func view() async {
        let pane = active
        guard let item = pane.current, !item.isParent else { return }
        if item.isDirectory { pane.enter(); return }

        var fileURL = item.url
        if let conn = pane.connection, let path = item.remotePath {
            do {
                let tmp = try newTempDir()
                fileURL = tmp.appendingPathComponent(item.name)
                try await conn.download(path: path, isDirectory: false, to: fileURL, maxBytes: 512 * 1024)
            } catch {
                Dialogs.error(error.localizedDescription)
                return
            }
        }
        guard let handle = try? FileHandle(forReadingFrom: fileURL) else { return }
        defer { try? handle.close() }
        let data = (try? handle.read(upToCount: 512 * 1024)) ?? Data()
        if data.contains(0) {
            NSWorkspace.shared.open(fileURL)   // binární soubor – výchozí aplikace
            return
        }
        viewer = ViewerContent(title: item.name, text: String(decoding: data, as: UTF8.self))
    }

    func edit() {
        let pane = active
        guard let item = pane.current, !item.isParent, !item.isDirectory else { return }
        if pane.connection != nil {
            Dialogs.error("Editace souborů na serveru zatím není podporovaná. Soubor nejdřív zkopírujte (F5) do lokálního panelu.")
            return
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-t", item.url.path]
        try? p.run()
    }

    // MARK: Označování podle masky

    func markByMask(on: Bool) {
        guard let mask = Dialogs.prompt(on ? "Označit podle masky" : "Odznačit podle masky",
                                        info: "Např. *.jpg", initial: "*") else { return }
        active.mark(matching: mask, on: on)
    }

    // MARK: Servery a síť

    func connect(_ conn: RemoteConnection) async -> Bool {
        await active.loadRemote(conn.startPath, select: "", using: conn)
    }

    static func mountedVolumes() -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: "/Volumes")) ?? []).sorted()
    }

    /// Připojí síťový disk (smb://, afp://, nfs://…) přes systém a otevře ho v aktivním panelu.
    func mount(_ address: String) async {
        let before = Set(Self.mountedVolumes())
        let escaped = address.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let script = "mount volume \"\(escaped)\""
        progressText = "Připojuji \(address)…"
        progress = 0
        let (status, err): (Int32, String) = await Task.detached { () -> (Int32, String) in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            p.arguments = ["-e", script]
            let pipe = Pipe()
            p.standardError = pipe
            p.standardOutput = Pipe()
            do { try p.run() } catch { return (1, error.localizedDescription) }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            return (p.terminationStatus, String(decoding: data, as: UTF8.self))
        }.value
        progress = nil
        progressText = ""
        if status != 0 {
            Dialogs.error("Disk se nepodařilo připojit:\n\(err.trimmingCharacters(in: .whitespacesAndNewlines))")
            return
        }
        let added = Set(Self.mountedVolumes()).subtracting(before).sorted()
        active.navigate(to: URL(fileURLWithPath: added.first.map { "/Volumes/\($0)" } ?? "/Volumes"))
    }
}
