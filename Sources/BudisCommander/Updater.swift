import Foundation
import AppKit

struct UpdateInfo {
    let commit: String
    let assetURL: URL
    let size: Int64
}

/// Kontrola nové verze na stránce Releases (tag macos-latest) a výměna běžící aplikace.
enum Updater {
    static let repo = "galfar77/budis.net"
    static let tag = "macos-latest"
    static let assetName = "BudisCommander.zip"

    /// Commit, ze kterého byla aplikace sestavena (zapsaný do Info.plist skriptem make-app.sh).
    static var currentCommit: String {
        ((Bundle.main.object(forInfoDictionaryKey: "BudisCommit") as? String) ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Jestli aplikace běží jako balíček .app (jen ten jde vyměnit za novou verzi).
    static var isAppBundle: Bool { Bundle.main.bundleURL.pathExtension == "app" }

    /// Vrátí informace o nové verzi, nebo nil, když je aplikace aktuální.
    static func check(current: String) async throws -> UpdateInfo? {
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/tags/\(tag)")!)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("BudisCommander", forHTTPHeaderField: "User-Agent")
        req.timeoutInterval = 20
        let (data, response) = try await URLSession.shared.data(for: req)
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw RemoteError(message: "GitHub vrátil kód \(http.statusCode).")
        }
        guard let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let body = obj["body"] as? String else { return nil }
        let regex = try NSRegularExpression(pattern: #"commitu:\s*`?([0-9a-fA-F]{7,40})`?"#)
        guard let m = regex.firstMatch(in: body, range: NSRange(body.startIndex..., in: body)),
              let r = Range(m.range(at: 1), in: body) else { return nil }
        let commit = String(body[r]).lowercased()
        let cur = current.lowercased()
        if cur.count >= 7 && (commit.hasPrefix(cur) || cur.hasPrefix(commit)) { return nil }
        guard let assets = obj["assets"] as? [[String: Any]],
              let asset = assets.first(where: { ($0["name"] as? String) == assetName }),
              let urlString = asset["browser_download_url"] as? String,
              let url = URL(string: urlString) else { return nil }
        return UpdateInfo(commit: commit, assetURL: url, size: (asset["size"] as? NSNumber)?.int64Value ?? 0)
    }

    /// Stáhne ZIP a rozbalí ho; vrátí cestu k nové aplikaci .app.
    static func downloadAndUnpack(_ info: UpdateInfo) async throws -> URL {
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("budis-update-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        let (tmp, response) = try await URLSession.shared.download(from: info.assetURL)
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw RemoteError(message: "Stažení selhalo (kód \(http.statusCode)).")
        }
        let zip = work.appendingPathComponent(assetName)
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
        guard status == 0 else { throw RemoteError(message: "Rozbalení stažené aplikace selhalo.") }
        let apps = (try? FileManager.default.contentsOfDirectory(at: out, includingPropertiesForKeys: nil)) ?? []
        guard let app = apps.first(where: { $0.pathExtension == "app" }) else {
            throw RemoteError(message: "Stažený archiv neobsahuje aplikaci.")
        }
        return app
    }

    /// Nahradí běžící aplikaci novou a spustí ji. Výměnu dělá pomocný skript, který počká na konec této aplikace.
    @MainActor
    static func installAndRelaunch(_ newApp: URL) throws {
        let target = Bundle.main.bundleURL
        guard target.pathExtension == "app" else {
            throw RemoteError(message: "Aplikace neběží jako balíček .app (spuštěná příkazem swift run).")
        }
        guard FileManager.default.isWritableFile(atPath: target.deletingLastPathComponent().path) else {
            throw RemoteError(message: "Do složky „\(target.deletingLastPathComponent().path)“ nelze zapisovat. Přesuňte aplikaci třeba do složky Aplikace ve vašem domovském adresáři.")
        }
        let script = """
        while kill -0 "$1" 2>/dev/null; do sleep 0.2; done
        rm -rf "$3.old"
        if mv "$3" "$3.old"; then
          if mv "$2" "$3"; then rm -rf "$3.old"; else mv "$3.old" "$3"; fi
        fi
        xattr -dr com.apple.quarantine "$3" 2>/dev/null
        open "$3"
        """
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = ["-c", script, "bash", String(ProcessInfo.processInfo.processIdentifier), newApp.path, target.path]
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        try p.run()
        NSApp.terminate(nil)
    }
}

extension AppModel {
    /// Zeptá se na novou verzi, stáhne ji a vymění aplikaci.
    func checkForUpdate() async {
        guard Updater.isAppBundle else {
            Dialogs.error("Aktualizace funguje jen v aplikaci .app (ne při spuštění příkazem swift run).")
            return
        }
        notice = "Zjišťuji, jestli je nová verze…"
        let info: UpdateInfo?
        do {
            info = try await Updater.check(current: Updater.currentCommit)
        } catch {
            notice = nil
            Dialogs.error("Kontrola aktualizací se nezdařila (je Mac online?):\n\n\(error.localizedDescription)")
            return
        }
        notice = nil
        Settings.shared.lastUpdateCheck = Date().timeIntervalSince1970
        guard let info else {
            showNotice("Máte nejnovější verzi.")
            return
        }
        let size = ByteCountFormatter.string(fromByteCount: info.size, countStyle: .file)
        guard Dialogs.confirm("Je dostupná nová verze",
                              info: "Stáhne se \(size) a aplikace se po výměně restartuje.", ok: "Aktualizovat") else { return }
        progressText = "Stahuji aktualizaci…"
        progress = 0
        do {
            let newApp = try await Updater.downloadAndUnpack(info)
            progress = nil
            progressText = ""
            try Updater.installAndRelaunch(newApp)
        } catch {
            progress = nil
            progressText = ""
            Dialogs.error("Aktualizace se nezdařila:\n\n\(error.localizedDescription)")
        }
    }

    /// Tiše zjistí, jestli je venku nová verze (při startu, nejvýš jednou za 20 hodin), a jen to oznámí.
    func autoCheckForUpdate() async {
        let settings = Settings.shared
        guard settings.autoCheckUpdates, Updater.isAppBundle, !Updater.currentCommit.isEmpty,
              Date().timeIntervalSince1970 - settings.lastUpdateCheck > 20 * 3600 else { return }
        do {
            let info = try await Updater.check(current: Updater.currentCommit)
            settings.lastUpdateCheck = Date().timeIntervalSince1970
            if info != nil {
                showNotice("Je dostupná nová verze Budis Commanderu. Nainstalujete ji v menu Nástroje → Aktualizovat aplikaci.")
            }
        } catch {
            // bez internetu se nic neděje
        }
    }
}
