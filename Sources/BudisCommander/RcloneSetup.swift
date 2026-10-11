import Foundation
import CryptoKit

/// Služba cloudu: kterou položku v nabídce nabízíme a jaká pole k ní patří.
struct CloudProvider: Identifiable {
    struct Field: Identifiable {
        let key: String
        let label: String
        let secret: Bool
        let defaultValue: String
        var id: String { key }
    }

    let id: String
    let title: String
    let rcloneType: String
    /// true = přihlášení v prohlížeči (OAuth), false = vyplní se pole.
    let browser: Bool
    let fields: [Field]

    static let all: [CloudProvider] = [
        CloudProvider(id: "drive", title: L("Google Disk"), rcloneType: "drive", browser: true, fields: []),
        CloudProvider(id: "dropbox", title: "Dropbox", rcloneType: "dropbox", browser: true, fields: []),
        CloudProvider(id: "onedrive", title: L("Microsoft OneDrive"), rcloneType: "onedrive", browser: true, fields: []),
        CloudProvider(id: "box", title: "Box", rcloneType: "box", browser: true, fields: []),
        CloudProvider(id: "s3", title: L("Amazon S3 a kompatibilní"), rcloneType: "s3", browser: false, fields: [
            Field(key: "provider", label: L("Poskytovatel (AWS, Minio, Wasabi…)"), secret: false, defaultValue: "AWS"),
            Field(key: "access_key_id", label: "Access key ID", secret: false, defaultValue: ""),
            Field(key: "secret_access_key", label: "Secret access key", secret: true, defaultValue: ""),
            Field(key: "region", label: L("Region (např. eu-central-1)"), secret: false, defaultValue: ""),
            Field(key: "endpoint", label: L("Adresa serveru (jen mimo AWS)"), secret: false, defaultValue: ""),
        ]),
        CloudProvider(id: "webdav", title: L("WebDAV (Nextcloud, ownCloud…)"), rcloneType: "webdav", browser: false, fields: [
            Field(key: "url", label: L("Adresa (https://…)"), secret: false, defaultValue: ""),
            Field(key: "vendor", label: L("Druh (nextcloud, owncloud, other)"), secret: false, defaultValue: "other"),
            Field(key: "user", label: L("Uživatel"), secret: false, defaultValue: ""),
            Field(key: "pass", label: L("Heslo"), secret: true, defaultValue: ""),
        ]),
        CloudProvider(id: "b2", title: "Backblaze B2", rcloneType: "b2", browser: false, fields: [
            Field(key: "account", label: L("ID účtu nebo klíče"), secret: false, defaultValue: ""),
            Field(key: "key", label: L("Aplikační klíč"), secret: true, defaultValue: ""),
        ]),
    ]
}

/// Stažení programu rclone z oficiálního webu a přidání úložiště bez ruční práce s „rclone config“.
enum RcloneSetup {
    static let baseURL = "https://downloads.rclone.org"

    static var installDir: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/BudisCommander/rclone")
    }
    static var installedExe: String { installDir.appendingPathComponent("rclone").path }

    static var platformSuffix: String {
        #if arch(arm64)
        return "osx-arm64"
        #else
        return "osx-amd64"
        #endif
    }

    static func isValidName(_ name: String) -> Bool {
        guard (1...40).contains(name.count) else { return false }
        return name.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }
    }

    private static func fetchText(_ url: URL) async throws -> String {
        var req = URLRequest(url: url)
        req.timeoutInterval = 30
        let (data, response) = try await URLSession.shared.data(for: req)
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw RemoteError(message: "Server vrátil kód \(http.statusCode) pro \(url.lastPathComponent).")
        }
        return String(decoding: data, as: UTF8.self)
    }

    /// Stáhne rclone z oficiálního webu, ověří kontrolní součet SHA-256 a nainstaluje ho do složky aplikace.
    static func install(base: String = baseURL, platform: String = platformSuffix,
                        into dir: URL = installDir) async throws -> String {
        let versionText = try await fetchText(URL(string: "\(base)/version.txt")!).trimmingCharacters(in: .whitespacesAndNewlines)
        guard let version = versionText.split(separator: " ").last.map(String.init),
              version.range(of: #"^v\d+\.\d+\.\d+$"#, options: .regularExpression) != nil else {
            throw RemoteError(message: "Nečekaná verze rclone: \(versionText)")
        }
        let zipName = "rclone-\(version)-\(platform).zip"
        let sums = try await fetchText(URL(string: "\(base)/\(version)/SHA256SUMS")!)
        guard let line = sums.split(whereSeparator: \.isNewline).map({ $0.trimmingCharacters(in: .whitespaces) })
            .first(where: { $0.hasSuffix(" " + zipName) }),
              let expected = line.split(separator: " ").first?.lowercased() else {
            throw RemoteError(message: "Kontrolní součet pro \(zipName) nebyl nalezen.")
        }

        let work = FileManager.default.temporaryDirectory.appendingPathComponent("budis-rclone-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: work) }

        let (tmp, response) = try await URLSession.shared.download(from: URL(string: "\(base)/\(version)/\(zipName)")!)
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw RemoteError(message: L("Stažení selhalo (kód \(http.statusCode))."))
        }
        let zip = work.appendingPathComponent(zipName)
        try FileManager.default.moveItem(at: tmp, to: zip)
        let data = try Data(contentsOf: zip, options: .mappedIfSafe)
        let actual = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard actual == expected else {
            throw RemoteError(message: "Stažený soubor nesouhlasí s kontrolním součtem, instalace byla zrušena.")
        }

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

        guard let en = FileManager.default.enumerator(at: out, includingPropertiesForKeys: nil),
              let found = en.compactMap({ $0 as? URL }).first(where: { $0.lastPathComponent == "rclone" }) else {
            throw RemoteError(message: "Archiv neobsahuje program rclone.")
        }
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let target = dir.appendingPathComponent("rclone")
        try? FileManager.default.removeItem(at: target)
        try FileManager.default.copyItem(at: found, to: target)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: target.path)
        return target.path
    }

    /// Vytvoří úložiště bez prohlížeče (S3, WebDAV, B2); hesla rclone zašifruje sám přepínačem --obscure.
    static func addWithFields(exe: String, name: String, provider: CloudProvider, values: [String: String]) async throws {
        guard isValidName(name) else { throw RemoteError(message: L("Název smí obsahovat jen písmena, číslice, pomlčku a podtržítko.")) }
        var args = ["config", "create", name, provider.rcloneType]
        for f in provider.fields {
            let v = (values[f.key] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if !v.isEmpty { args += [f.key, v] }
        }
        args.append("--obscure")
        let r = try await Rclone.run(exe, args)
        if r.status != 0 {
            throw RemoteError(message: Rclone.message(from: r.err, fallback: "rclone config create selhal."))
        }
    }

    /// Otevře Terminál s příkazem „rclone config create“; rclone sám otevře prohlížeč s přihlášením ke službě.
    static func addWithBrowser(exe: String, name: String, provider: CloudProvider) throws {
        guard isValidName(name) else { throw RemoteError(message: L("Název smí obsahovat jen písmena, číslice, pomlčku a podtržítko.")) }
        let command = "\"\(exe)\" config create \(name) \(provider.rcloneType)"
        let escaped = command.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", "tell application \"Terminal\" to activate",
                       "-e", "tell application \"Terminal\" to do script \"\(escaped)\""]
        try p.run()
    }
}
