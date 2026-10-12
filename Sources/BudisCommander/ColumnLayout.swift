import Foundation

/// Pořadí a šířky sloupců za názvem souboru (Název vyplní zbytek místa a zůstává první).
struct ColumnLayout: Codable, Equatable {
    /// "extra" je skupina sloupců Práva, Vlastník a Rozměry/délka.
    static let all = ["ext", "extra", "size", "date"]
    static let minWidth = 30.0, maxWidth = 600.0

    var order: [String] = ColumnLayout.all
    var widths: [String: Double] = [:]

    static func defaultWidth(_ column: String) -> Double {
        switch column {
        case "ext": return 64
        case "size": return 80
        case "date": return 130
        default: return 0
        }
    }

    var isDefault: Bool { widths.isEmpty && normalized() == ColumnLayout.all }

    /// Úplné pořadí všech sloupců (neznámé se zahodí, chybějící se přidají na konec).
    func normalized() -> [String] {
        var list: [String] = []
        for c in order where ColumnLayout.all.contains(c) && !list.contains(c) { list.append(c) }
        for c in ColumnLayout.all where !list.contains(c) { list.append(c) }
        return list
    }

    func visible(showExt: Bool, showExtra: Bool) -> [String] {
        normalized().filter { ($0 != "ext" || showExt) && ($0 != "extra" || showExtra) }
    }

    func width(_ column: String) -> Double {
        if let w = widths[column] { return min(max(w, ColumnLayout.minWidth), ColumnLayout.maxWidth) }
        return ColumnLayout.defaultWidth(column)
    }

    mutating func resize(_ column: String, to width: Double) {
        guard column != "extra" else { return }
        widths[column] = min(max(width.rounded(), ColumnLayout.minWidth), ColumnLayout.maxWidth)
    }

    /// Posune sloupec o jedno místo (-1 doleva, +1 doprava) mezi zobrazenými sloupci.
    @discardableResult
    mutating func move(_ column: String, by delta: Int, showExt: Bool, showExtra: Bool) -> Bool {
        var full = normalized()
        let shown = visible(showExt: showExt, showExtra: showExtra)
        guard let vi = shown.firstIndex(of: column), shown.indices.contains(vi + delta),
              let a = full.firstIndex(of: column), let b = full.firstIndex(of: shown[vi + delta]) else { return false }
        full.swapAt(a, b)
        order = full
        return true
    }
}
