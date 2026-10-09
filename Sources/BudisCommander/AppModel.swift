import Foundation
import AppKit

struct ViewerContent: Identifiable {
    enum Kind {
        case text(String)
        case image(NSImage)
        case pdf(URL)
    }

    let id = UUID()
    let title: String
    let kind: Kind

    init(title: String, text: String) {
        self.title = title
        kind = .text(text)
    }

    init(title: String, kind: Kind) {
        self.title = title
        self.kind = kind
    }
}

enum ActiveSheet: String, Identifiable {
    case server, network, batchRename, search, settings, favorites
    var id: String { rawValue }
}

@MainActor
final class AppModel: ObservableObject {
    let leftTabs: TabGroup
    let rightTabs: TabGroup
    @Published var activeIsLeft = true
    @Published var progress: Double?
    @Published var progressText = ""
    @Published var viewer: ViewerContent?
    @Published var sheet: ActiveSheet?
    @Published var notice: String?
    @Published var batchItems: [FileItem] = []
    @Published var canCancel = false
    @Published var showCommandLine = false
    let runner = CommandRunner()
    private var transferTask: Task<Void, Never>?
    private var editTasks: [String: Task<Void, Never>] = [:]

    var left: PaneState { leftTabs.current }
    var right: PaneState { rightTabs.current }
    var active: PaneState { activeIsLeft ? left : right }
    var other: PaneState { activeIsLeft ? right : left }
    var activeGroup: TabGroup { activeIsLeft ? leftTabs : rightTabs }

    init() {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first ?? home
        let state = SessionState.load()
        leftTabs = TabGroup(urls: SessionState.existingDirectories(state?.left, fallback: home),
                            selected: state?.leftSel ?? 0)
        rightTabs = TabGroup(urls: SessionState.existingDirectories(state?.right, fallback: downloads),
                             selected: state?.rightSel ?? 0)
        activeIsLeft = state?.activeLeft ?? true

        PaneState.onLocationChange = { [weak self] in self?.saveState() }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification,
                                               object: nil, queue: .main) { _ in
            try? FileManager.default.removeItem(at: PaneState.archiveBase)
        }
        for name in [NSApplication.willTerminateNotification, NSApplication.didResignActiveNotification] {
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.saveState() }
            }
        }
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                // Vzdálené panely se při návratu do aplikace neobnovují (zbytečný provoz).
                if self?.left.connection == nil { self?.left.reload() }
                if self?.right.connection == nil { self?.right.reload() }
            }
        }
    }

    func switchPane() {
        activeIsLeft.toggle()
        saveState()
    }

    func saveState() {
        SessionState(left: leftTabs.tabs.map { $0.persistentURL.path }, leftSel: leftTabs.selected,
                     right: rightTabs.tabs.map { $0.persistentURL.path }, rightSel: rightTabs.selected,
                     activeLeft: activeIsLeft).save()
    }

    private func describe(_ items: [FileItem]) -> String {
        items.count == 1 ? "„\(items[0].name)“" : "\(items.count) položek"
    }

    private func exists(_ name: String, in pane: PaneState) -> Bool {
        if pane.connection != nil { return pane.allItems.contains { !$0.isParent && $0.name == name } }
        return FileManager.default.fileExists(atPath: pane.url.appendingPathComponent(name).path)
    }

    private func newTempDir() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    // MARK: F5 / F6

    func transfer(move: Bool) async {
        let src = active, dst = other
        let items = src.targets
        guard !items.isEmpty else { return }

        if dst.isArchive {
            Dialogs.error("Archiv je otevřený jen pro čtení, nelze do něj kopírovat.")
            return
        }
        if src.isArchive && move {
            Dialogs.error("Z archivu lze soubory jen kopírovat (F5), ne přesouvat.")
            return
        }
        let sameFolder: Bool
        if src.connection == nil && dst.connection == nil { sameFolder = src.url == dst.url }
        else { sameFolder = src.connection === dst.connection && src.remotePath == dst.remotePath }
        if sameFolder {
            Dialogs.error("Zdrojový a cílový adresář jsou stejné.")
            return
        }
        let destTitle = dst.title
        let verb = move ? "Přesunout" : "Kopírovat"
        guard Dialogs.confirm("\(verb) \(describe(items))?", info: "Cíl: \(destTitle)", ok: verb) else { return }

        var overwriteAll = false
        var cancelled = false
        var errors: [String] = []
        for (i, item) in items.enumerated() {
            if Task.isCancelled { cancelled = true; break }
            progress = Double(i) / Double(items.count)
            progressText = "\(verb) \(item.name) (\(i + 1)/\(items.count))"
            let report = progressReporter(index: i, count: items.count, verb: verb)

            if src.connection == nil && dst.connection == nil && item.isDirectory {
                let d = dst.url.path, s = item.url.path
                if d == s || d.hasPrefix(s + "/") {
                    errors.append("\(item.name): nelze \(move ? "přesunout" : "kopírovat") adresář do sebe sama")
                    continue
                }
            }
            var overwrite = false
            if exists(item.name, in: dst) {
                if overwriteAll {
                    overwrite = true
                } else {
                    switch Dialogs.conflict(item.name) {
                    case .overwrite: overwrite = true
                    case .overwriteAll: overwrite = true; overwriteAll = true
                    case .skip: continue
                    case .cancel:
                        finish(src, dst)
                        return
                    }
                }
            }
            do {
                try await transferOne(item, src: src, dst: dst, move: move, overwrite: overwrite, report: report)
            } catch {
                if Task.isCancelled {
                    if dst.connection == nil {
                        try? FileManager.default.removeItem(at: dst.url.appendingPathComponent(item.name))
                    }
                    cancelled = true
                    break
                }
                errors.append("\(item.name): \(error.localizedDescription)")
            }
        }
        src.marked = []
        finish(src, dst)
        if cancelled { showNotice("Přenos zrušen.") }
        if !errors.isEmpty { Dialogs.error(errors.joined(separator: "\n")) }
    }

    private func progressReporter(index: Int, count: Int, verb: String) -> @Sendable (String, Double) -> Void {
        return { [weak self] name, frac in
            Task { @MainActor in
                guard let self else { return }
                self.progress = (Double(index) + frac) / Double(count)
                self.progressText = "\(verb) \(name) – \(Int(frac * 100)) % (\(index + 1)/\(count))"
            }
        }
    }

    private func transferOne(_ item: FileItem, src: PaneState, dst: PaneState, move: Bool, overwrite: Bool,
                             report: @escaping @Sendable (String, Double) -> Void) async throws {
        switch (src.connection, dst.connection) {
        case (nil, nil):
            let source = item.url
            let dest = dst.url.appendingPathComponent(item.name)
            let renameOnly = move && LocalFS.sameVolume(source, dst.url)
            try await Task.detached {
                let fm = FileManager.default
                if overwrite && fm.fileExists(atPath: dest.path) { try fm.removeItem(at: dest) }
                if renameOnly { try fm.moveItem(at: source, to: dest) }
            }.value
            if !renameOnly {
                try await LocalFS.copy(from: source, to: dest, progress: report)
                if move { try await Task.detached { try FileManager.default.removeItem(at: source) }.value }
            }

        case (nil, let d?):
            try await d.upload(local: item.url, to: RemotePath.child(dst.remotePath, item.name), progress: report)
            if move { try FileManager.default.removeItem(at: item.url) }

        case (let s?, nil):
            guard let path = item.remotePath else { return }
            let dest = dst.url.appendingPathComponent(item.name)
            if overwrite && FileManager.default.fileExists(atPath: dest.path) {
                try FileManager.default.removeItem(at: dest)
            }
            try await s.download(path: path, isDirectory: item.isDirectory, size: item.size, to: dest, progress: report)
            if move { try await s.delete(path: path, isDirectory: item.isDirectory) }

        case (let s?, let d?):
            guard let path = item.remotePath else { return }
            let tmp = try newTempDir()
            defer { try? FileManager.default.removeItem(at: tmp) }
            let local = tmp.appendingPathComponent(item.name)
            try await s.download(path: path, isDirectory: item.isDirectory, size: item.size, to: local,
                                 progress: { name, f in report(name, f / 2) })
            try await d.upload(local: local, to: RemotePath.child(dst.remotePath, item.name),
                               progress: { name, f in report(name, 0.5 + f / 2) })
            if move { try await s.delete(path: path, isDirectory: item.isDirectory) }
        }
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
        guard !pane.isArchive else {
            Dialogs.error("Archiv je otevřený jen pro čtení.")
            return
        }
        let conn = pane.connection
        let ok = conn == nil
            ? Dialogs.confirm("Přesunout \(describe(items)) do koše?", ok: "Do koše")
            : Dialogs.confirm("Trvale smazat \(describe(items)) ze serveru?", info: "Tuto akci nelze vrátit zpět.", ok: "Smazat")
        guard ok else { return }

        var errors: [String] = []
        for (i, item) in items.enumerated() {
            if Task.isCancelled { break }
            progress = Double(i) / Double(items.count)
            progressText = "Mažu \(item.name)"
            do {
                if let conn, let path = item.remotePath {
                    try await conn.delete(path: path, isDirectory: item.isDirectory)
                } else {
                    let url = item.url
                    try await Task.detached { try FileManager.default.trashItem(at: url, resultingItemURL: nil) }.value
                }
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

    // MARK: F7, F2

    func makeDirectory() async {
        let pane = active
        guard !pane.isArchive else { Dialogs.error("Archiv je otevřený jen pro čtení."); return }
        guard let name = Dialogs.prompt("Nový adresář", info: "Vytvoří se v \(pane.title)", ok: "Vytvořit") else { return }
        do {
            if let conn = pane.connection {
                try await conn.mkdir(RemotePath.child(pane.remotePath, name))
                await pane.loadRemote(pane.remotePath)
            } else {
                try FileManager.default.createDirectory(at: pane.url.appendingPathComponent(name),
                                                        withIntermediateDirectories: true)
                pane.reload()
            }
            if let idx = pane.items.firstIndex(where: { $0.name == name }) { pane.cursor = idx }
        } catch {
            Dialogs.error(error.localizedDescription)
        }
    }

    func rename() async {
        let pane = active
        guard !pane.isArchive else { Dialogs.error("Archiv je otevřený jen pro čtení."); return }
        guard let item = pane.current, !item.isParent,
              let name = Dialogs.prompt("Přejmenovat", initial: item.name, ok: "Přejmenovat"),
              name != item.name else { return }
        do {
            if let conn = pane.connection, let path = item.remotePath {
                try await conn.rename(from: path, to: RemotePath.child(RemotePath.parent(path), name))
                await pane.loadRemote(pane.remotePath)
            } else {
                let dest = item.url.deletingLastPathComponent().appendingPathComponent(name)
                try FileManager.default.moveItem(at: item.url, to: dest)
                pane.reload()
            }
            if let idx = pane.items.firstIndex(where: { $0.name == name }) { pane.cursor = idx }
        } catch {
            Dialogs.error(error.localizedDescription)
        }
    }

    // MARK: F3 / F4

    func view() async {
        let pane = active
        guard let item = pane.current, !item.isParent else { return }
        if item.isDirectory { pane.enter(); return }

        let ext = (item.name as NSString).pathExtension.lowercased()
        let isImage = ["png", "jpg", "jpeg", "gif", "heic", "tif", "tiff", "bmp", "webp"].contains(ext)
        let isMedia = isImage || ext == "pdf"

        var fileURL = item.url
        if let conn = pane.connection, let path = item.remotePath {
            if isMedia && item.size > 100 * 1024 * 1024 {
                Dialogs.error("Soubor je příliš velký pro náhled (nad 100 MB). Zkopírujte ho do lokálního panelu.")
                return
            }
            do {
                let tmp = try newTempDir()
                fileURL = tmp.appendingPathComponent(item.name)
                notice = "Stahuji náhled \(item.name)…"
                try await conn.download(path: path, isDirectory: false, size: item.size, to: fileURL,
                                        maxBytes: isMedia ? nil : 512 * 1024)
                notice = nil
            } catch {
                notice = nil
                Dialogs.error(error.localizedDescription)
                return
            }
        }
        if isMedia {
            if ext == "pdf" {
                viewer = ViewerContent(title: item.name, kind: .pdf(fileURL))
            } else if let image = NSImage(contentsOf: fileURL) {
                viewer = ViewerContent(title: item.name, kind: .image(image))
            } else {
                NSWorkspace.shared.open(fileURL)
            }
            return
        }
        guard let handle = try? FileHandle(forReadingFrom: fileURL) else { return }
        defer { try? handle.close() }
        let data = (try? handle.read(upToCount: 512 * 1024)) ?? Data()
        if data.contains(0) {
            NSWorkspace.shared.open(fileURL)   // binární soubor – výchozí aplikace
            return
        }
        viewer = ViewerContent(title: item.name, text: String(decoding: data, as: UTF8.self))
    }

    private func openInTextEditor(_ url: URL) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-t", url.path]
        try? p.run()
    }

    private static func modificationDate(_ url: URL) -> Date? {
        (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
    }

    private func showNotice(_ text: String) {
        notice = text
        Task {
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            if notice == text { notice = nil }
        }
    }

    /// Lokální soubor otevře v editoru; u souboru na serveru ho stáhne, otevře a po každém uložení
    /// nahraje změny zpět.
    func edit() async {
        let pane = active
        guard let item = pane.current, !item.isParent, !item.isDirectory else { return }
        guard let conn = pane.connection, let path = item.remotePath else {
            openInTextEditor(item.url)
            return
        }

        let key = "\(conn.displayName)\(path)"
        editTasks[key]?.cancel()
        let file: URL
        do {
            let tmp = try newTempDir()
            file = tmp.appendingPathComponent(item.name)
            notice = "Stahuji \(item.name)…"
            try await conn.download(path: path, isDirectory: false, to: file)
        } catch {
            notice = nil
            Dialogs.error(error.localizedDescription)
            return
        }
        showNotice("Editace „\(item.name)“: po uložení se změny nahrají na server")
        openInTextEditor(file)

        let dir = RemotePath.parent(path)
        editTasks[key] = Task { [weak self] in
            var last = Self.modificationDate(file)
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                guard Self.modificationDate(file) != last else { continue }
                try? await Task.sleep(nanoseconds: 300_000_000)   // editor dopíše soubor
                last = Self.modificationDate(file)
                guard let self else { return }
                self.notice = "Nahrávám změny „\(item.name)“ na server…"
                do {
                    try await conn.upload(local: file, to: path)
                    self.showNotice("Uloženo na server: \(item.name)")
                    if pane.connection === conn && pane.remotePath == dir { pane.reload() }
                } catch {
                    self.notice = nil
                    Dialogs.error("Změny „\(item.name)“ se nepodařilo nahrát:\n\(error.localizedDescription)")
                }
            }
        }
    }

    // MARK: Označování podle masky

    func markByMask(on: Bool) {
        guard let mask = Dialogs.prompt(on ? "Označit podle masky" : "Odznačit podle masky",
                                        info: "Např. *.jpg", initial: "*") else { return }
        active.mark(matching: mask, on: on)
    }

    // MARK: Akce ze zkratek a menu

    func perform(_ action: ShortcutAction) {
        switch action {
        case .newTab: activeGroup.newTab()
        case .closeTab: activeGroup.close(activeGroup.selected)
        case .refresh: active.reload()
        case .markAll: active.markAll()
        case .hidden: active.toggleHidden()
        case .mirror: other.navigate(to: active.url)
        case .connect: sheet = .server
        case .network: sheet = .network
        case .compare: compare()
        case .batchRename: startBatchRename()
        case .pack: Task { await pack() }
        case .unpack: Task { await unpack() }
        case .search: sheet = .search
        case .settings: sheet = .settings
        case .sync: startSync()
        case .commandLine: showCommandLine.toggle()
        case .favorites: sheet = .favorites
        case .back: active.goBack()
        case .forward: active.goForward()
        }
    }

    // MARK: Spuštění úloh se zrušením

    private func runCancellable(_ work: @escaping @MainActor () async -> Void) {
        guard transferTask == nil else {
            showNotice("Právě probíhá jiná operace.")
            return
        }
        canCancel = true
        transferTask = Task {
            await work()
            transferTask = nil
            canCancel = false
        }
    }

    func startTransfer(move: Bool) { runCancellable { await self.transfer(move: move) } }
    func startDelete() { runCancellable { await self.delete() } }
    func startSync() { runCancellable { await self.syncMirror() } }
    func cancelTransfer() { transferTask?.cancel() }

    // MARK: Zrcadlení adresářů

    /// Udělá z cílového (neaktivního) panelu přesnou kopii aktivního: nové a změněné soubory zkopíruje,
    /// soubory navíc v cíli přesune do koše.
    func syncMirror() async {
        let src = active, dst = other
        guard src.connection == nil, dst.connection == nil else {
            Dialogs.error("Zrcadlení funguje jen mezi lokálními složkami.")
            return
        }
        guard !dst.isArchive else {
            Dialogs.error("Archiv je otevřený jen pro čtení.")
            return
        }
        let s = src.url, d = dst.url
        guard s != d, !d.path.hasPrefix(s.path + "/"), !s.path.hasPrefix(d.path + "/") else {
            Dialogs.error("Zdrojová a cílová složka se nesmí překrývat.")
            return
        }
        notice = "Počítám rozdíly…"
        let plan = await Task.detached { SyncPlan.make(src: s, dst: d) }.value
        notice = nil
        guard !plan.isEmpty else {
            showNotice("Složky jsou shodné, není co zrcadlit.")
            return
        }
        let size = ByteCountFormatter.string(fromByteCount: plan.bytes, countStyle: .file)
        var info = "Kopírovat nebo přepsat: \(plan.copies.count) souborů (\(size))\nVytvořit složek: \(plan.mkdirs.count)\n"
        info += "Přesunout do koše (jen v cíli): \(plan.deletes.count)"
        let preview = plan.deletes.prefix(6).map { "  • " + $0.lastPathComponent }
        if !preview.isEmpty { info += "\n" + preview.joined(separator: "\n") + (plan.deletes.count > 6 ? "\n  …" : "") }
        info += "\n\nCíl bude přesně odpovídat zdroji, i když je v něm některý soubor novější."
        guard Dialogs.confirm("Zrcadlit „\(s.lastPathComponent)“ do „\(d.lastPathComponent)“?", info: info, ok: "Zrcadlit") else { return }

        var errors: [String] = []
        let total = plan.deletes.count + plan.mkdirs.count + plan.copies.count
        var step = 0
        func tick(_ text: String) {
            progress = Double(step) / Double(max(total, 1))
            progressText = text
            step += 1
        }
        for url in plan.deletes {
            if Task.isCancelled { break }
            tick("Do koše: \(url.lastPathComponent)")
            do { try await Task.detached { try FileManager.default.trashItem(at: url, resultingItemURL: nil) }.value }
            catch { errors.append("\(url.lastPathComponent): \(error.localizedDescription)") }
        }
        for url in plan.mkdirs {
            if Task.isCancelled { break }
            tick("Složka: \(url.lastPathComponent)")
            do { try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true) }
            catch { errors.append("\(url.lastPathComponent): \(error.localizedDescription)") }
        }
        for (from, to) in plan.copies {
            if Task.isCancelled { break }
            tick("Kopíruji: \(from.lastPathComponent)")
            do {
                try await Task.detached {
                    let fm = FileManager.default
                    if fm.fileExists(atPath: to.path) { try fm.removeItem(at: to) }
                    try fm.copyItem(at: from, to: to)
                }.value
            } catch {
                errors.append("\(from.lastPathComponent): \(error.localizedDescription)")
            }
        }
        progress = nil
        progressText = ""
        src.reload()
        dst.reload()
        showNotice(Task.isCancelled ? "Zrcadlení zrušeno." : "Zrcadlení hotovo.")
        if !errors.isEmpty { Dialogs.error(errors.prefix(12).joined(separator: "\n")) }
    }

    // MARK: Porovnání adresářů

    /// Označí v obou panelech soubory, které chybí v druhém panelu nebo jsou novější / jiné.
    /// Označené soubory pak stačí zkopírovat (F5) a adresáře jsou sesynchronizované.
    func compare() {
        let a = left, b = right
        a.clearFilter()
        b.clearFilter()
        func files(_ p: PaneState) -> [String: FileItem] {
            Dictionary(p.items.filter { !$0.isParent }.map { ($0.name, $0) }, uniquingKeysWith: { first, _ in first })
        }
        let fa = files(a), fb = files(b)
        let tolerance: TimeInterval = (a.connection != nil || b.connection != nil) ? 120 : 2
        var markA = Set<String>(), markB = Set<String>()

        for (name, x) in fa {
            guard let y = fb[name] else { markA.insert(x.id); continue }
            if x.isDirectory || y.isDirectory { continue }
            if let dx = x.modified, let dy = y.modified, abs(dx.timeIntervalSince(dy)) > tolerance {
                if dx > dy { markA.insert(x.id) } else { markB.insert(y.id) }
            } else if x.size != y.size {
                markA.insert(x.id)
                markB.insert(y.id)
            }
        }
        for (name, y) in fb where fa[name] == nil { markB.insert(y.id) }

        a.marked = markA
        b.marked = markB
        showNotice("Porovnání: vlevo označeno \(markA.count), vpravo \(markB.count) (chybějící, novější nebo jiné). Zkopírujte je klávesou F5.")
    }

    // MARK: Hromadné přejmenování

    func startBatchRename() {
        let items = active.targets
        guard !items.isEmpty else { return }
        guard !active.isArchive else { Dialogs.error("Archiv je otevřený jen pro čtení."); return }
        batchItems = items
        sheet = .batchRename
    }

    func applyRename(_ items: [FileItem], _ names: [String]) async {
        let pane = active
        let pairs = Array(zip(items, names)).filter { $0.0.name != $0.1 }
        guard !pairs.isEmpty else { return }

        func rename(_ item: FileItem, from: String, to: String) async throws {
            if let conn = pane.connection, let path = item.remotePath {
                let dir = RemotePath.parent(path)
                try await conn.rename(from: RemotePath.child(dir, from), to: RemotePath.child(dir, to))
            } else {
                let dir = item.url.deletingLastPathComponent()
                try FileManager.default.moveItem(at: dir.appendingPathComponent(from), to: dir.appendingPathComponent(to))
            }
        }

        let oldNames = Set(pairs.map { $0.0.name })
        let needsTemp = pairs.contains { oldNames.contains($0.1) }
        var errors: [String] = []
        var staged: [(FileItem, String, String)] = []   // položka, aktuální název, cílový název

        for (i, (item, new)) in pairs.enumerated() {
            progress = Double(i) / Double(pairs.count)
            progressText = "Přejmenovávám \(item.name)"
            do {
                if needsTemp {
                    let tmp = ".__brn\(i)_\(UUID().uuidString.prefix(6))"
                    try await rename(item, from: item.name, to: tmp)
                    staged.append((item, tmp, new))
                } else {
                    try await rename(item, from: item.name, to: new)
                }
            } catch {
                errors.append("\(item.name): \(error.localizedDescription)")
            }
        }
        for (item, tmp, new) in staged {
            do { try await rename(item, from: tmp, to: new) }
            catch { errors.append("\(new): \(error.localizedDescription)") }
        }
        progress = nil
        progressText = ""
        pane.marked = []
        pane.reload()
        if !errors.isEmpty { Dialogs.error(errors.joined(separator: "\n")) }
    }

    // MARK: Archivy

    func pack() async {
        let src = active
        guard src.connection == nil else {
            Dialogs.error("Balení funguje jen na lokálním disku. Soubory ze serveru nejdřív zkopírujte (F5).")
            return
        }
        let items = src.targets
        guard !items.isEmpty else { return }
        let destDir = other.connection == nil ? other.url : src.url
        let base = items.count == 1 ? items[0].name
            : (src.url.lastPathComponent.isEmpty ? "Archiv" : src.url.lastPathComponent)
        guard let name = Dialogs.prompt("Zabalit do archivu", info: "Do: \(destDir.path)\nPodporováno: .zip, .tar.gz",
                                        initial: base + ".zip", ok: "Zabalit") else { return }
        let lower = name.lowercased()
        let isZip = lower.hasSuffix(".zip")
        guard isZip || lower.hasSuffix(".tar.gz") || lower.hasSuffix(".tgz") else {
            Dialogs.error("Název archivu musí končit na .zip, .tar.gz nebo .tgz.")
            return
        }
        let dest = destDir.appendingPathComponent(name)
        if FileManager.default.fileExists(atPath: dest.path) {
            guard Dialogs.confirm("Archiv „\(name)“ už existuje. Přepsat?", ok: "Přepsat") else { return }
            try? FileManager.default.removeItem(at: dest)
        }
        let names = items.map { $0.name.hasPrefix("-") ? "./" + $0.name : $0.name }
        notice = "Balím \(name)…"
        do {
            if isZip {
                try await Shell.run("/usr/bin/zip", ["-r", "-q", dest.path] + names, cwd: src.url)
            } else {
                try await Shell.run("/usr/bin/tar", ["-czf", dest.path] + names, cwd: src.url)
            }
            src.marked = []
            showNotice("Archiv vytvořen: \(name)")
        } catch {
            notice = nil
            Dialogs.error(error.localizedDescription)
        }
        src.reload()
        other.reload()
    }

    func unpack() async {
        let src = active
        guard src.connection == nil, let item = src.current, !item.isParent, !item.isDirectory else {
            Dialogs.error("Vyberte lokální soubor s archivem (zip, tar, tar.gz, tar.bz2, 7z…).")
            return
        }
        let destDir = other.connection == nil ? other.url : src.url
        guard Dialogs.confirm("Rozbalit „\(item.name)“?", info: "Do: \(destDir.path)", ok: "Rozbalit") else { return }
        notice = "Rozbaluji \(item.name)…"
        do {
            try await Shell.run("/usr/bin/tar", ["-xf", item.url.path, "-C", destDir.path])
            showNotice("Rozbaleno do \(destDir.lastPathComponent)")
        } catch {
            notice = nil
            Dialogs.error(error.localizedDescription)
        }
        src.reload()
        other.reload()
    }

    // MARK: Hledání

    /// Otevře složku nalezeného souboru v aktivním panelu a vybere ho.
    func reveal(_ url: URL) {
        let pane = active
        if pane.connection != nil {
            Task { await pane.loadRemote(RemotePath.parent(url.path), select: url.path) }
            return
        }
        pane.navigate(to: url.deletingLastPathComponent())
        if let idx = pane.items.firstIndex(where: { $0.url.path == url.path }) { pane.cursor = idx }
    }

    // MARK: Servery a síť

    func connect(_ conn: RemoteConnection) async -> Bool {
        await active.loadRemote(conn.startPath, select: "", using: conn)
    }

    static func mountedVolumes() -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: "/Volumes")) ?? []).sorted()
    }

    /// Připojí síťový disk (smb://, afp://, nfs://…) přes systém a otevře ho v aktivním panelu.
    func mount(_ address: String) async {
        let before = Set(Self.mountedVolumes())
        let escaped = address.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let script = "mount volume \"\(escaped)\""
        progressText = "Připojuji \(address)…"
        progress = 0
        let (status, err): (Int32, String) = await Task.detached { () -> (Int32, String) in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            p.arguments = ["-e", script]
            let pipe = Pipe()
            p.standardError = pipe
            p.standardOutput = Pipe()
            do { try p.run() } catch { return (1, error.localizedDescription) }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            return (p.terminationStatus, String(decoding: data, as: UTF8.self))
        }.value
        progress = nil
        progressText = ""
        if status != 0 {
            Dialogs.error("Disk se nepodařilo připojit:\n\(err.trimmingCharacters(in: .whitespacesAndNewlines))")
            return
        }
        let added = Set(Self.mountedVolumes()).subtracting(before).sorted()
        active.navigate(to: URL(fileURLWithPath: added.first.map { "/Volumes/\($0)" } ?? "/Volumes"))
    }
}
