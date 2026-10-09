import SwiftUI
import CryptoKit

struct DupGroup: Identifiable {
    let id = UUID()
    let size: Int64
    var files: [URL]
}

/// Hledá soubory se stejným obsahem: nejdřív podle velikosti, pak podle prvních 64 kB, nakonec podle SHA-256.
final class DuplicateFinder: ObservableObject {
    @Published var groups: [DupGroup] = []
    @Published var status = ""
    @Published var running = false
    private var flag = CancelFlag()
    static let fileLimit = 300_000

    func start(roots: [URL], minSize: Int64, hidden: Bool) {
        stop()
        groups = []
        running = true
        status = "Procházím složky…"
        let flag = CancelFlag()
        self.flag = flag
        Task.detached(priority: .userInitiated) { [weak self] in
            let report: @Sendable (String) -> Void = { text in
                DispatchQueue.main.async { self?.status = text }
            }
            let found = DuplicateFinder.scan(roots, minSize, hidden, flag, report)
            DispatchQueue.main.async {
                self?.groups = found.sorted { $0.size * Int64($0.files.count) > $1.size * Int64($1.files.count) }
                self?.running = false
                self?.status = flag.isSet ? "Zastaveno." : (found.isEmpty ? "Žádné duplicity." : "Nalezeno skupin: \(found.count)")
            }
        }
    }

    func stop() {
        flag.set()
    }

    private static func scan(_ roots: [URL], _ minSize: Int64, _ hidden: Bool, _ flag: CancelFlag,
                             _ report: @Sendable (String) -> Void) -> [DupGroup] {
        var bySize: [Int64: [URL]] = [:]
        var count = 0
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey]
        let options: FileManager.DirectoryEnumerationOptions = hidden ? [] : [.skipsHiddenFiles]
        var seen = Set<String>()
        outer: for root in roots {
            guard let en = FileManager.default.enumerator(at: root, includingPropertiesForKeys: keys, options: options) else { continue }
            for case let url as URL in en {
                if flag.isSet { return [] }
                guard let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true else { continue }
                let size = Int64(v.fileSize ?? 0)
                guard size >= minSize, size > 0, seen.insert(url.path).inserted else { continue }
                bySize[size, default: []].append(url)
                count += 1
                if count % 500 == 0 { report("Prohledáno souborů: \(count)") }
                if count >= fileLimit { break outer }
            }
        }
        let candidates = bySize.filter { $0.value.count > 1 }
        var result: [DupGroup] = []
        var done = 0
        for (size, urls) in candidates {
            if flag.isSet { return [] }
            done += 1
            report("Porovnávám obsah (\(done)/\(candidates.count))…")
            var byPartial: [String: [URL]] = [:]
            for u in urls { if let h = hash(u, limit: 64 * 1024) { byPartial[h, default: []].append(u) } }
            for (_, partial) in byPartial where partial.count > 1 {
                var byFull: [String: [URL]] = [:]
                for u in partial {
                    if flag.isSet { return [] }
                    if let h = hash(u, limit: nil) { byFull[h, default: []].append(u) }
                }
                for (_, same) in byFull where same.count > 1 {
                    result.append(DupGroup(size: size, files: same.sorted { $0.path < $1.path }))
                }
            }
        }
        return result
    }

    private static func hash(_ url: URL, limit: Int?) -> String? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        var sha = SHA256()
        var remaining = limit ?? Int.max
        while remaining > 0, let data = try? handle.read(upToCount: min(1 << 20, remaining)), !data.isEmpty {
            sha.update(data: data)
            remaining -= data.count
        }
        return sha.finalize().map { String(format: "%02x", $0) }.joined()
    }
}

struct DuplicatesSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @StateObject private var finder = DuplicateFinder()
    @State private var bothPanes = false
    @State private var minKB = "1"
    @State private var hidden = false
    @State private var selected: Set<URL> = []
    @State private var roots: [URL] = []

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Hledání duplicit").font(.headline)
            HStack {
                Toggle("Hledat i ve složce druhého panelu", isOn: $bothPanes)
                Toggle("Včetně skrytých", isOn: $hidden)
                Spacer()
                Text("Nejmenší velikost (kB)")
                TextField("1", text: $minKB).frame(width: 60).textFieldStyle(.roundedBorder)
            }
            .font(.system(size: 12))
            Text(roots.isEmpty ? "" : "Prohledá se: " + roots.map(\.path).joined(separator: ", "))
                .font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(2)

            HStack {
                if finder.running {
                    Button("Zastavit") { finder.stop() }
                    ProgressView().controlSize(.small)
                } else {
                    Button("Hledat") { start() }.keyboardShortcut(.defaultAction)
                }
                Text(finder.status).font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
            }

            List {
                ForEach(finder.groups) { group in
                    Section("\(group.files.count) × \(ByteCountFormatter.string(fromByteCount: group.size, countStyle: .file))") {
                        ForEach(group.files, id: \.self) { url in
                            HStack {
                                Toggle("", isOn: Binding(get: { selected.contains(url) },
                                                         set: { if $0 { selected.insert(url) } else { selected.remove(url) } }))
                                    .labelsHidden()
                                Text(url.path).font(.system(size: 11)).lineLimit(1).truncationMode(.middle)
                                Spacer()
                                Button("Ukázat") { model.reveal(url); dismiss() }.controlSize(.small)
                            }
                        }
                    }
                }
            }
            .frame(height: 280)

            HStack {
                Button("Vybrat přebytečné (ponechat nejstarší)") { selectExtras() }.disabled(finder.groups.isEmpty)
                Button("Zrušit výběr") { selected = [] }.disabled(selected.isEmpty)
                Spacer()
                Button("Do koše (\(selected.count))") { trashSelected() }.disabled(selected.isEmpty)
                Button("Zavřít") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 720)
        .onAppear { updateRoots() }
        .onChange(of: bothPanes) { _, _ in updateRoots() }
        .onDisappear { finder.stop() }
    }

    private func updateRoots() {
        var r: [URL] = []
        if model.active.connection == nil { r.append(model.active.persistentURL) }
        if bothPanes, model.other.connection == nil, model.other.persistentURL != model.active.persistentURL {
            r.append(model.other.persistentURL)
        }
        roots = r
    }

    private func start() {
        guard !roots.isEmpty else {
            Dialogs.error("Hledání duplicit funguje jen v lokálních složkách.")
            return
        }
        selected = []
        let kb = Int64(minKB.trimmingCharacters(in: .whitespaces)) ?? 1
        finder.start(roots: roots, minSize: max(kb, 0) * 1024, hidden: hidden)
    }

    /// V každé skupině ponechá nejstarší soubor a ostatní označí.
    private func selectExtras() {
        var picked = Set<URL>()
        for group in finder.groups {
            let dated = group.files.map { url -> (URL, Date) in
                let d = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantFuture
                return (url, d)
            }
            guard let keep = dated.min(by: { $0.1 < $1.1 })?.0 else { continue }
            for (url, _) in dated where url != keep { picked.insert(url) }
        }
        selected = picked
    }

    private func trashSelected() {
        let urls = Array(selected)
        guard Dialogs.confirm("Přesunout \(urls.count) souborů do koše?", info: "Vybrané duplicity půjde vrátit příkazem Vrátit poslední operaci.", ok: "Do koše") else { return }
        Task {
            await model.trashURLs(urls)
            for i in finder.groups.indices { finder.groups[i].files.removeAll { selected.contains($0) } }
            finder.groups.removeAll { $0.files.count < 2 }
            selected = []
        }
    }
}
