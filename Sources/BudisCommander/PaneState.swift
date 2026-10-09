import Foundation
import AppKit

enum SortKey { case name, size, date }

/// Archiv otevřený jako složka: rozbalený do dočasného adresáře, jen pro čtení.
struct ArchiveInfo {
    let name: String
    let root: URL       // dočasná složka s obsahem
    let origin: URL     // složka, ve které archiv leží
    let itemID: String  // id položky archivu v původní složce

    static let extensions: Set<String> = ["zip", "tar", "tgz", "tbz", "tbz2", "txz", "7z", "rar", "jar"]
    static let compound = [".tar.gz", ".tar.bz2", ".tar.xz"]

    static func isArchive(_ name: String) -> Bool {
        let lower = name.lowercased()
        return extensions.contains((lower as NSString).pathExtension) || compound.contains { lower.hasSuffix($0) }
    }
}

@MainActor
final class PaneState: ObservableObject {
    /// Poslední lokální adresář (zůstává nastavený i při připojení k serveru).
    @Published private(set) var url: URL
    @Published private(set) var items: [FileItem] = []
    @Published var cursor = 0
    @Published var marked: Set<String> = []
    @Published var sortKey: SortKey = .name
    @Published var ascending = true
    @Published var showHidden = false
    @Published private(set) var connection: RemoteConnection?
    @Published private(set) var remotePath = "/"
    @Published private(set) var isLoading = false
    @Published private(set) var archive: ArchiveInfo?
    /// Ploché zobrazení všech souborů ze všech podsložek (Branch view).
    @Published private(set) var branch = false
    /// Spočítané velikosti složek (id položky → bajty).
    @Published private(set) var dirSizes: [String: Int64] = [:]
    /// Text, kterým je seznam zúžený (prázdný = bez filtru).
    @Published private(set) var filter = ""
    /// Všechny položky adresáře bez filtru.
    private(set) var allItems: [FileItem] = []

    static let archiveBase = FileManager.default.temporaryDirectory.appendingPathComponent("BudisArchives")
    private var back: [URL] = []
    private var forward: [URL] = []
    private var historyMove = false

    /// Volá se po každé úspěšné změně adresáře (AppModel podle toho ukládá stav).
    static var onLocationChange: (() -> Void)?

    private var searchBuffer = ""
    private var searchTime = Date.distantPast

    init(url: URL) {
        self.url = url
        _ = load(url)
    }

    var isArchive: Bool { archive != nil }
    var canGoBack: Bool { !back.isEmpty }
    var canGoForward: Bool { !forward.isEmpty }

    /// Adresář, který se ukládá do stavu (u archivu složka s archivem).
    var persistentURL: URL { archive?.origin ?? url }

    private static func isTemp(_ url: URL) -> Bool { url.path.hasPrefix(archiveBase.path) }

    var tabTitle: String {
        if let a = archive { return a.name }
        if let c = connection { return c.host }
        return url.path == "/" ? "/" : url.lastPathComponent
    }

    var title: String {
        if let a = archive { return "Archiv \(a.name)" + String(url.path.dropFirst(a.root.path.count)) + " (jen pro čtení)" }
        if let c = connection { return c.displayName + remotePath }
        if branch { return url.path + "  (všechny podsložky)" }
        return url.path
    }

    // MARK: Načítání

    private func visible(_ list: [FileItem]) -> [FileItem] {
        guard !filter.isEmpty else { return list }
        return list.filter {
            $0.isParent || $0.name.range(of: filter, options: [.caseInsensitive, .diacriticInsensitive]) != nil
        }
    }

    private func apply(_ list: [FileItem], previousID: String?) {
        allItems = list
        items = visible(list)
        marked = marked.filter { m in list.contains { $0.id == m } }
        if previousID == "" {
            cursor = 0
        } else if let previousID, let idx = items.firstIndex(where: { $0.id == previousID }) {
            cursor = idx
        } else {
            cursor = min(cursor, max(items.count - 1, 0))
        }
    }

    // MARK: Filtr (Alt + text)

    private func refilter() {
        items = visible(allItems)
        cursor = items.firstIndex { !$0.isParent } ?? 0
    }

    func appendFilter(_ text: String) {
        filter += text
        refilter()
    }

    func deleteFilterChar() {
        guard !filter.isEmpty else { return }
        filter.removeLast()
        refilter()
    }

    func clearFilter() {
        guard !filter.isEmpty else { return }
        filter = ""
        refilter()
    }

    @discardableResult
    private func load(_ dir: URL, select id: String? = nil) -> Bool {
        let urls: [URL]
        do {
            urls = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil,
                                                               options: showHidden ? [] : [.skipsHiddenFiles])
        } catch {
            Dialogs.error("Adresář nelze otevřít:\n\(dir.path)\n\n\(error.localizedDescription)")
            return false
        }
        let previousID = id ?? current?.id
        if dir != url { branch = false; dirSizes = [:] }
        var list = branch ? sorted(branchItems(dir)) : sorted(urls.compactMap(FileItem.load))
        if dir.path != "/" { list.insert(.parent(of: dir), at: 0) }
        if dir != url { filter = "" }
        if dir != url && !historyMove && !Self.isTemp(url) && !Self.isTemp(dir) {
            back.append(url)
            forward.removeAll()
            if back.count > 100 { back.removeFirst() }
        }
        if !Self.isTemp(dir) { FavoritesStore.shared.visited(dir) }
        url = dir
        apply(list, previousID: previousID)
        Self.onLocationChange?()
        return true
    }

    /// Všechny soubory ze všech podsložek (nejvýš 20 000), s relativní cestou k zobrazení.
    private func branchItems(_ dir: URL) -> [FileItem] {
        let keys: [URLResourceKey] = [.isDirectoryKey, .fileSizeKey, .contentModificationDateKey]
        let options: FileManager.DirectoryEnumerationOptions = showHidden ? [] : [.skipsHiddenFiles]
        guard let en = FileManager.default.enumerator(at: dir, includingPropertiesForKeys: keys, options: options) else { return [] }
        var out: [FileItem] = []
        let prefix = dir.path.hasSuffix("/") ? dir.path : dir.path + "/"
        for case let u as URL in en {
            guard let v = try? u.resourceValues(forKeys: Set(keys)), v.isDirectory != true else { continue }
            let rel = u.path.hasPrefix(prefix) ? String(u.path.dropFirst(prefix.count)) : u.lastPathComponent
            out.append(FileItem(url: u, name: u.lastPathComponent, isDirectory: false, size: Int64(v.fileSize ?? 0),
                                modified: v.contentModificationDate, isParent: false, subpath: rel))
            if out.count >= 20_000 { break }
        }
        return out
    }

    func toggleBranch() {
        guard connection == nil else { return }
        branch.toggle()
        marked = []
        load(url, select: "")
    }

    /// Spočítá velikosti zadaných složek na pozadí a ukáže je ve sloupci Velikost.
    func computeDirSizes(for dirs: [FileItem]) async {
        guard connection == nil else { return }
        isLoading = true
        defer { isLoading = false }
        for d in dirs where d.isDirectory && !d.isParent {
            let url = d.url
            let size = await Task.detached { LocalFS.totalSize(url) }.value
            dirSizes[d.id] = size
        }
    }

    /// Načte adresář na serveru. Při úspěchu přepne panel do vzdáleného režimu.
    @discardableResult
    func loadRemote(_ path: String, select id: String? = nil, using conn: RemoteConnection? = nil) async -> Bool {
        guard let conn = conn ?? connection else { return false }
        let previousID = id ?? current?.id
        isLoading = true
        defer { isLoading = false }
        do {
            if path != remotePath || conn !== connection { filter = "" }
            var list = try await conn.list(path)
            if !showHidden { list = list.filter { !$0.name.hasPrefix(".") } }
            list = sorted(list)
            if path != "/" { list.insert(.remoteParent(of: path), at: 0) }
            connection = conn
            remotePath = path
            apply(list, previousID: previousID)
            return true
        } catch {
            Dialogs.error("Server: \(conn.displayName)\n\n\(error.localizedDescription)")
            return false
        }
    }

    func reload() {
        if connection != nil {
            let path = remotePath
            Task { await loadRemote(path) }
        } else {
            load(url)
        }
    }

    func disconnect() {
        filter = ""
        connection = nil
        remotePath = "/"
        marked = []
        load(url, select: "")
    }

    private func sorted(_ list: [FileItem]) -> [FileItem] {
        list.sorted { a, b in
            if a.isDirectory != b.isDirectory { return a.isDirectory }
            let byName = a.name.localizedStandardCompare(b.name)
            var r = byName
            switch sortKey {
            case .name: break
            case .size: r = a.size == b.size ? .orderedSame : (a.size < b.size ? .orderedAscending : .orderedDescending)
            case .date:
                let da = a.modified ?? .distantPast, db = b.modified ?? .distantPast
                r = da == db ? .orderedSame : (da < db ? .orderedAscending : .orderedDescending)
            }
            if r == .orderedSame { return byName == .orderedAscending }
            return ascending ? r == .orderedAscending : r == .orderedDescending
        }
    }

    func setSort(_ key: SortKey) {
        if sortKey == key { ascending.toggle() } else { sortKey = key; ascending = true }
        reload()
    }

    func toggleHidden() {
        showHidden.toggle()
        reload()
    }

    // MARK: Navigace

    var current: FileItem? { items.indices.contains(cursor) ? items[cursor] : nil }

    /// Přejde do lokálního adresáře (a případně se odpojí od serveru).
    func navigate(to dir: URL) {
        marked = []
        let leavingArchive = archive.map { !dir.path.hasPrefix($0.root.path) } ?? false
        if load(dir, select: "") {
            connection = nil
            remotePath = "/"
            if leavingArchive { discardArchive() }
        }
    }

    func goBack() {
        guard let prev = back.popLast() else { return }
        let current = url
        historyMove = true
        navigate(to: prev)
        historyMove = false
        if url == prev { forward.append(current) } else { back.append(prev) }
    }

    func goForward() {
        guard let next = forward.popLast() else { return }
        let current = url
        historyMove = true
        navigate(to: next)
        historyMove = false
        if url == next { back.append(current) } else { forward.append(next) }
    }

    // MARK: Archivy jako složky

    private func discardArchive() {
        guard let a = archive else { return }
        archive = nil
        try? FileManager.default.removeItem(at: a.root)
    }

    private func leaveArchive() {
        guard let a = archive else { return }
        archive = nil
        marked = []
        try? FileManager.default.removeItem(at: a.root)
        load(a.origin, select: a.itemID)
    }

    func openArchive(_ item: FileItem) async {
        let dest = Self.archiveBase.appendingPathComponent(UUID().uuidString)
        isLoading = true
        defer { isLoading = false }
        do {
            try FileManager.default.createDirectory(at: dest, withIntermediateDirectories: true)
            try await Shell.run("/usr/bin/tar", ["-xf", item.url.path, "-C", dest.path])
            let origin = url
            marked = []
            if load(dest, select: "") {
                archive = ArchiveInfo(name: item.name, root: dest, origin: origin, itemID: item.id)
            } else {
                try? FileManager.default.removeItem(at: dest)
            }
        } catch {
            try? FileManager.default.removeItem(at: dest)
            Dialogs.error("Archiv „\(item.name)“ se nepodařilo otevřít:\n\(error.localizedDescription)")
        }
    }

    func goUp() {
        marked = []
        if let a = archive, url == a.root {
            leaveArchive()
        } else if connection != nil {
            guard remotePath != "/" else { return }
            let old = remotePath
            Task { await loadRemote(RemotePath.parent(old), select: old) }
        } else {
            guard url.path != "/" else { return }
            load(url.deletingLastPathComponent(), select: url.path)
        }
    }

    func enter() {
        guard let item = current else { return }
        if item.isParent {
            goUp()
        } else if let conn = connection, let path = item.remotePath {
            if item.isDirectory {
                marked = []
                Task { await loadRemote(path, select: "") }
            } else {
                Task { await openRemoteFile(conn, item) }
            }
        } else if item.isDirectory {
            navigate(to: item.url)
        } else if archive == nil && ArchiveInfo.isArchive(item.name) {
            Task { await openArchive(item) }
        } else {
            NSWorkspace.shared.open(item.url)
        }
    }

    private func openRemoteFile(_ conn: RemoteConnection, _ item: FileItem) async {
        guard let path = item.remotePath else { return }
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let file = tmp.appendingPathComponent(item.name)
        isLoading = true
        defer { isLoading = false }
        do {
            try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
            try await conn.download(path: path, isDirectory: false, to: file)
            NSWorkspace.shared.open(file)
        } catch {
            Dialogs.error(error.localizedDescription)
        }
    }

    func move(by delta: Int) {
        guard !items.isEmpty else { return }
        cursor = min(max(cursor + delta, 0), items.count - 1)
    }

    func moveTo(_ index: Int) { move(by: index - cursor) }

    // MARK: Označování

    func toggleMark(advance: Bool = true) {
        if let item = current, !item.isParent {
            if marked.contains(item.id) { marked.remove(item.id) } else { marked.insert(item.id) }
        }
        if advance { move(by: 1) }
    }

    func markAll() { marked = Set(items.filter { !$0.isParent }.map(\.id)) }

    func mark(matching pattern: String, on: Bool) {
        for item in items where !item.isParent && fnmatch(pattern, item.name, FNM_CASEFOLD) == 0 {
            if on { marked.insert(item.id) } else { marked.remove(item.id) }
        }
    }

    /// Označené položky, případně položka pod kurzorem.
    var targets: [FileItem] {
        let m = items.filter { marked.contains($0.id) }
        if !m.isEmpty { return m }
        if let c = current, !c.isParent { return [c] }
        return []
    }

    // MARK: Rychlé hledání

    func quickSearch(_ chars: String) {
        let now = Date()
        if now.timeIntervalSince(searchTime) > 1 { searchBuffer = "" }
        searchTime = now
        searchBuffer += chars.lowercased()
        if let idx = items.firstIndex(where: { !$0.isParent && $0.name.lowercased().hasPrefix(searchBuffer) }) {
            cursor = idx
        }
    }

    // MARK: Statistika

    var summary: String {
        let files = items.filter { !$0.isParent }
        let markedItems = files.filter { marked.contains($0.id) }
        let fmt = { (n: Int64) in ByteCountFormatter.string(fromByteCount: n, countStyle: .file) }
        if !filter.isEmpty {
            return "Filtr „\(filter)“: \(files.count) z \(allItems.filter { !$0.isParent }.count) položek (Esc zruší)"
        }
        if markedItems.isEmpty {
            return "\(files.count) položek, \(fmt(files.reduce(0) { $0 + $1.size }))"
        }
        return "Označeno \(markedItems.count) z \(files.count), \(fmt(markedItems.reduce(0) { $0 + $1.size }))"
    }
}
