import SwiftUI

/// Podmínky pro rozšířené označování: maska názvu, druh položky, velikost a datum změny.
struct SelectionCriteria {
    var mask = "*"
    /// 0 vše, 1 jen soubory, 2 jen složky
    var kind = 0
    var minSize: Int64?
    var maxSize: Int64?
    var modifiedFrom: Date?
    /// Poslední den včetně (do konce dne).
    var modifiedTo: Date?
    /// Jen už označené (true), jen neoznačené (false), nil = bez ohledu
    var marked: Bool?

    static func matchesMask(_ name: String, _ mask: String) -> Bool {
        let parts = mask.split(whereSeparator: { $0 == ";" || $0 == "," })
            .map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        if parts.isEmpty { return true }
        return parts.contains { fnmatch($0, name, FNM_CASEFOLD) == 0 }
    }

    func matches(name: String, isDirectory: Bool, size: Int64, modified: Date?) -> Bool {
        if kind == 1 && isDirectory { return false }
        if kind == 2 && !isDirectory { return false }
        if !SelectionCriteria.matchesMask(name, mask) { return false }
        if minSize != nil || maxSize != nil {
            if isDirectory { return false }
            if let lo = minSize, size < lo { return false }
            if let hi = maxSize, size > hi { return false }
        }
        if modifiedFrom != nil || modifiedTo != nil {
            guard let m = modified else { return false }
            let cal = Calendar.current
            if let from = modifiedFrom, m < cal.startOfDay(for: from) { return false }
            if let to = modifiedTo, let end = cal.date(byAdding: .day, value: 1, to: cal.startOfDay(for: to)), m >= end { return false }
        }
        return true
    }

    /// Velikost jako „500“, „10 KB“, „1,5 MB“, „2 GB“ (násobky 1024). Prázdný text = žádná hranice (úspěch s nil).
    static func parseSize(_ text: String) -> (ok: Bool, value: Int64?) {
        var t = text.replacingOccurrences(of: " ", with: "").uppercased()
        if t.isEmpty { return (true, nil) }
        var mult: Double = 1
        for (suffix, m) in [("GB", 1_073_741_824.0), ("MB", 1_048_576.0), ("KB", 1024.0), ("G", 1_073_741_824.0), ("M", 1_048_576.0), ("K", 1024.0), ("B", 1.0)] {
            if t.hasSuffix(suffix) { mult = m; t = String(t.dropLast(suffix.count)); break }
        }
        t = t.replacingOccurrences(of: ",", with: ".")
        guard let v = Double(t), v >= 0 else { return (false, nil) }
        return (true, Int64(v * mult))
    }

    /// Datum jako 2025-03-31, 31.3.2025 nebo 31. 3. 2025. Prázdný text = žádná hranice.
    static func parseDate(_ text: String) -> (ok: Bool, value: Date?) {
        let t = text.trimmingCharacters(in: .whitespaces)
        if t.isEmpty { return (true, nil) }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        for format in ["yyyy-MM-dd", "d.M.yyyy", "d. M. yyyy", "dd.MM.yyyy"] {
            f.dateFormat = format
            if let d = f.date(from: t) { return (true, d) }
        }
        return (false, nil)
    }
}

extension PaneState {
    /// Označí (nebo odznačí) položky splňující podmínky; vrací počet dotčených položek.
    @discardableResult
    func markWhere(_ c: SelectionCriteria, on: Bool) -> Int {
        var n = 0
        for item in items where !item.isParent
            && c.matches(name: item.name, isDirectory: item.isDirectory, size: item.size, modified: item.modified) {
            if let want = c.marked, marked.contains(item.id) != want { continue }
            if on { marked.insert(item.id) } else { marked.remove(item.id) }
            n += 1
        }
        return n
    }

    /// Označí všechny soubory se stejnou příponou, jakou má položka pod kurzorem.
    @discardableResult
    func markSameExtension() -> Int {
        guard let cur = current, !cur.isParent, !cur.isDirectory else { return 0 }
        let ext = (cur.name as NSString).pathExtension.lowercased()
        var n = 0
        for item in items where !item.isParent && !item.isDirectory
            && (item.name as NSString).pathExtension.lowercased() == ext {
            marked.insert(item.id); n += 1
        }
        return n
    }

    func invertMarks() {
        let all = Set(items.filter { !$0.isParent }.map(\.id))
        marked = all.subtracting(marked)
    }
}

struct MarkAdvancedSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var mask = "*"
    @State private var kind = 1
    @State private var minSize = ""
    @State private var maxSize = ""
    @State private var from = ""
    @State private var to = ""
    @State private var select = true
    @State private var result = ""
    @State private var failed = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Označit podle podmínek")).font(.headline)
            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow { Text(L("Maska názvu")); TextField(L("např. *.jpg;*.png"), text: $mask).textFieldStyle(.roundedBorder) }
                GridRow {
                    Text(L("Druh"))
                    Picker("", selection: $kind) {
                        Text(L("Soubory i složky")).tag(0); Text(L("Jen soubory")).tag(1); Text(L("Jen složky")).tag(2)
                    }.labelsHidden()
                }
                GridRow { Text(L("Velikost od")); TextField(L("např. 10 MB"), text: $minSize).textFieldStyle(.roundedBorder) }
                GridRow { Text(L("Velikost do")); TextField(L("např. 1 GB"), text: $maxSize).textFieldStyle(.roundedBorder) }
                GridRow { Text(L("Změněno od")); TextField(L("např. 1. 1. 2025"), text: $from).textFieldStyle(.roundedBorder) }
                GridRow { Text(L("Změněno do")); TextField(L("např. 31. 12. 2025"), text: $to).textFieldStyle(.roundedBorder) }
                GridRow {
                    Text(L("Akce"))
                    Picker("", selection: $select) { Text(L("Označit")).tag(true); Text(L("Odznačit")).tag(false) }
                        .pickerStyle(.segmented).labelsHidden()
                }
            }
            Text(L("Vyplněné podmínky platí zároveň. Složky nemají velikost, takže při zadané velikosti se vynechají."))
                .font(.system(size: 11)).foregroundStyle(.secondary)
            Text(result).foregroundStyle(failed ? Color.red : Color.primary)
            HStack {
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("Provést")) { run() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 440)
    }

    private func run() {
        let lo = SelectionCriteria.parseSize(minSize), hi = SelectionCriteria.parseSize(maxSize)
        guard lo.ok, hi.ok else { failed = true; result = L("Velikost zapište např. 500, 10 KB nebo 1,5 MB."); return }
        let df = SelectionCriteria.parseDate(from), dt = SelectionCriteria.parseDate(to)
        guard df.ok, dt.ok else { failed = true; result = L("Datum zapište např. 31. 12. 2025 nebo 2025-12-31."); return }
        var c = SelectionCriteria()
        c.mask = mask; c.kind = kind; c.minSize = lo.value; c.maxSize = hi.value
        c.modifiedFrom = df.value; c.modifiedTo = dt.value
        let n = model.active.markWhere(c, on: select)
        failed = false
        result = (select ? L("Označeno: ") : L("Odznačeno: ")) + String(n)
    }
}
