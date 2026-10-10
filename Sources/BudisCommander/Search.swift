import SwiftUI

final class CancelFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false
    var isSet: Bool { lock.lock(); defer { lock.unlock() }; return value }
    func set() { lock.lock(); value = true; lock.unlock() }
}

/// Rekurzivní hledání souborů podle masky názvu a případně obsahu.
final class FileSearch: ObservableObject {
    @Published var results: [URL] = []
    @Published var running = false
    @Published var scanned = 0
    private var flag = CancelFlag()
    static let limit = 5000

    func start(root: URL, mask: String, text: String, hidden: Bool) {
        stop()
        results = []
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
            FileSearch.walk(root, finalPattern, needle, hidden, flag) { batch, scanned in
                DispatchQueue.main.async {
                    self?.results.append(contentsOf: batch)
                    self?.scanned = scanned
                }
            }
            DispatchQueue.main.async { self?.running = false }
        }
    }

    /// Hledání podle názvu na serveru: prochází složky do šířky (nejvýš 3000 výpisů).
    func startRemote(conn: RemoteConnection, root: String, mask: String, hidden: Bool) {
        stop()
        results = []
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
            while !queue.isEmpty && !flag.isSet && listed < 3000 && found < FileSearch.limit {
                let dir = queue.removeFirst()
                listed += 1
                guard let items = try? await conn.list(dir) else { continue }
                var batch: [URL] = []
                for item in items {
                    if !hidden && item.name.hasPrefix(".") { continue }
                    guard let path = item.remotePath else { continue }
                    if item.isDirectory { queue.append(path) }
                    if fnmatch(finalPattern, item.name, FNM_CASEFOLD) == 0 {
                        batch.append(URL(fileURLWithPath: path))
                        found += 1
                    }
                }
                let count = listed
                DispatchQueue.main.async {
                    self?.results.append(contentsOf: batch)
                    self?.scanned = count
                }
            }
            DispatchQueue.main.async { self?.running = false }
        }
    }

    func stop() {
        flag.set()
        running = false
    }

    private static func walk(_ root: URL, _ mask: String, _ text: String, _ hidden: Bool,
                             _ flag: CancelFlag, _ report: ([URL], Int) -> Void) {
        let opts: FileManager.DirectoryEnumerationOptions = hidden ? [] : [.skipsHiddenFiles]
        guard let en = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey],
                                                      options: opts) else { return }
        var batch: [URL] = []
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
            if batch.count >= 50 || scanned % 500 == 0 {
                report(batch, scanned)
                batch = []
            }
            if found >= limit { break }
        }
        report(batch, scanned)
    }

    private static func fileContains(_ url: URL, _ text: String) -> Bool {
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
    @State private var conn: RemoteConnection?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Hledat soubory")).font(.headline)
            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow { Text(L("Začít v")); TextField("", text: $root) }
                GridRow { Text(L("Název")); TextField(L("např. *.jpg nebo část názvu"), text: $mask) }
                GridRow {
                    Text(L("Obsahuje"))
                    TextField(conn == nil ? L("text v souboru (volitelné)") : L("na serveru jen podle názvu"), text: $text)
                        .disabled(conn != nil)
                }
            }
            .textFieldStyle(.roundedBorder)
            Toggle(L("Včetně skrytých souborů"), isOn: $hidden).font(.system(size: 12))

            HStack {
                if search.running {
                    Button(L("Zastavit")) { search.stop() }
                    ProgressView().controlSize(.small)
                } else {
                    Button(L("Hledat")) { run() }.keyboardShortcut(.defaultAction)
                }
                Text(conn == nil ? L("Nalezeno \(search.results.count), prohledáno \(search.scanned)") : L("Nalezeno \(search.results.count), prohledaných složek \(search.scanned)"))
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
            }

            List(search.results, id: \.self) { url in
                Button {
                    model.reveal(url)
                    dismiss()
                } label: {
                    Text(relative(url)).lineLimit(1).truncationMode(.middle)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.plain)
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
            search.startRemote(conn: conn, root: root, mask: mask, hidden: hidden)
            return
        }
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: root, isDirectory: &isDir), isDir.boolValue else {
            Dialogs.error(L("Složka „\(root)“ neexistuje."))
            return
        }
        search.start(root: URL(fileURLWithPath: root), mask: mask, text: text, hidden: hidden)
    }

    private func relative(_ url: URL) -> String {
        url.path.hasPrefix(root + "/") ? String(url.path.dropFirst(root.count + 1)) : url.path
    }
}
