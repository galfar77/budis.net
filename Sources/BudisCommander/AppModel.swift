import Foundation
import AppKit

struct ViewerContent: Identifiable {
    let id = UUID()
    let title: String
    let text: String
}

@MainActor
final class AppModel: ObservableObject {
    let left: PaneState
    let right: PaneState
    @Published var activeIsLeft = true
    @Published var progress: Double?
    @Published var progressText = ""
    @Published var viewer: ViewerContent?

    var active: PaneState { activeIsLeft ? left : right }
    var other: PaneState { activeIsLeft ? right : left }

    init() {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let docs = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first ?? home
        left = PaneState(url: home)
        right = PaneState(url: docs)
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.left.reload(); self?.right.reload() }
        }
    }

    func switchPane() { activeIsLeft.toggle() }

    private func describe(_ items: [FileItem]) -> String {
        items.count == 1 ? "„\(items[0].name)“" : "\(items.count) položek"
    }

    // MARK: F5 / F6

    func transfer(move: Bool) async {
        let src = active, dst = other
        let items = src.targets
        guard !items.isEmpty else { return }
        let destDir = dst.url
        if src.url == destDir {
            Dialogs.error("Zdrojový a cílový adresář jsou stejné.")
            return
        }
        let verb = move ? "Přesunout" : "Kopírovat"
        guard Dialogs.confirm("\(verb) \(describe(items))?", info: "Cíl: \(destDir.path)", ok: verb) else { return }

        var overwriteAll = false
        var errors: [String] = []
        for (i, item) in items.enumerated() {
            progress = Double(i) / Double(items.count)
            progressText = "\(verb) \(item.name) (\(i + 1)/\(items.count))"
            let source = item.url
            let dest = destDir.appendingPathComponent(item.name)

            if item.isDirectory && (destDir.path == source.path || destDir.path.hasPrefix(source.path + "/")) {
                errors.append("\(item.name): nelze \(move ? "přesunout" : "kopírovat") adresář do sebe sama")
                continue
            }
            var overwrite = overwriteAll
            if FileManager.default.fileExists(atPath: dest.path) && !overwriteAll {
                switch Dialogs.conflict(item.name) {
                case .overwrite: overwrite = true
                case .overwriteAll: overwrite = true; overwriteAll = true
                case .skip: continue
                case .cancel:
                    finish(src, dst)
                    return
                }
            } else if FileManager.default.fileExists(atPath: dest.path) {
                overwrite = true
            }
            do {
                try await Task.detached {
                    let fm = FileManager.default
                    if overwrite && fm.fileExists(atPath: dest.path) { try fm.removeItem(at: dest) }
                    if move { try fm.moveItem(at: source, to: dest) } else { try fm.copyItem(at: source, to: dest) }
                }.value
            } catch {
                errors.append("\(item.name): \(error.localizedDescription)")
            }
        }
        src.marked = []
        finish(src, dst)
        if !errors.isEmpty { Dialogs.error(errors.joined(separator: "\n")) }
    }

    private func finish(_ a: PaneState, _ b: PaneState) {
        progress = nil
        progressText = ""
        a.reload()
        b.reload()
    }

    // MARK: F8

    func delete() async {
        let pane = active
        let items = pane.targets
        guard !items.isEmpty else { return }
        guard Dialogs.confirm("Přesunout \(describe(items)) do koše?", ok: "Do koše") else { return }
        var errors: [String] = []
        for (i, item) in items.enumerated() {
            progress = Double(i) / Double(items.count)
            progressText = "Mažu \(item.name)"
            let url = item.url
            do {
                try await Task.detached { try FileManager.default.trashItem(at: url, resultingItemURL: nil) }.value
            } catch {
                errors.append("\(item.name): \(error.localizedDescription)")
            }
        }
        pane.marked = []
        progress = nil
        progressText = ""
        pane.reload()
        if !errors.isEmpty { Dialogs.error(errors.joined(separator: "\n")) }
    }

    // MARK: F7, přejmenování

    func makeDirectory() {
        let pane = active
        guard let name = Dialogs.prompt("Nový adresář", info: "Vytvoří se v \(pane.url.path)", ok: "Vytvořit") else { return }
        let url = pane.url.appendingPathComponent(name)
        do {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            pane.reload()
            if let idx = pane.items.firstIndex(where: { $0.url == url }) { pane.cursor = idx }
        } catch {
            Dialogs.error(error.localizedDescription)
        }
    }

    func rename() {
        let pane = active
        guard let item = pane.current, !item.isParent,
              let name = Dialogs.prompt("Přejmenovat", initial: item.name, ok: "Přejmenovat"),
              name != item.name else { return }
        let dest = item.url.deletingLastPathComponent().appendingPathComponent(name)
        do {
            try FileManager.default.moveItem(at: item.url, to: dest)
            pane.reload()
            if let idx = pane.items.firstIndex(where: { $0.url == dest }) { pane.cursor = idx }
        } catch {
            Dialogs.error(error.localizedDescription)
        }
    }

    // MARK: F3 / F4

    func view() {
        guard let item = active.current, !item.isParent else { return }
        if item.isDirectory { active.enter(); return }
        guard let handle = try? FileHandle(forReadingFrom: item.url) else { return }
        defer { try? handle.close() }
        let data = (try? handle.read(upToCount: 512 * 1024)) ?? Data()
        if data.contains(0) {
            NSWorkspace.shared.open(item.url)   // binární soubor – výchozí aplikace
            return
        }
        viewer = ViewerContent(title: item.name, text: String(decoding: data, as: UTF8.self))
    }

    func edit() {
        guard let item = active.current, !item.isParent, !item.isDirectory else { return }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-t", item.url.path]
        try? p.run()
    }

    // MARK: Označování podle masky

    func markByMask(on: Bool) {
        guard let mask = Dialogs.prompt(on ? "Označit podle masky" : "Odznačit podle masky",
                                        info: "Např. *.jpg", initial: "*") else { return }
        active.mark(matching: mask, on: on)
    }
}
