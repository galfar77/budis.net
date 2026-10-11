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
            throw RemoteError(message: L("GitHub vrátil kód \(http.statusCode)."))
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
            throw RemoteError(message: L("Stažení selhalo (kód \(http.statusCode))."))
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
        guard status == 0 else { throw RemoteError(message: L("Rozbalení stažené aplikace selhalo.")) }
        let apps = (try? FileManager.default.contentsOfDirectory(at: out, includingPropertiesForKeys: nil)) ?? []
        guard let app = apps.first(where: { $0.pathExtension == "app" }) else {
            throw RemoteError(message: L("Stažený archiv neobsahuje aplikaci."))
        }
        return app
    }

    /// Kam se ukládá předchozí verze po výměně (slouží k návratu zpět).
    static var previousApp: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return base.appendingPathComponent("BudisCommander/Previous/BudisCommander.app")
    }

    /// Commit verze, která je už stažená a čeká na ukončení aplikace (ať se nestahuje znovu).
    @MainActor static var stagedCommit = ""

    static var canRollBack: Bool { FileManager.default.fileExists(atPath: previousApp.path) }

    /// Skript pro výměnu aplikace: počká na konec této aplikace, starou uloží jako předchozí verzi a nahradí ji novou.
    static let installScript = """
    while kill -0 "$1" 2>/dev/null; do sleep 0.2; done
    mkdir -p "$(dirname "$4")"
    rm -rf "$4"
    if mv "$3" "$4"; then
      if mv "$2" "$3"; then :; else mv "$4" "$3"; fi
    fi
    xattr -dr com.apple.quarantine "$3" 2>/dev/null
    if [ "$5" = "1" ]; then "${BUDIS_OPEN:-open}" "$3"; fi
    """

    /// Skript pro návrat: po skončení aplikace prohodí aktuální a předchozí verzi a spustí aplikaci.
    static let rollbackScript = """
    while kill -0 "$1" 2>/dev/null; do sleep 0.2; done
    tmp="$3.swap"
    rm -rf "$tmp"
    if mv "$3" "$tmp"; then
      if mv "$4" "$3"; then mv "$tmp" "$4"; else mv "$tmp" "$3"; fi
    fi
    xattr -dr com.apple.quarantine "$3" 2>/dev/null
    "${BUDIS_OPEN:-open}" "$3"
    """

    @MainActor
    private static func runHelper(_ script: String, args: [String]) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = ["-c", script, "bash", String(ProcessInfo.processInfo.processIdentifier)] + args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        try p.run()
    }

    @MainActor
    private static func checkTarget() throws -> URL {
        let target = Bundle.main.bundleURL
        guard target.pathExtension == "app" else {
            throw RemoteError(message: L("Aplikace neběží jako balíček .app (spuštěná příkazem swift run)."))
        }
        guard FileManager.default.isWritableFile(atPath: target.deletingLastPathComponent().path) else {
            throw RemoteError(message: L("Do složky „\(target.deletingLastPathComponent().path)“ nelze zapisovat. Přesuňte aplikaci třeba do složky Aplikace ve vašem domovském adresáři."))
        }
        return target
    }

    /// Nahradí běžící aplikaci novou a spustí ji. Výměnu dělá pomocný skript, který počká na konec této aplikace.
    @MainActor
    static func installAndRelaunch(_ newApp: URL) throws {
        let target = try checkTarget()
        try runHelper(installScript, args: [newApp.path, target.path, previousApp.path, "1"])
        NSApp.terminate(nil)
    }

    /// Připraví výměnu na okamžik, kdy aplikaci příště ukončíte; nic nerestartuje.
    @MainActor
    static func installOnNextQuit(_ newApp: URL) throws {
        let target = try checkTarget()
        try runHelper(installScript, args: [newApp.path, target.path, previousApp.path, "0"])
    }

    /// Vrátí předchozí verzi (aktuální se uloží, takže jde přepnout zpět) a aplikaci restartuje.
    @MainActor
    static func rollBackAndRelaunch() throws {
        let target = try checkTarget()
        guard canRollBack else { throw RemoteError(message: L("Předchozí verze není k dispozici. Uloží se při příští aktualizaci.")) }
        try runHelper(rollbackScript, args: ["", target.path, previousApp.path])
        NSApp.terminate(nil)
    }
}

extension AppModel {
    /// Zeptá se na novou verzi, stáhne ji a vymění aplikaci.
    func checkForUpdate() async {
        guard Updater.isAppBundle else {
            Dialogs.error(L("Aktualizace funguje jen v aplikaci .app (ne při spuštění příkazem swift run)."))
            return
        }
        notice = L("Zjišťuji, jestli je nová verze…")
        let info: UpdateInfo?
        do {
            info = try await Updater.check(current: Updater.currentCommit)
        } catch {
            notice = nil
            Dialogs.error(L("Kontrola aktualizací se nezdařila (je Mac online?):\n\n\(error.localizedDescription)"))
            return
        }
        notice = nil
        Settings.shared.lastUpdateCheck = Date().timeIntervalSince1970
        guard let info else {
            showNotice(L("Máte nejnovější verzi."))
            return
        }
        let size = ByteCountFormatter.string(fromByteCount: info.size, countStyle: .file)
        guard Dialogs.confirm(L("Je dostupná nová verze"),
                              info: L("Stáhne se \(size) a aplikace se po výměně restartuje."), ok: L("Aktualizovat")) else { return }
        progressText = L("Stahuji aktualizaci…")
        progress = 0
        do {
            let newApp = try await Updater.downloadAndUnpack(info)
            progress = nil
            progressText = ""
            try Updater.installAndRelaunch(newApp)
        } catch {
            progress = nil
            progressText = ""
            Dialogs.error(L("Aktualizace se nezdařila:\n\n\(error.localizedDescription)"))
        }
    }

    /// Vrátí předchozí verzi aplikace a restartuje ji.
    func rollBackVersion() {
        guard Updater.isAppBundle else {
            Dialogs.error(L("Aktualizace funguje jen v aplikaci .app (ne při spuštění příkazem swift run)."))
            return
        }
        guard Updater.canRollBack else {
            Dialogs.error(L("Předchozí verze není k dispozici. Uloží se při příští aktualizaci."))
            return
        }
        guard Dialogs.confirm(L("Vrátit předchozí verzi?"),
                              info: L("Teď nainstalovaná verze se uloží a půjde na ni přepnout zpět stejným příkazem. Aplikace se restartuje."),
                              ok: L("Vrátit")) else { return }
        do { try Updater.rollBackAndRelaunch() } catch { Dialogs.error(error.localizedDescription) }
    }

    /// Tiše zjistí, jestli je venku nová verze (při startu, nejvýš jednou za 20 hodin), a jen to oznámí.
    func autoCheckForUpdate() async {
        let settings = Settings.shared
        // S automatickou instalací se kontroluje častěji (nejvýš jednou za hodinu), jinak jednou za 20 hodin.
        let interval: Double = settings.autoInstallUpdates ? 3600 : 20 * 3600
        guard settings.autoCheckUpdates, Updater.isAppBundle, !Updater.currentCommit.isEmpty,
              Date().timeIntervalSince1970 - settings.lastUpdateCheck > interval else { return }
        let info: UpdateInfo?
        do {
            info = try await Updater.check(current: Updater.currentCommit)
            settings.lastUpdateCheck = Date().timeIntervalSince1970
        } catch {
            return      // bez internetu se nic neděje
        }
        guard let info else { return }
        guard settings.autoInstallUpdates else {
            showNotice(L("Je dostupná nová verze Budis Commanderu. Nainstalujete ji v menu Nápověda → Aktualizovat aplikaci."))
            return
        }
        if Updater.stagedCommit == info.commit { return }       // tahle verze už je stažená a čeká na ukončení aplikace
        do {
            let newApp = try await Updater.downloadAndUnpack(info)
            try Updater.installOnNextQuit(newApp)
            Updater.stagedCommit = info.commit
            showNotice(L("Nová verze je stažená a nainstaluje se při ukončení aplikace. Předchozí verzi vrátíte v menu Nápověda."))
        } catch {
            showNotice(L("Automatická instalace aktualizace se nezdařila: \(error.localizedDescription)"))
        }
    }
}
