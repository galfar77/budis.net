import SwiftUI

struct RenameOptions {
    var mask = "[N].[E]"
    var search = ""
    var replace = ""
    var regex = false
    var start = 1
    var digits = 1
    /// 0 = beze změny, 1 = malá písmena, 2 = velká písmena
    var caseMode = 0
    /// Odstranit diakritiku (á → a).
    var removeDiacritics = false
    /// Mezery nahradit podtržítkem.
    var spacesToUnderscore = false
}

/// Položka k přejmenování včetně údajů pro značky [Y], [M], [D], [h], [m], [s] (datum změny) a [P] (název nadřazené složky).
struct RenameInput {
    var name: String
    var isDirectory: Bool
    var modified: Date? = nil
    var parent = ""
}

enum BatchRename {
    static func newNames(for items: [(name: String, isDirectory: Bool)], _ o: RenameOptions) -> [String] {
        newNames(for: items.map { RenameInput(name: $0.name, isDirectory: $0.isDirectory) }, o)
    }

    /// Odstraní diakritiku (včetně písmen jako ł a đ, která se rozkladem neodstraní).
    static func stripDiacritics(_ s: String) -> String {
        var out = s.folding(options: .diacriticInsensitive, locale: nil)
        for (from, to) in [("ł", "l"), ("Ł", "L"), ("đ", "d"), ("Đ", "D"), ("ø", "o"), ("Ø", "O"), ("ß", "s")] {
            out = out.replacingOccurrences(of: from, with: to)
        }
        return out
    }

    private static func format(_ date: Date?, _ pattern: String) -> String {
        guard let date else { return "" }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = pattern
        return f.string(from: date)
    }

    static func newNames(for items: [RenameInput], _ o: RenameOptions) -> [String] {
        let regex: NSRegularExpression? = (o.regex && !o.search.isEmpty)
            ? try? NSRegularExpression(pattern: o.search) : nil
        let range = try? NSRegularExpression(pattern: #"\[N(\d+)-(\d+)\]"#)

        return items.enumerated().map { index, item in
            let ns = item.name as NSString
            let ext = item.isDirectory ? "" : ns.pathExtension
            let base = item.isDirectory ? item.name : ns.deletingPathExtension
            let counter = String(format: "%0\(max(o.digits, 1))d", o.start + index)

            var out = o.mask
            if ext.isEmpty {
                out = out.replacingOccurrences(of: ".[E]", with: "").replacingOccurrences(of: "[E]", with: "")
            } else {
                out = out.replacingOccurrences(of: "[E]", with: ext)
            }
            if let range {
                let chars = Array(base)
                for m in range.matches(in: out, range: NSRange(location: 0, length: (out as NSString).length)).reversed() {
                    let from = Int((out as NSString).substring(with: m.range(at: 1))) ?? 1
                    let to = Int((out as NSString).substring(with: m.range(at: 2))) ?? 0
                    var piece = ""
                    if max(from, 1) <= chars.count && to >= max(from, 1) {
                        piece = String(chars[(max(from, 1) - 1)..<min(to, chars.count)])
                    }
                    out = (out as NSString).replacingCharacters(in: m.range, with: piece)
                }
            }
            out = out.replacingOccurrences(of: "[C]", with: counter)
            out = out.replacingOccurrences(of: "[N]", with: base)
            for (token, pattern) in [("[Y]", "yyyy"), ("[M]", "MM"), ("[D]", "dd"), ("[h]", "HH"), ("[m]", "mm"), ("[s]", "ss")] {
                out = out.replacingOccurrences(of: token, with: format(item.modified, pattern))
            }
            out = out.replacingOccurrences(of: "[P]", with: item.parent)

            if !o.search.isEmpty {
                if let regex {
                    out = regex.stringByReplacingMatches(in: out, range: NSRange(location: 0, length: (out as NSString).length),
                                                         withTemplate: o.replace)
                } else if !o.regex {
                    out = out.replacingOccurrences(of: o.search, with: o.replace)
                }
            }
            switch o.caseMode {
            case 1: out = out.lowercased()
            case 2: out = out.uppercased()
            default: break
            }
            if o.removeDiacritics { out = stripDiacritics(out) }
            if o.spacesToUnderscore { out = out.replacingOccurrences(of: " ", with: "_") }
            return out.replacingOccurrences(of: "/", with: "-").trimmingCharacters(in: .whitespaces)
        }
    }
}

struct BatchRenameSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var opts = RenameOptions()
    @State private var busy = false

    private var items: [FileItem] { model.batchItems }
    private var names: [String] { BatchRename.newNames(for: items.map {
        RenameInput(name: $0.name, isDirectory: $0.isDirectory, modified: $0.modified,
                    parent: $0.url.deletingLastPathComponent().lastPathComponent)
    }, opts) }

    /// Indexy položek, jejichž nový název je prázdný, duplicitní nebo koliduje s jiným souborem.
    private var problems: Set<Int> {
        let new = names
        let oldNames = Set(items.map(\.name))
        let others = Set(model.active.allItems.filter { !$0.isParent && !oldNames.contains($0.name) }.map(\.name))
        var counts: [String: Int] = [:]
        for n in new { counts[n, default: 0] += 1 }
        return Set(new.indices.filter { new[$0].isEmpty || counts[new[$0]]! > 1 || others.contains(new[$0]) })
    }

    var body: some View {
        let new = names
        let bad = problems
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Hromadné přejmenování (\(items.count) položek)")).font(.headline)

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow {
                    Text(L("Maska"))
                    TextField("[N].[E]", text: $opts.mask)
                }
                GridRow {
                    Text(L("Hledat"))
                    TextField("", text: $opts.search)
                }
                GridRow {
                    Text(L("Nahradit"))
                    TextField("", text: $opts.replace)
                }
                GridRow {
                    Text("")
                    Toggle(L("Regulární výraz (v náhradě lze použít $1, $2…)"), isOn: $opts.regex)
                }
                GridRow {
                    Text("")
                    Toggle(L("Odstranit diakritiku (á → a)"), isOn: $opts.removeDiacritics)
                }
                GridRow {
                    Text("")
                    Toggle(L("Mezery nahradit podtržítkem"), isOn: $opts.spacesToUnderscore)
                }
                GridRow {
                    Text(L("Čítač"))
                    HStack {
                        Stepper("od \(opts.start)", value: $opts.start, in: 0...99999)
                        Stepper(L("číslic \(opts.digits)"), value: $opts.digits, in: 1...6)
                    }
                }
                GridRow {
                    Text(L("Písmena"))
                    Picker("", selection: $opts.caseMode) {
                        Text(L("Beze změny")).tag(0)
                        Text(L("malá")).tag(1)
                        Text(L("VELKÁ")).tag(2)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }
            }
            .textFieldStyle(.roundedBorder)

            Text(L("Maska: [N] = název bez přípony, [N2-5] = znaky 2 až 5 názvu, [E] = přípona, [C] = čítač, [Y] [M] [D] [h] [m] [s] = rok, měsíc, den, hodina, minuta, sekunda změny, [P] = název nadřazené složky."))
                .font(.system(size: 11)).foregroundStyle(.secondary)

            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(Array(items.enumerated()), id: \.offset) { i, item in
                        HStack(spacing: 6) {
                            Text(item.name).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                            Image(systemName: "arrow.right").font(.system(size: 9))
                            Text(new[i]).lineLimit(1)
                                .foregroundStyle(bad.contains(i) ? Color.red : (new[i] == item.name ? Color.secondary : Color.primary))
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .font(.system(size: 12))
                    }
                }
            }
            .frame(height: 170)
            .padding(6)
            .background(Color.secondary.opacity(0.1))
            .clipShape(RoundedRectangle(cornerRadius: 6))

            if !bad.isEmpty {
                Text(L("Červeně označené názvy jsou prázdné, duplicitní nebo už existují."))
                    .font(.system(size: 11)).foregroundStyle(.red)
            }

            HStack {
                if busy { ProgressView().controlSize(.small) }
                Spacer()
                Button(L("Zrušit")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("Přejmenovat")) {
                    busy = true
                    Task {
                        await model.applyRename(items, new)
                        busy = false
                        dismiss()
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!bad.isEmpty || busy || new == items.map(\.name))
            }
        }
        .padding(16)
        .frame(width: 520)
    }
}
