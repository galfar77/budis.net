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

    var id: String { rawValue }
    var scheme: String { self == .sftp ? "sftp" : "ftp" }
    var defaultPort: Int { self == .sftp ? 22 : 21 }
}

struct RemoteError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

private final class DataBox: @unchecked Sendable { var data = Data() }

/// Připojení k FTP/SFTP serveru. Všechny operace běží přes systémový `/usr/bin/curl`;
/// přihlašovací údaje se předávají přes stdin (ne v argumentech procesu).
final class RemoteConnection: @unchecked Sendable {
    let proto: RemoteProtocol
    let host: String
    let port: Int
    let user: String
    let password: String
    let insecure: Bool

    init(proto: RemoteProtocol, host: String, port: Int, user: String, password: String, insecure: Bool) {
        self.proto = proto; self.host = host; self.port = port
        self.user = user; self.password = password; self.insecure = insecure
    }

    var displayName: String { "\(proto.scheme)://\(user.isEmpty ? "" : user + "@")\(host)" }
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

    private func config(url: String, extra: [String] = []) -> String {
        var lines = ["url = \(q(url))", "silent", "show-error", "fail", "connect-timeout = 15"]
        if !user.isEmpty || !password.isEmpty { lines.append("user = \(q("\(user):\(password)"))") }
        if proto == .ftpTLS { lines.append("ssl-reqd") }
        if insecure { lines.append("insecure") }
        return (lines + extra).joined(separator: "\n") + "\n"
    }

    private func run(_ config: String) async throws -> Data {
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
                inPipe.fileHandleForWriting.write(Data(config.utf8))
                try? inPipe.fileHandleForWriting.close()

                let errBox = DataBox()
                let group = DispatchGroup()
                group.enter()
                DispatchQueue.global().async {
                    errBox.data = errPipe.fileHandleForReading.readDataToEndOfFile()
                    group.leave()
                }
                let out = outPipe.fileHandleForReading.readDataToEndOfFile()
                p.waitUntilExit()
                group.wait()

                if p.terminationStatus == 0 {
                    cont.resume(returning: out)
                } else {
                    var msg = String(decoding: errBox.data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                    if msg.isEmpty { msg = "curl skončil s kódem \(p.terminationStatus)" }
                    if p.terminationStatus == 51 || p.terminationStatus == 60 {
                        msg += "\n\nKlíč/certifikát serveru nelze ověřit. Pokud serveru věříte, zaškrtněte při připojení „Důvěřovat serveru bez ověření“."
                    }
                    cont.resume(throwing: RemoteError(message: msg))
                }
            }
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
        let out = try await run(config(url: url(path, directory: true)))
        let text = String(decoding: out, as: UTF8.self)
        return text.split(whereSeparator: \.isNewline).compactMap { parseListing(String($0), in: path) }
    }

    func download(path: String, isDirectory: Bool, to local: URL, maxBytes: Int? = nil) async throws {
        if isDirectory {
            try FileManager.default.createDirectory(at: local, withIntermediateDirectories: true)
            for child in try await list(path) {
                try await download(path: child.remotePath ?? "", isDirectory: child.isDirectory,
                                   to: local.appendingPathComponent(child.name))
            }
        } else {
            var extra = ["output = \(q(local.path))"]
            if let maxBytes { extra.append("range = \"0-\(maxBytes - 1)\"") }
            _ = try await run(config(url: url(path), extra: extra))
        }
    }

    func upload(local: URL, to path: String) async throws {
        var isDir: ObjCBool = false
        FileManager.default.fileExists(atPath: local.path, isDirectory: &isDir)
        if isDir.boolValue {
            try? await mkdir(path)
            for child in try FileManager.default.contentsOfDirectory(at: local, includingPropertiesForKeys: nil) {
                try await upload(local: child, to: RemotePath.child(path, child.lastPathComponent))
            }
        } else {
            _ = try await run(config(url: url(path), extra: ["upload-file = \(q(local.path))", "ftp-create-dirs"]))
        }
    }

    func mkdir(_ path: String) async throws {
        let r = relPath(path)
        try await command([proto == .sftp ? "mkdir \(quoted(r))" : "MKD \(r)"])
    }

    func delete(path: String, isDirectory: Bool) async throws {
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
