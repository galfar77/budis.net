import Foundation
import AppKit

enum SortKey { case name, size, date }

@MainActor
final class PaneState: ObservableObject {
    @Published private(set) var url: URL
    @Published private(set) var items: [FileItem] = []
    @Published var cursor = 0
    @Published var marked: Set<String> = []
    @Published var sortKey: SortKey = .name
    @Published var ascending = true
    @Published var showHidden = false

    private var searchBuffer = ""
    private var searchTime = Date.distantPast

    init(url: URL) {
        self.url = url
        _ = load(url)
    }

    // MARK: Načítání

    @discardableResult
    private func load(_ dir: URL, select id: String? = nil) -> Bool {
        let fm = FileManager.default
        let urls: [URL]
        do {
            urls = try fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil,
                                              options: showHidden ? [] : [.skipsHiddenFiles])
        } catch {
            Dialogs.error("Adresář nelze otevřít:\n\(dir.path)\n\n\(error.localizedDescription)")
            return false
        }
        let previousID = id ?? (items.indices.contains(cursor) ? items[cursor].id : nil)
        var list = sorted(urls.compactMap(FileItem.load))
        if dir.path != "/" { list.insert(.parent(of: dir), at: 0) }
        url = dir
        items = list
        marked = marked.filter { m in list.contains { $0.id == m } }
        if let previousID, let idx = list.firstIndex(where: { $0.id == previousID }) {
            cursor = idx
        } else {
            cursor = min(cursor, max(list.count - 1, 0))
        }
        return true
    }

    func reload() { load(url) }

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

    func navigate(to dir: URL) {
        marked = []
        if load(dir, select: "") { cursor = 0 }
    }

    func goUp() {
        guard url.path != "/" else { return }
        let old = url.path
        marked = []
        load(url.deletingLastPathComponent(), select: old)
    }

    func enter() {
        guard let item = current else { return }
        if item.isParent { goUp() }
        else if item.isDirectory { navigate(to: item.url) }
        else { NSWorkspace.shared.open(item.url) }
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
        if markedItems.isEmpty {
            return "\(files.count) položek, \(fmt(files.reduce(0) { $0 + $1.size }))"
        }
        return "Označeno \(markedItems.count) z \(files.count), \(fmt(markedItems.reduce(0) { $0 + $1.size }))"
    }
}
