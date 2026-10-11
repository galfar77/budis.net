import SwiftUI

final class CancelFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false
    var isSet: Bool { lock.lock(); defer { lock.unlock() }; return value }
    func set() { lock.lock(); value = true; lock.unlock() }
}

/// Nález uvnitř archivu.
struct ArchiveHit: Identifiable, Hashable {
    let archive: URL
    let entry: String
    var id: String { archive.path + "\u{0}" + entry }
}

/// Hledání uvnitř archivů: podle názvu položky a (volitelně) podle textu v obsahu.
enum ArchiveSearch {
    static let maxArchiveBytes: Int64 = 300 * 1024 * 1024

    /// Názvy položek archivu (bez složek) – přes systémový tar (zip, tar, 7z, rar…).
    static func entries(of archive: URL) -> [String] {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        p.arguments = ["-tf", archive.path]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return [] }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else { return [] }
        return String(decoding: data, as: UTF8.self).split(separator: "\n").map(String.init)
            .filter { !$0.hasSuffix("/") }
    }

    /// Najde položky odpovídající masce (a textu). Pro hledání textu se archiv rozbalí do dočasné složky.
    static func search(archive: URL, mask: String, text: String, limit: Int, flag: CancelFlag) -> [String] {
        let names = entries(of: archive).filter {
            fnmatch(mask, ($0 as NSString).lastPathComponent, FNM_CASEFOLD) == 0
        }
        if text.isEmpty { return Array(names.prefix(limit)) }
        if names.isEmpty { return [] }
        let size = Int64((try? archive.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0)
        if size > maxArchiveBytes { return [] }
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("budis-asearch-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: tmp) }
        do { try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true) } catch { return [] }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        p.arguments = ["-xf", archive.path, "-C", tmp.path]
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return [] }
        p.waitUntilExit()
        var found: [String] = []
        for name in names {
            if flag.isSet || found.count >= limit { break }
            if FileSearch.fileContains(tmp.appendingPathComponent(name), text) { found.append(name) }
        }
        return found
    }
}

/// Rekurzivní hledání souborů podle masky názvu a případně obsahu.
final class FileSearch: ObservableObject {
    @Published var results: [URL] = []
    @Published var archiveHits: [ArchiveHit] = []
    @Published var filesRead = 0
    @Published var readLimitReached = false
    static let maxFilesRead = 300
    @Published var running = false
    @Published var scanned = 0
    private var flag = CancelFlag()
    static let limit = 5000

    func start(root: URL, mask: String, text: String, hidden: Bool, inArchives: Bool = false) {
        stop()
        results = []
        archiveHits = []
        scanned = 0
        running = true
        let flag = CancelFlag()
        self.flag = flag
        var pattern = mask.trimmingCharacters(in: .whitespaces)
        if pattern.isEmpty { pattern = "*" }
        else if !pattern.contains("*") && !pattern.contains("?") { pattern = "*\(pattern)*" }
        let needle = text
        let finalPattern = pattern

        Task.detached(priority: .userInitiated) { [weak self] in
            FileSearch.walk(root, finalPattern, needle, hidden, inArchives, flag) { batch, archiveBatch, scanned in
                DispatchQueue.main.async {
                    self?.results.append(contentsOf: batch)
                    self?.archiveHits.append(contentsOf: archiveBatch)
                    self?.scanned = scanned
                }
            }
            DispatchQueue.main.async { self?.running = false }
        }
    }

    // MARK: Hledání přes Spotlight

    private static func spotlightEscape(_ s: String) -> String {
        s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
    }

    /// Dotaz pro mdfind: názvy podle masek (oddělených středníkem) a případně text v obsahu.
    static func spotlightQuery(mask: String, text: String) -> String {
        let patterns = (mask.trimmingCharacters(in: .whitespaces).isEmpty ? "*" : mask)
            .split(separator: ";").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            .map { p -> String in (p.contains("*") || p.contains("?")) ? p : "*\(p)*" }
        let names = patterns.map { "kMDItemFSName == \"\(spotlightEscape($0))\"c" }.joined(separator: " || ")
        var query = "(" + (names.isEmpty ? "kMDItemFSName == \"*\"" : names) + ")"
        let needle = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if !needle.isEmpty { query += " && kMDItemTextContent == \"\(spotlightEscape(needle))\"cd" }
        return query
    }

    private static func runSpotlight(_ query: String, root: String, flag: CancelFlag) -> [URL] {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/mdfind")
        p.arguments = ["-onlyin", root, query]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return [] }
        var buffer = Data()
        var urls: [URL] = []
        let handle = out.fileHandleForReading
        while !flag.isSet {
            let chunk = handle.availableData
            if chunk.isEmpty { break }
            buffer.append(chunk)
            while let nl = buffer.firstIndex(of: 10) {
                let line = String(decoding: buffer[buffer.startIndex..<nl], as: UTF8.self)
                buffer.removeSubrange(buffer.startIndex...nl)
                if !line.isEmpty { urls.append(URL(fileURLWithPath: line)) }
                if urls.count >= FileSearch.limit { p.terminate(); return urls }
            }
        }
        if flag.isSet { p.terminate() }
        p.waitUntilExit()
        return urls
    }

    /// Nejdřív zkusí rychlý index Spotlight; když nic nenajde (složka není indexovaná), hledá procházením složek.
    func startWithIndex(root: URL, mask: String, text: String, hidden: Bool) {
        stop()
        archiveHits = []
        results = []
        scanned = 0
        running = true
        let flag = CancelFlag()
        self.flag = flag
        let query = FileSearch.spotlightQuery(mask: mask, text: text)
        Task.detached(priority: .userInitiated) { [weak self] in
            let hits = FileSearch.runSpotlight(query, root: root.path, flag: flag)
            if flag.isSet { return }
            DispatchQueue.main.async {
                if !hits.isEmpty {
                    self?.results = hits
                    self?.running = false
                } else {
                    self?.start(root: root, mask: mask, text: text, hidden: hidden)
                }
            }
        }
    }

    /// Hledání podle názvu na serveru: prochází složky do šířky (nejvýš 3000 výpisů).
    func startRemote(conn: RemoteConnection, root: String, mask: String, text: String = "", hidden: Bool) {
        stop()
        results = []
        archiveHits = []
        filesRead = 0
        readLimitReached = false
        let needle = text
        scanned = 0
        running = true
        let flag = CancelFlag()
        self.flag = flag
        var pattern = mask.trimmingCharacters(in: .whitespaces)
        if pattern.isEmpty { pattern = "*" }
        else if !pattern.contains("*") && !pattern.contains("?") { pattern = "*\(pattern)*" }
        let finalPattern = pattern

        Task.detached(priority: .userInitiated) { [weak self] in
            var queue = [root]
            var listed = 0
            var found = 0
            var filesReadCount = 0
            var limitReached = false
            while !queue.isEmpty && !flag.isSet && listed < 3000 && found < FileSearch.limit {
                let dir = queue.removeFirst()
                listed += 1
                guard let items = try? await conn.list(dir) else { continue }
                var batch: [URL] = []
                for item in items {
                    if flag.isSet { break }
                    if !hidden && item.name.hasPrefix(".") { continue }
                    guard let path = item.remotePath else { continue }
                    if item.isDirectory { queue.append(path) }
                    guard fnmatch(finalPattern, item.name, FNM_CASEFOLD) == 0 else { continue }
                    if needle.isEmpty {
                        batch.append(URL(fileURLWithPath: path))
                        found += 1
                    } else if !item.isDirectory && item.size <= 50 * 1024 * 1024 {
                        if filesReadCount >= FileSearch.maxFilesRead { limitReached = true; continue }
                        filesReadCount += 1
                        if await FileSearch.remoteFileContains(conn, item, needle) {
                            batch.append(URL(fileURLWithPath: path))
                            found += 1
                        }
                    }
                }
                let count = listed, read = filesReadCount, limit = limitReached
                DispatchQueue.main.async {
                    self?.results.append(contentsOf: batch)
                    self?.scanned = count
                    self?.filesRead = read
                    self?.readLimitReached = limit
                }
            }
            DispatchQueue.main.async { self?.running = false }
        }
    }

    func stop() {
        flag.set()
        running = false
    }

    /// Stáhne začátek vzdáleného souboru (nejvýš 1 MB) a hledá v něm text.
    static func remoteFileContains(_ conn: RemoteConnection, _ item: FileItem, _ text: String) async -> Bool {
        guard let path = item.remotePath else { return false }
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("budis-rsearch-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: tmp) }
        guard (try? await conn.download(path: path, isDirectory: false, size: item.size, to: tmp, maxBytes: 1024 * 1024)) != nil,
              let data = try? Data(contentsOf: tmp) else { return false }
        if data.prefix(4096).contains(0) { return false }
        return String(decoding: data, as: UTF8.self).range(of: text, options: .caseInsensitive) != nil
    }

    private static func walk(_ root: URL, _ mask: String, _ text: String, _ hidden: Bool, _ inArchives: Bool,
                             _ flag: CancelFlag, _ report: ([URL], [ArchiveHit], Int) -> Void) {
        let opts: FileManager.DirectoryEnumerationOptions = hidden ? [] : [.skipsHiddenFiles]
        guard let en = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey],
                                                      options: opts) else { return }
        var batch: [URL] = []
        var archiveBatch: [ArchiveHit] = []
        var scanned = 0
        var found = 0
        for case let url as URL in en {
            if flag.isSet { break }
            scanned += 1
            if fnmatch(mask, url.lastPathComponent, FNM_CASEFOLD) == 0,
               text.isEmpty || fileContains(url, text) {
                batch.append(url)
                found += 1
            }
            if inArchives, ArchiveInfo.isArchive(url.lastPathComponent) {
                for entry in ArchiveSearch.search(archive: url, mask: mask, text: text, limit: 500, flag: flag) {
                    archiveBatch.append(ArchiveHit(archive: url, entry: entry))
                    found += 1
                }
            }
            if batch.count >= 50 || !archiveBatch.isEmpty || scanned % 500 == 0 {
                report(batch, archiveBatch, scanned)
                batch = []
                archiveBatch = []
            }
            if found >= limit { break }
        }
        report(batch, archiveBatch, scanned)
    }

    static func fileContains(_ url: URL, _ text: String) -> Bool {
        guard let v = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
              v.isRegularFile == true, (v.fileSize ?? 0) <= 20 * 1024 * 1024,
              let data = try? Data(contentsOf: url, options: .mappedIfSafe) else { return false }
        if data.prefix(4096).contains(0) { return false }
        return String(decoding: data, as: UTF8.self).range(of: text, options: .caseInsensitive) != nil
    }
}

struct SearchSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @StateObject private var search = FileSearch()
    @State private var root = ""
    @State private var mask = "*"
    @State private var text = ""
    @State private var hidden = false
    @State private var useIndex = true
    @State private var inArchives = false
    @State private var conn: RemoteConnection?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Hledat soubory")).font(.headline)
            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow { Text(L("Začít v")); TextField("", text: $root) }
                GridRow { Text(L("Název")); TextField(L("např. *.jpg nebo část názvu"), text: $mask) }
                GridRow {
                    Text(L("Obsahuje"))
                    TextField(conn == nil ? L("text v souboru (volitelné)") : L("text v souboru (čte se jen první MB, nejvýše 300 souborů)"), text: $text)
                }
            }
            .textFieldStyle(.roundedBorder)
            Toggle(L("Včetně skrytých souborů"), isOn: $hidden).font(.system(size: 12))
            if conn == nil {
                Toggle(L("Hledat i uvnitř archivů (zip, tar, 7z, rar…)"), isOn: $inArchives).font(.system(size: 12))
                Toggle(L("Nejdřív zkusit rychlý index Spotlight (jen indexované složky)"), isOn: $useIndex)
                    .font(.system(size: 12)).disabled(inArchives)
            }

            HStack {
                if search.running {
                    Button(L("Zastavit")) { search.stop() }
                    ProgressView().controlSize(.small)
                } else {
                    Button(L("Hledat")) { run() }.keyboardShortcut(.defaultAction)
                }
                Text(statusText)
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
            }

            List {
                ForEach(search.results, id: \.self) { url in
                    Button {
                        model.reveal(url)
                        dismiss()
                    } label: {
                        Text(relative(url)).lineLimit(1).truncationMode(.middle)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .buttonStyle(.plain)
                }
                ForEach(search.archiveHits) { hit in
                    Button {
                        model.reveal(hit.archive)
                        dismiss()
                    } label: {
                        Text(relative(hit.archive) + " ▸ " + hit.entry).lineLimit(1).truncationMode(.middle)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .buttonStyle(.plain)
                }
            }
            .frame(height: 220)

            HStack {
                Text(L("Kliknutím se v aktivním panelu otevře složka souboru."))
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 560)
        .onAppear {
            conn = model.active.connection
            root = conn != nil ? model.active.remotePath : model.active.url.path
        }
        .onDisappear { search.stop() }
    }

    private func run() {
        if let conn {
            search.startRemote(conn: conn, root: root, mask: mask, text: text, hidden: hidden)
            return
        }
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: root, isDirectory: &isDir), isDir.boolValue else {
            Dialogs.error(L("Složka „\(root)“ neexistuje."))
            return
        }
        if useIndex && !inArchives {
            search.startWithIndex(root: URL(fileURLWithPath: root), mask: mask, text: text, hidden: hidden)
        } else {
            search.start(root: URL(fileURLWithPath: root), mask: mask, text: text, hidden: hidden, inArchives: inArchives)
        }
    }

    private var statusText: String {
        let count = search.results.count + search.archiveHits.count
        if conn == nil { return L("Nalezeno \(count), prohledáno \(search.scanned)") }
        var s = L("Nalezeno \(count), prohledaných složek \(search.scanned)")
        if !text.isEmpty { s += L(", přečteno souborů \(search.filesRead)") }
        if search.readLimitReached { s += L(" (další se kvůli limitu nečetly)") }
        return s
    }

    private func relative(_ url: URL) -> String {
        url.path.hasPrefix(root + "/") ? String(url.path.dropFirst(root.count + 1)) : url.path
    }
}
