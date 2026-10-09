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
}

enum BatchRename {
    static func newNames(for items: [(name: String, isDirectory: Bool)], _ o: RenameOptions) -> [String] {
        let regex: NSRegularExpression? = (o.regex && !o.search.isEmpty)
            ? try? NSRegularExpression(pattern: o.search) : nil

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
            out = out.replacingOccurrences(of: "[C]", with: counter)
            out = out.replacingOccurrences(of: "[N]", with: base)

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
    private var names: [String] { BatchRename.newNames(for: items.map { ($0.name, $0.isDirectory) }, opts) }

    /// Indexy položek, jejichž nový název je prázdný, duplicitní nebo koliduje s jiným souborem.
    private var problems: Set<Int> {
        let new = names
        let oldNames = Set(items.map(\.name))
        let others = Set(model.active.items.filter { !$0.isParent && !oldNames.contains($0.name) }.map(\.name))
        var counts: [String: Int] = [:]
        for n in new { counts[n, default: 0] += 1 }
        return Set(new.indices.filter { new[$0].isEmpty || counts[new[$0]]! > 1 || others.contains(new[$0]) })
    }

    var body: some View {
        let new = names
        let bad = problems
        VStack(alignment: .leading, spacing: 10) {
            Text("Hromadné přejmenování (\(items.count) položek)").font(.headline)

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow {
                    Text("Maska")
                    TextField("[N].[E]", text: $opts.mask)
                }
                GridRow {
                    Text("Hledat")
                    TextField("", text: $opts.search)
                }
                GridRow {
                    Text("Nahradit")
                    TextField("", text: $opts.replace)
                }
                GridRow {
                    Text("")
                    Toggle("Regulární výraz (v náhradě lze použít $1, $2…)", isOn: $opts.regex)
                }
                GridRow {
                    Text("Čítač")
                    HStack {
                        Stepper("od \(opts.start)", value: $opts.start, in: 0...99999)
                        Stepper("číslic \(opts.digits)", value: $opts.digits, in: 1...6)
                    }
                }
                GridRow {
                    Text("Písmena")
                    Picker("", selection: $opts.caseMode) {
                        Text("Beze změny").tag(0)
                        Text("malá").tag(1)
                        Text("VELKÁ").tag(2)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }
            }
            .textFieldStyle(.roundedBorder)

            Text("Maska: [N] = název bez přípony, [E] = přípona, [C] = čítač.")
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
                Text("Červeně označené názvy jsou prázdné, duplicitní nebo už existují.")
                    .font(.system(size: 11)).foregroundStyle(.red)
            }

            HStack {
                if busy { ProgressView().controlSize(.small) }
                Spacer()
                Button("Zrušit") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Přejmenovat") {
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
