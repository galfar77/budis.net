import Foundation

enum RemotePath {
    static func parent(_ p: String) -> String {
        let parent = (p as NSString).deletingLastPathComponent
        return parent.isEmpty ? "/" : parent
    }
    static func child(_ p: String, _ name: String) -> String {
        (p as NSString).appendingPathComponent(name)
    }
}

enum RemoteProtocol: String, CaseIterable, Identifiable, Codable {
    case ftp = "FTP"
    case ftpTLS = "FTP + TLS"
    case sftp = "SFTP"
    /// Cloudová úložiště přes program rclone (nezobrazuje se v dialogu pro FTP/SFTP).
    case rclone = "Cloud (rclone)"

    var id: String { rawValue }
    var scheme: String {
        switch self {
        case .sftp: return "sftp"
        case .rclone: return "rclone"
        default: return "ftp"
        }
    }
    var defaultPort: Int { self == .sftp ? 22 : (self == .rclone ? 0 : 21) }
}

struct RemoteError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

private final class DataBox: @unchecked Sendable { var data = Data() }

/// Drží běžící proces curl, aby šel při zrušení úlohy ukončit.
private final class ProcBox: @unchecked Sendable {
    private let lock = NSLock()
    private var proc: Process?
    private var cancelled = false

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

/// Sečte přenesené bajty přes všechny soubory složky, aby průběh složky neskákal.
private final class Tracker: @unchecked Sendable {
    let total: Int64
    let sink: @Sendable (String, Double) -> Void
    private var done: Int64 = 0
    private let lock = NSLock()

    init(total: Int64, sink: @escaping @Sendable (String, Double) -> Void) {
        self.total = total
        self.sink = sink
    }

    func fileProgress(_ name: String, _ frac: Double, size: Int64) {
        lock.lock(); let d = done; lock.unlock()
        let overall = total > 0 ? (Double(d) + frac * Double(size)) / Double(total) : frac
        sink(name, min(max(overall, 0), 1))
    }

    func fileDone(size: Int64) {
        lock.lock(); done += size; lock.unlock()
    }
}

/// Připojení k FTP/SFTP serveru. Všechny operace běží přes systémový `/usr/bin/curl`;
/// přihlašovací údaje se předávají přes stdin (ne v argumentech procesu).
final class RemoteConnection: @unchecked Sendable {
    let proto: RemoteProtocol
    let host: String
    let port: Int
    let user: String
    let password: String
    let insecure: Bool
    /// Cesta k soukromému SSH klíči (jen SFTP); heslo je pak heslem ke klíči.
    let keyPath: String
    /// Cesta k programu rclone (jen pro protokol `.rclone`); `host` je pak název úložiště.
    let rcloneExe: String

    init(proto: RemoteProtocol, host: String, port: Int, user: String, password: String,
         insecure: Bool, keyPath: String = "", rcloneExe: String = "") {
        self.proto = proto; self.host = host; self.port = port
        self.user = user; self.password = password; self.insecure = insecure
        self.keyPath = keyPath
        self.rcloneExe = rcloneExe
    }

    var displayName: String {
        proto == .rclone ? "cloud \(host):" : "\(proto.scheme)://\(user.isEmpty ? "" : user + "@")\(host)"
    }
    /// FTP: domovský adresář = "/", SFTP: "/~" (kořen serveru je "/").
    var startPath: String { proto == .sftp ? "/~" : "/" }

    // MARK: URL a konfigurace curl

    private func url(_ path: String, directory: Bool = false) -> String {
        let parts = path.split(separator: "/", omittingEmptySubsequences: true).map {
            String($0).addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? String($0)
        }
        var s = "\(proto.scheme)://\(host):\(port)/" + parts.joined(separator: "/")
        if directory && !parts.isEmpty { s += "/" }
        return s
    }

    private func q(_ s: String) -> String {
        "\"" + s.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: "\\n") + "\""
    }

    private func config(url: String, extra: [String] = [], progress: Bool = false) -> String {
        var lines = ["url = \(q(url))", "fail", "connect-timeout = 15"]
        lines += progress ? ["progress-bar"] : ["silent", "show-error"]
        if proto == .sftp && !keyPath.isEmpty {
            let key = (keyPath as NSString).expandingTildeInPath
            lines.append("key = \(q(key))")
            if FileManager.default.fileExists(atPath: key + ".pub") { lines.append("pubkey = \(q(key + ".pub"))") }
            if !user.isEmpty { lines.append("user = \(q(user))") }
            if !password.isEmpty { lines.append("pass = \(q(password))") }
        } else if !user.isEmpty || !password.isEmpty {
            lines.append("user = \(q("\(user):\(password)"))")
        }
        if proto == .ftpTLS { lines.append("ssl-reqd") }
        if insecure { lines.append("insecure") }
        return (lines + extra).joined(separator: "\n") + "\n"
    }

    private static let percentRegex = try! NSRegularExpression(pattern: #"(\d{1,3}(?:\.\d)?)%"#)

    private static func lastPercent(in data: Data) -> Double? {
        let s = String(decoding: data, as: UTF8.self) as NSString
        guard let m = percentRegex.matches(in: s as String, range: NSRange(location: 0, length: s.length)).last else { return nil }
        return Double(s.substring(with: m.range(at: 1)))
    }

    private func run(_ config: String, onProgress: (@Sendable (Double) -> Void)? = nil) async throws -> Data {
        let box = ProcBox()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Data, Error>) in
            DispatchQueue.global().async {
                let p = Process()
                p.executableURL = URL(fileURLWithPath: "/usr/bin/curl")
                p.arguments = ["-K", "-"]
                let inPipe = Pipe(), outPipe = Pipe(), errPipe = Pipe()
                p.standardInput = inPipe
                p.standardOutput = outPipe
                p.standardError = errPipe
                do { try p.run() } catch { cont.resume(throwing: error); return }
                box.set(p)
                inPipe.fileHandleForWriting.write(Data(config.utf8))
                try? inPipe.fileHandleForWriting.close()

                let errBox = DataBox()
                let group = DispatchGroup()
                group.enter()
                DispatchQueue.global().async {
                    let handle = errPipe.fileHandleForReading
                    var lastPct = -1
                    while true {
                        let chunk = handle.availableData
                        if chunk.isEmpty { break }
                        errBox.data.append(chunk)
                        if let cb = onProgress, let pct = Self.lastPercent(in: chunk), Int(pct) > lastPct {
                            lastPct = Int(pct)
                            cb(min(pct, 100) / 100)
                        }
                    }
                    group.leave()
                }
                let out = outPipe.fileHandleForReading.readDataToEndOfFile()
                p.waitUntilExit()
                group.wait()

                if p.terminationStatus == 0 {
                    cont.resume(returning: out)
                } else {
                    var msg = String(decoding: errBox.data, as: UTF8.self)
                    if let r = msg.range(of: "curl:") { msg = String(msg[r.lowerBound...]) }
                    else if onProgress != nil { msg = "" }
                    msg = msg.trimmingCharacters(in: .whitespacesAndNewlines)
                    if msg.isEmpty { msg = L("curl skončil s kódem \(p.terminationStatus)") }
                    if p.terminationStatus == 51 || p.terminationStatus == 60 {
                        msg += L("\n\nKlíč/certifikát serveru nelze ověřit. Pokud serveru věříte, zaškrtněte při připojení „Důvěřovat serveru bez ověření“.")
                    }
                    cont.resume(throwing: RemoteError(message: msg))
                }
            }
            }
        } onCancel: {
            box.cancel()
        }
    }

    /// Cesta pro příkazy -Q (FTP: relativně k domovskému adresáři, SFTP: "/~/x" → "x").
    private func relPath(_ path: String) -> String {
        if proto == .sftp {
            if path == "/~" { return "." }
            if path.hasPrefix("/~/") { return String(path.dropFirst(3)) }
            return path
        }
        let r = String(path.drop(while: { $0 == "/" }))
        return r.isEmpty ? "." : r
    }

    private func quoted(_ s: String) -> String { "\"" + s + "\"" }

    private func command(_ cmds: [String]) async throws {
        let extra = cmds.map { "quote = \(q($0))" } + ["output = \"/dev/null\""]
        _ = try await run(config(url: url(startPath, directory: true), extra: extra))
    }

    // MARK: Operace

    func list(_ path: String) async throws -> [FileItem] {
        if proto == .rclone { return try await rcloneList(path) }
        let out = try await run(config(url: url(path, directory: true)))
        let text = String(decoding: out, as: UTF8.self)
        return text.split(whereSeparator: \.isNewline).compactMap { parseListing(String($0), in: path) }
    }

    /// Součet velikostí souborů na serveru (rekurzivně u složek).
    func totalSize(path: String, isDirectory: Bool, size: Int64) async -> Int64 {
        guard isDirectory else { return size }
        var sum: Int64 = 0
        for child in (try? await list(path)) ?? [] {
            sum += await totalSize(path: child.remotePath ?? "", isDirectory: child.isDirectory, size: child.size)
        }
        return sum
    }

    /// `progress` dostává název souboru a celkový podíl hotového přenosu (0…1) včetně všech souborů složky.
    /// `size` je velikost souboru (u složek se zjistí sama).
    func download(path: String, isDirectory: Bool, size: Int64 = 0, to local: URL, maxBytes: Int? = nil,
                  progress: (@Sendable (String, Double) -> Void)? = nil) async throws {
        var tracker: Tracker?
        if let progress, maxBytes == nil {
            tracker = Tracker(total: await totalSize(path: path, isDirectory: isDirectory, size: size), sink: progress)
        }
        try await downloadTree(path: path, isDirectory: isDirectory, size: size, to: local, maxBytes: maxBytes, tracker: tracker)
    }

    private func downloadTree(path: String, isDirectory: Bool, size: Int64, to local: URL,
                              maxBytes: Int?, tracker: Tracker?) async throws {
        if isDirectory {
            try FileManager.default.createDirectory(at: local, withIntermediateDirectories: true)
            for child in try await list(path) {
                try await downloadTree(path: child.remotePath ?? "", isDirectory: child.isDirectory, size: child.size,
                                       to: local.appendingPathComponent(child.name), maxBytes: nil, tracker: tracker)
            }
        } else {
            if proto == .rclone {
                let name = local.lastPathComponent
                try await rcloneDownloadFile(remote: path, to: local, maxBytes: maxBytes,
                                             onProgress: tracker.map { t in { @Sendable frac in t.fileProgress(name, frac, size: size) } })
                tracker?.fileDone(size: size)
                return
            }
            var extra = ["output = \(q(local.path))"]
            if let maxBytes { extra.append("range = \"0-\(maxBytes - 1)\"") }
            let name = local.lastPathComponent
            _ = try await run(config(url: url(path), extra: extra, progress: tracker != nil),
                              onProgress: tracker.map { t in { @Sendable frac in t.fileProgress(name, frac, size: size) } })
            tracker?.fileDone(size: size)
        }
    }

    func upload(local: URL, to path: String, progress: (@Sendable (String, Double) -> Void)? = nil) async throws {
        let tracker = progress.map { Tracker(total: LocalFS.totalSize(local), sink: $0) }
        try await uploadTree(local: local, to: path, tracker: tracker)
    }

    private func uploadTree(local: URL, to path: String, tracker: Tracker?) async throws {
        var isDir: ObjCBool = false
        FileManager.default.fileExists(atPath: local.path, isDirectory: &isDir)
        if isDir.boolValue {
            try? await mkdir(path)
            for child in try FileManager.default.contentsOfDirectory(at: local, includingPropertiesForKeys: nil) {
                try await uploadTree(local: child, to: RemotePath.child(path, child.lastPathComponent), tracker: tracker)
            }
        } else {
            let name = local.lastPathComponent
            let size = LocalFS.totalSize(local)
            if proto == .rclone {
                try await rcloneUploadFile(local: local, to: path,
                                           onProgress: tracker.map { t in { @Sendable frac in t.fileProgress(name, frac, size: size) } })
                tracker?.fileDone(size: size)
                return
            }
            _ = try await run(config(url: url(path), extra: ["upload-file = \(q(local.path))", "ftp-create-dirs"],
                                     progress: tracker != nil),
                              onProgress: tracker.map { t in { @Sendable frac in t.fileProgress(name, frac, size: size) } })
            tracker?.fileDone(size: size)
        }
    }

    func mkdir(_ path: String) async throws {
        if proto == .rclone { return try await rcloneMkdir(path) }
        let r = relPath(path)
        try await command([proto == .sftp ? "mkdir \(quoted(r))" : "MKD \(r)"])
    }

    func delete(path: String, isDirectory: Bool) async throws {
        if proto == .rclone { return try await rcloneDelete(path, isDirectory: isDirectory) }
        if isDirectory {
            for child in try await list(path) {
                try await delete(path: child.remotePath ?? "", isDirectory: child.isDirectory)
            }
            let r = relPath(path)
            try await command([proto == .sftp ? "rmdir \(quoted(r))" : "RMD \(r)"])
        } else {
            let r = relPath(path)
            try await command([proto == .sftp ? "rm \(quoted(r))" : "DELE \(r)"])
        }
    }

    func rename(from: String, to: String) async throws {
        if proto == .rclone { return try await rcloneRename(from: from, to: to) }
        let a = relPath(from), b = relPath(to)
        if proto == .sftp {
            try await command(["rename \(quoted(a)) \(quoted(b))"])
        } else {
            try await command(["RNFR \(a)", "RNTO \(b)"])
        }
    }

    // MARK: Parsování výpisu adresáře

    private static let unixRegex = try! NSRegularExpression(
        pattern: #"^([-dlbcps])[rwxXsStT\-+@.]{9,10}\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\w{3})\s+(\d{1,2})\s+(\d{4}|\d{1,2}:\d{2})\s+(.+?)\s*$"#)
    private static let dosRegex = try! NSRegularExpression(
        pattern: #"^(\d{2})-(\d{2})-(\d{2,4})\s+(\d{1,2}):(\d{2})\s*([AP]M)?\s+(<DIR>|\d+)\s+(.+?)\s*$"#)
    private static let months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]

    private func parseListing(_ line: String, in dir: String) -> FileItem? {
        let ns = line as NSString
        let range = NSRange(location: 0, length: ns.length)
        func group(_ m: NSTextCheckingResult, _ i: Int) -> String { ns.substring(with: m.range(at: i)) }

        if let m = Self.unixRegex.firstMatch(in: line, range: range) {
            var name = group(m, 6)
            if let arrow = name.range(of: " -> ") { name = String(name[..<arrow.lowerBound]) }
            if name == "." || name == ".." { return nil }
            let type = group(m, 1)
            var comps = DateComponents()
            comps.month = (Self.months.firstIndex(of: group(m, 3).lowercased()) ?? 0) + 1
            comps.day = Int(group(m, 4))
            let last = group(m, 5)
            let cal = Calendar.current
            if last.contains(":") {
                let t = last.split(separator: ":")
                comps.hour = Int(t[0]); comps.minute = Int(t[1])
                comps.year = cal.component(.year, from: Date())
                if let d = cal.date(from: comps), d > Date().addingTimeInterval(86400) { comps.year! -= 1 }
            } else {
                comps.year = Int(last)
            }
            return makeItem(name: name, dir: dir, isDir: type == "d" || type == "l",
                            size: Int64(group(m, 2)) ?? 0, date: cal.date(from: comps))
        }
        if let m = Self.dosRegex.firstMatch(in: line, range: range) {
            let name = group(m, 8)
            var comps = DateComponents()
            comps.month = Int(group(m, 1)); comps.day = Int(group(m, 2))
            var y = Int(group(m, 3)) ?? 0
            if y < 100 { y += y < 70 ? 2000 : 1900 }
            comps.year = y
            var h = Int(group(m, 4)) ?? 0
            if m.range(at: 6).location != NSNotFound {
                let pm = group(m, 6) == "PM"
                if pm && h < 12 { h += 12 } else if !pm && h == 12 { h = 0 }
            }
            comps.hour = h; comps.minute = Int(group(m, 5))
            let sizeStr = group(m, 7)
            return makeItem(name: name, dir: dir, isDir: sizeStr == "<DIR>",
                            size: Int64(sizeStr) ?? 0, date: Calendar.current.date(from: comps))
        }
        return nil
    }

    private func makeItem(name: String, dir: String, isDir: Bool, size: Int64, date: Date?) -> FileItem {
        let path = RemotePath.child(dir, name)
        return FileItem(url: URL(fileURLWithPath: path), name: name, isDirectory: isDir,
                        size: size, modified: date, isParent: false, remotePath: path)
    }
}
