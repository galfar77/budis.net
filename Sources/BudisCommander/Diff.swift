import SwiftUI

struct DiffRow: Identifiable {
    enum Kind { case same, removed, added }
    let id: Int
    let kind: Kind
    let left: String?
    let right: String?
    let leftNo: Int?
    let rightNo: Int?
}

enum DiffResult {
    case rows([DiffRow], changes: Int)
    case binary(identical: Bool)
    case tooLarge(identical: Bool)
}

enum DiffEngine {
    static let maxBytes = 3 * 1024 * 1024
    static let maxLines = 8000

    private static func lines(_ data: Data) -> [String] {
        String(decoding: data, as: UTF8.self).split(omittingEmptySubsequences: false, whereSeparator: \.isNewline).map(String.init)
    }

    /// Porovná dva soubory po řádcích; binární a příliš velké jen celkově.
    static func compute(_ a: URL, _ b: URL) -> DiffResult {
        let size = { (u: URL) in LocalFS.totalSize(u) }
        guard size(a) <= Int64(maxBytes), size(b) <= Int64(maxBytes) else {
            return .tooLarge(identical: LocalFS.sameContent(a, b))
        }
        guard let da = try? Data(contentsOf: a), let db = try? Data(contentsOf: b) else { return .binary(identical: false) }
        if da.prefix(4096).contains(0) || db.prefix(4096).contains(0) { return .binary(identical: da == db) }

        let old = lines(da), new = lines(db)
        guard old.count <= maxLines, new.count <= maxLines else { return .tooLarge(identical: da == db) }

        let diff = new.difference(from: old)
        var removed = Set<Int>(), inserted = Set<Int>()
        for change in diff {
            switch change {
            case .remove(let offset, _, _): removed.insert(offset)
            case .insert(let offset, _, _): inserted.insert(offset)
            }
        }
        var rows: [DiffRow] = []
        var i = 0, j = 0
        var changes = 0
        func add(_ kind: DiffRow.Kind, _ l: String?, _ r: String?, _ ln: Int?, _ rn: Int?) {
            rows.append(DiffRow(id: rows.count, kind: kind, left: l, right: r, leftNo: ln, rightNo: rn))
        }
        while i < old.count || j < new.count {
            if i < old.count && (removed.contains(i) || j >= new.count) {
                add(.removed, old[i], nil, i + 1, nil); i += 1; changes += 1
            } else if j < new.count && (inserted.contains(j) || i >= old.count) {
                add(.added, nil, new[j], nil, j + 1); j += 1; changes += 1
            } else {
                add(.same, old[i], new[j], i + 1, j + 1); i += 1; j += 1
            }
        }
        return .rows(rows, changes: changes)
    }
}

struct DiffSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var result: DiffResult?
    @State private var onlyChanges = false

    private var files: (URL, URL)? { model.diffFiles }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Porovnání souborů").font(.headline)
                Spacer()
                Toggle("Jen rozdíly", isOn: $onlyChanges).toggleStyle(.checkbox)
                Button("Zavřít") { dismiss() }.keyboardShortcut(.cancelAction)
            }
            .padding(10)
            if let (a, b) = files {
                HStack {
                    Text(a.lastPathComponent).frame(maxWidth: .infinity, alignment: .leading)
                    Text(b.lastPathComponent).frame(maxWidth: .infinity, alignment: .leading)
                }
                .font(.system(size: 12, weight: .semibold))
                .padding(.horizontal, 10).padding(.bottom, 6)
            }
            Divider()
            content
        }
        .frame(minWidth: 900, minHeight: 560)
        .task {
            guard let (a, b) = files else { return }
            result = await Task.detached { DiffEngine.compute(a, b) }.value
        }
    }

    @ViewBuilder
    private var content: some View {
        switch result {
        case nil:
            VStack { ProgressView("Porovnávám…").padding(40) }.frame(maxWidth: .infinity)
        case .binary(let identical)?:
            message(identical ? "Binární soubory jsou shodné." : "Binární soubory se liší.")
        case .tooLarge(let identical)?:
            message(identical ? "Soubory jsou příliš velké pro porovnání po řádcích, ale jsou shodné."
                              : "Soubory jsou příliš velké pro porovnání po řádcích (nad 3 MB nebo 8000 řádků) a liší se.")
        case .rows(let rows, let changes)?:
            VStack(spacing: 0) {
                Text(changes == 0 ? "Soubory jsou shodné." : "Rozdílných řádků: \(changes)")
                    .font(.system(size: 12)).padding(6).frame(maxWidth: .infinity, alignment: .leading)
                ScrollView([.vertical, .horizontal]) {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(rows.filter { !onlyChanges || $0.kind != .same }) { row in
                            HStack(spacing: 0) {
                                cell(row.leftNo, row.left, tint: row.kind == .removed ? .red : nil)
                                cell(row.rightNo, row.right, tint: row.kind == .added ? .green : nil)
                            }
                        }
                    }
                }
            }
        }
    }

    private func message(_ text: String) -> some View {
        Text(text).padding(40).frame(maxWidth: .infinity)
    }

    private func cell(_ number: Int?, _ text: String?, tint: Color?) -> some View {
        HStack(spacing: 6) {
            Text(number.map(String.init) ?? "").foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
            Text(text ?? "").lineLimit(1)
            Spacer(minLength: 0)
        }
        .font(.system(size: 11, design: .monospaced))
        .frame(width: 450, height: 16, alignment: .leading)
        .background((tint ?? .clear).opacity(0.22))
    }
}
