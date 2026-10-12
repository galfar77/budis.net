import SwiftUI

enum OpKind: String, Codable { case copy, move, delete, rename, newFolder }

/// Jedna položka operace: odkud (původní cesta), kam (nová cesta; u mazání prázdné) a u mazání cesta v Koši.
struct OpItem: Codable, Hashable {
    var from: String
    var to: String
    var trash: String? = nil
}

/// Záznam v historii operací.
struct OpEntry: Codable, Identifiable, Hashable {
    var id: Int
    var time: Date
    var kind: OpKind
    var summary: String
    var items: [OpItem]
    /// Mazání natrvalo se obnovit nedá.
    var permanent = false
    var reverted = false
}

/// Historie provedených operací se soubory (kopírování, přesuny, mazání…), uložená v Application Support.
@MainActor
final class OperationLog: ObservableObject {
    static let maxEntries = 500
    static let shared = OperationLog()

    @Published private(set) var entries: [OpEntry] = []     // od nejnovějšího
    private let url: URL?
    private var nextID = 1

    nonisolated static var defaultURL: URL? {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first?
            .appendingPathComponent("BudisCommander/history.json")
    }

    init(url: URL? = OperationLog.defaultURL) {
        self.url = url
        if let url, let data = try? Data(contentsOf: url), let list = try? JSONDecoder().decode([OpEntry].self, from: data) {
            entries = list.sorted { $0.id > $1.id }
            nextID = (list.map(\.id).max() ?? 0) + 1
        }
    }

    @discardableResult
    func add(_ kind: OpKind, _ summary: String, items: [OpItem], permanent: Bool = false) -> OpEntry {
        let e = OpEntry(id: nextID, time: Date(), kind: kind, summary: summary, items: items, permanent: permanent)
        nextID += 1
        entries.insert(e, at: 0)
        if entries.count > Self.maxEntries { entries.removeLast(entries.count - Self.maxEntries) }
        save()
        return e
    }

    func markReverted(_ id: Int) {
        if let i = entries.firstIndex(where: { $0.id == id }) { entries[i].reverted = true; save() }
    }

    func clear() { entries = []; save() }

    private func save() {
        guard let url else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(entries.sorted { $0.id < $1.id }) { try? data.write(to: url, options: .atomic) }
    }

    // MARK: Co jde s operací udělat

    nonisolated static func canRepeat(_ e: OpEntry) -> Bool {
        e.kind == .copy && !e.items.isEmpty && e.items.allSatisfy { $0.from.hasPrefix("/") && $0.to.hasPrefix("/") }
    }

    nonisolated static func canRevert(_ e: OpEntry) -> Bool {
        guard !e.reverted, !e.items.isEmpty else { return false }
        return e.kind == .delete ? (!e.permanent && e.items.allSatisfy { $0.trash != nil }) : true
    }

    /// Zopakuje kopírování: každou položku zkopíruje znovu na stejné místo (existující cíl přepíše).
    nonisolated static func repeatCopy(_ e: OpEntry) throws -> Int {
        let fm = FileManager.default
        var n = 0
        for item in e.items where fm.fileExists(atPath: item.from) {
            let to = URL(fileURLWithPath: item.to)
            try fm.createDirectory(at: to.deletingLastPathComponent(), withIntermediateDirectories: true)
            if fm.fileExists(atPath: item.to) { try fm.trashItem(at: to, resultingItemURL: nil) }
            try fm.copyItem(atPath: item.from, toPath: item.to)
            n += 1
        }
        return n
    }

    /// Vrátí operaci: kopii a novou složku přesune do Koše, přesun a přejmenování vrátí zpět, smazané obnoví z Koše.
    nonisolated static func revert(_ e: OpEntry) -> (done: Int, errors: [String]) {
        let fm = FileManager.default
        var done = 0
        var errors: [String] = []
        for item in e.items {
            let name = (item.from.isEmpty ? item.to : item.from) as NSString
            do {
                switch e.kind {
                case .copy, .newFolder:
                    if fm.fileExists(atPath: item.to) { try fm.trashItem(at: URL(fileURLWithPath: item.to), resultingItemURL: nil); done += 1 }
                case .move, .rename:
                    if fm.fileExists(atPath: item.from) { throw RemoteError(message: "původní místo je obsazené") }
                    try fm.createDirectory(atPath: (item.from as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
                    try fm.moveItem(atPath: item.to, toPath: item.from)
                    done += 1
                case .delete:
                    guard let trash = item.trash, fm.fileExists(atPath: trash) else { throw RemoteError(message: "v Koši už není") }
                    var target = item.from
                    if fm.fileExists(atPath: target) {
                        let dir = (target as NSString).deletingLastPathComponent
                        let stem = ((target as NSString).lastPathComponent as NSString).deletingPathExtension
                        let ext = (target as NSString).pathExtension
                        var n = 2
                        repeat {
                            target = (dir as NSString).appendingPathComponent("\(stem) (\(n))" + (ext.isEmpty ? "" : ".\(ext)"))
                            n += 1
                        } while fm.fileExists(atPath: target)
                    }
                    try fm.createDirectory(atPath: (target as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
                    try fm.moveItem(atPath: trash, toPath: target)
                    done += 1
                }
            } catch {
                errors.append("\(name.lastPathComponent): \(error.localizedDescription)")
            }
        }
        return (done, errors)
    }
}

/// Historie operací; se zapnutým filtrem „Smazané“ slouží i k obnovení souborů z Koše.
struct HistorySheet: View {
    @ObservedObject var model: AppModel
    @ObservedObject private var log = OperationLog.shared
    @Environment(\.dismiss) private var dismiss
    @State private var onlyDeleted: Bool
    @State private var selection: Int?
    @State private var status = ""

    init(model: AppModel, onlyDeleted: Bool) {
        self.model = model
        _onlyDeleted = State(initialValue: onlyDeleted)
    }

    private var shown: [OpEntry] { onlyDeleted ? log.entries.filter { $0.kind == .delete } : log.entries }
    private var selected: OpEntry? { shown.first { $0.id == selection } }

    static func icon(_ e: OpEntry) -> String {
        if e.reverted { return "↩" }
        switch e.kind {
        case .copy: return "⧉"
        case .move: return "➜"
        case .delete: return "🗑"
        case .rename: return "✎"
        case .newFolder: return "＋"
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(onlyDeleted ? L("Obnovit z Koše") : L("Historie operací")).font(.headline)
            Picker("", selection: $onlyDeleted) {
                Text(L("Všechny operace")).tag(false)
                Text(L("Jen smazané")).tag(true)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 260)
            List(shown, selection: $selection) { e in
                HStack {
                    Text(HistorySheet.icon(e))
                    Text(e.time.formatted(date: .numeric, time: .shortened)).foregroundStyle(.secondary)
                    Text(L(e.summary) + (e.reverted ? "  (" + L("vráceno") + ")" : "")).lineLimit(1).truncationMode(.middle)
                    Spacer(minLength: 0)
                }
                .font(.system(size: 12))
                .tag(e.id)
            }
            .frame(height: 300)
            Text(status).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(4)
            HStack {
                Button(L("Zopakovat")) { repeatSelected() }.disabled(selected.map { !OperationLog.canRepeat($0) } ?? true)
                Button(onlyDeleted ? L("Obnovit z Koše") : L("Vrátit zpět")) { revertSelected() }
                    .disabled(selected.map { !OperationLog.canRevert($0) } ?? true)
                Button(L("Vyčistit historii")) { log.clear() }
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
            Text(L("Zopakovat jde kopírování. Vrátit jde kopírování (kopie jde do Koše), přesun, přejmenování, nová složka a smazání do Koše. Z Koše jdou obnovit jen soubory smazané touto aplikací."))
                .font(.system(size: 11)).foregroundStyle(.secondary)
        }
        .padding(16)
        .frame(width: 640)
    }

    private func repeatSelected() {
        guard let e = selected else { return }
        do {
            let n = try OperationLog.repeatCopy(e)
            status = L("Zkopírováno znovu: \(n)")
            reloadPanes()
        } catch { status = error.localizedDescription }
    }

    private func revertSelected() {
        guard let e = selected else { return }
        let (done, errors) = OperationLog.revert(e)
        if errors.isEmpty { log.markReverted(e.id) }
        status = L("Vráceno položek: \(done)") + (errors.isEmpty ? "" : "\n" + errors.joined(separator: "\n"))
        reloadPanes()
    }

    private func reloadPanes() {
        model.active.reload()
        model.other.reload()
    }
}
