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
    /// Soubor, který jde otevřít ve výchozí aplikaci (u binárních souborů).
    let openURL: URL?

    init(title: String, text: String, openURL: URL? = nil) {
        self.title = title
        kind = .text(text)
        self.openURL = openURL
    }

    init(title: String, kind: Kind) {
        self.title = title
        self.kind = kind
        openURL = nil
    }
}

enum ActiveSheet: String, Identifiable {
    case server, network, batchRename, search, settings, favorites
    case diff, checksum, attributes, userMenu
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
    @Published var quickView = false
    @Published var queueCount = 0
    @Published var diffFiles: (URL, URL)?
    @Published var checksumItems: [FileItem] = []
    @Published var attributeItems: [FileItem] = []
    private var queue: [QueuedJob] = []
    private var interrupted: TransferPlan?
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

    struct TransferPlan {
        let move: Bool
        let src: PaneState
        let dst: PaneState
        var items: [FileItem]
        let srcKey: String
        let dstKey: String
    }

    struct QueuedJob {
        let run: @MainActor () async -> Void
    }

    private func locationKey(_ p: PaneState) -> String {
        p.connection != nil ? "\(p.connection?.displayName ?? "")|\(p.remotePath)" : "local|\(p.url.path)"
    }

    /// Ověří zadání a zeptá se uživatele; vrátí plán přenosu, který se pak zařadí do fronty.
    func prepareTransfer(move: Bool) -> TransferPlan? {
        let src = active, dst = other
        let items = src.targets
        guard !items.isEmpty else { return nil }

        if dst.isArchive {
            Dialogs.error("Archiv je otevřený jen pro čtení, nelze do něj kopírovat.")
            return nil
        }
        if src.isArchive && move {
            Dialogs.error("Z archivu lze soubory jen kopírovat (F5), ne přesouvat.")
            return nil
        }
        let sameFolder: Bool
        if src.connection == nil && dst.connection == nil { sameFolder = src.url == dst.url }
        else { sameFolder = src.connection === dst.connection && src.remotePath == dst.remotePath }
        if sameFolder && !src.branch {
            Dialogs.error("Zdrojový a cílový adresář jsou stejné.")
            return nil
        }
        let verb = move ? "Přesunout" : "Kopírovat"
        guard Dialogs.confirm("\(verb) \(describe(items))?", info: "Cíl: \(dst.title)", ok: verb) else { return nil }
        return TransferPlan(move: move, src: src, dst: dst, items: items, srcKey: locationKey(src), dstKey: locationKey(dst))
    }

    func transfer(plan: TransferPlan) async {
        let src = plan.src, dst = plan.dst, move = plan.move
        let items = plan.items
        guard locationKey(src) == plan.srcKey, locationKey(dst) == plan.dstKey else {
            Dialogs.error("Panely mezitím přešly do jiných složek. Vraťte je do původních a pokračujte z menu Nástroje.")
            interrupted = plan
            return
        }
        let verb = move ? "Přesunout" : "Kopírovat"

        var overwriteAll = false
        var cancelled = false
        var remaining: [FileItem] = []
        var errors: [String] = []
        for (i, item) in items.enumerated() {
            if Task.isCancelled { cancelled = true; remaining = Array(items[i...]); break }
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
                    remaining = Array(items[i...])
                    break
                }
                errors.append("\(item.name): \(error.localizedDescription)")
            }
        }
        src.marked = []
        finish(src, dst)
        interrupted = cancelled
            ? TransferPlan(move: move, src: src, dst: dst, items: remaining, srcKey: plan.srcKey, dstKey: plan.dstKey)
            : nil
        if cancelled { showNotice("Přenos zrušen. Zbylé položky (\(remaining.count)) jde dokončit z menu Nástroje.") }
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
        if data.prefix(4096).contains(0) {
            // Binární soubor: hexadecimální výpis prvních 64 kB, s možností otevřít ve výchozí aplikaci.
            let dump = LocalFS.hexDump(data.prefix(64 * 1024))
            viewer = ViewerContent(title: item.name + " (hex)", text: dump, openURL: pane.connection == nil ? fileURL : nil)
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
        case .compare: Task { await compare() }
        case .compareContent: Task { await compare(byContent: true) }
        case .diff: startDiff()
        case .checksum: startChecksum()
        case .branch: active.toggleBranch()
        case .dirSizes: calcDirSizes()
        case .attributes: startAttributes()
        case .quickView: quickView.toggle()
        case .split: startSplit()
        case .combine: startCombine()
        case .symlink: makeSymlink()
        case .userMenu: sheet = .userMenu
        case .resumeTransfer: resumeTransfer()
        case .thumbnails: Settings.shared.showThumbs.toggle()
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

    /// Spustí úlohu, nebo ji (u přenosů) zařadí do fronty, pokud už jiná běží.
    private func runCancellable(queue allowQueue: Bool = false, _ work: @escaping @MainActor () async -> Void) {
        if transferTask != nil {
            if allowQueue {
                queue.append(QueuedJob(run: work))
                queueCount = queue.count
                showNotice("Přidáno do fronty (ve frontě: \(queue.count))")
            } else {
                showNotice("Právě probíhá jiná operace.")
            }
            return
        }
        startJob(work)
    }

    private func startJob(_ work: @escaping @MainActor () async -> Void) {
        canCancel = true
        transferTask = Task {
            await work()
            transferTask = nil
            canCancel = false
            if !queue.isEmpty {
                let next = queue.removeFirst()
                queueCount = queue.count
                startJob(next.run)
            }
        }
    }

    func startTransfer(move: Bool) {
        guard let plan = prepareTransfer(move: move) else { return }
        runCancellable(queue: true) { await self.transfer(plan: plan) }
    }

    func resumeTransfer() {
        guard let plan = interrupted else {
            showNotice("Není žádný přerušený přenos.")
            return
        }
        interrupted = nil
        runCancellable(queue: true) { await self.transfer(plan: plan) }
    }

    func startDelete() { runCancellable { await self.delete() } }
    func startSync() { runCancellable { await self.syncMirror() } }

    /// Zruší běžící úlohu; `all` zruší i celou frontu.
    func cancelTransfer(all: Bool = false) {
        if all { queue.removeAll(); queueCount = 0 }
        transferTask?.cancel()
    }

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

    private enum MarkSide { case a, b, both }

    /// Označí v obou panelech soubory, které chybí v druhém panelu nebo jsou novější / jiné.
    /// Označené soubory pak stačí zkopírovat (F5) a adresáře jsou sesynchronizované.
    func compare(byContent: Bool = false) async {
        let a = left, b = right
        a.clearFilter()
        b.clearFilter()
        let content = byContent && a.connection == nil && b.connection == nil
        var toCheck: [(x: FileItem, y: FileItem, side: MarkSide)] = []
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
                if content && x.size == y.size { toCheck.append((x, y, dx > dy ? .a : .b)) }
                else if dx > dy { markA.insert(x.id) } else { markB.insert(y.id) }
            } else if x.size != y.size {
                markA.insert(x.id)
                markB.insert(y.id)
            } else if content {
                toCheck.append((x, y, .both))
            }
        }
        for (name, y) in fb where fa[name] == nil { markB.insert(y.id) }

        if !toCheck.isEmpty {
            notice = "Porovnávám obsah \(toCheck.count) souborů…"
            let pairs = toCheck.map { ($0.x.url, $0.y.url) }
            let differs: [Bool] = await Task.detached { pairs.map { !LocalFS.sameContent($0.0, $0.1) } }.value
            notice = nil
            for (i, d) in differs.enumerated() where d {
                let c = toCheck[i]
                switch c.side {
                case .a: markA.insert(c.x.id)
                case .b: markB.insert(c.y.id)
                case .both: markA.insert(c.x.id); markB.insert(c.y.id)
                }
            }
        }

        a.marked = markA
        b.marked = markB
        showNotice("Porovnání: vlevo označeno \(markA.count), vpravo \(markB.count) (chybějící, novější nebo jiné). Zkopírujte je klávesou F5.")
    }

    // MARK: Porovnání souborů, součty, atributy

    func startDiff() {
        guard left.connection == nil, right.connection == nil else {
            Dialogs.error("Porovnání souborů funguje jen mezi lokálními soubory.")
            return
        }
        guard let a = left.current, let b = right.current, !a.isDirectory, !b.isDirectory, !a.isParent, !b.isParent else {
            Dialogs.error("Postavte kurzor v obou panelech na soubor, který chcete porovnat.")
            return
        }
        diffFiles = (a.url, b.url)
        sheet = .diff
    }

    func startChecksum() {
        guard active.connection == nil else {
            Dialogs.error("Kontrolní součty se počítají jen z lokálních souborů.")
            return
        }
        let files = active.targets.filter { !$0.isDirectory }
        guard !files.isEmpty else {
            Dialogs.error("Vyberte aspoň jeden soubor.")
            return
        }
        checksumItems = files
        sheet = .checksum
    }

    func startAttributes() {
        guard active.connection == nil, !active.isArchive else {
            Dialogs.error("Atributy lze měnit jen u souborů na lokálním disku.")
            return
        }
        let items = active.targets
        guard !items.isEmpty else { return }
        attributeItems = items
        sheet = .attributes
    }

    func applyAttributes(_ change: LocalFS.AttributeChange, to urls: [URL]) async {
        notice = "Nastavuji atributy…"
        let errors = await Task.detached { LocalFS.apply(change, to: urls) }.value
        notice = nil
        active.reload()
        other.reload()
        if errors.isEmpty { showNotice("Atributy nastaveny.") } else { Dialogs.error(errors.prefix(10).joined(separator: "\n")) }
    }

    func calcDirSizes() {
        let pane = active
        guard pane.connection == nil else { return }
        var dirs = pane.targets.filter { $0.isDirectory }
        if dirs.isEmpty { dirs = pane.items.filter { $0.isDirectory && !$0.isParent } }
        guard !dirs.isEmpty else { return }
        Task { await pane.computeDirSizes(for: dirs) }
    }

    // MARK: Dělení a slepování souborů, odkazy

    private func localDestination(_ src: PaneState) -> URL {
        other.connection == nil && !other.isArchive ? other.url : src.url
    }

    func startSplit() {
        let src = active
        guard src.connection == nil, let item = src.current, !item.isDirectory, !item.isParent else {
            Dialogs.error("Vyberte lokální soubor, který chcete rozdělit.")
            return
        }
        let dir = localDestination(src)
        guard let text = Dialogs.prompt("Rozdělit „\(item.name)“",
                                        info: "Velikost jednoho dílu v MB (např. 100, 700, 4000).\nDíly se uloží do \(dir.path)",
                                        initial: "100", ok: "Rozdělit") else { return }
        guard let mb = Double(text.replacingOccurrences(of: ",", with: ".")), mb >= 0.001 else {
            Dialogs.error("Zadejte velikost dílu jako číslo v MB.")
            return
        }
        let partSize = Int64(mb * 1_048_576)
        let url = item.url
        let name = item.name
        runCancellable {
            let flag = CancelFlag()
            self.progress = 0
            self.progressText = "Dělím \(name)…"
            let report: @Sendable (Double) -> Void = { [weak self] f in
                Task { @MainActor in self?.progress = f }
            }
            do {
                let count = try await withTaskCancellationHandler {
                    try await Task.detached {
                        try LocalFS.split(file: url, partSize: partSize, into: dir, flag: flag, progress: report)
                    }.value
                } onCancel: {
                    flag.set()
                }
                self.showNotice("Soubor rozdělen na \(count) dílů.")
            } catch is CancellationError {
                self.showNotice("Dělení zrušeno.")
            } catch {
                Dialogs.error(error.localizedDescription)
            }
            self.progress = nil
            self.progressText = ""
            src.reload()
            self.other.reload()
        }
    }

    func startCombine() {
        let src = active
        guard src.connection == nil, let item = src.current, !item.isDirectory, !item.isParent,
              item.name.lowercased().hasSuffix(".001") else {
            Dialogs.error("Postavte kurzor na první díl souboru (název končí na .001).")
            return
        }
        let dir = localDestination(src)
        let target = String(item.name.dropLast(4))
        let dest = dir.appendingPathComponent(target)
        if FileManager.default.fileExists(atPath: dest.path) {
            guard Dialogs.confirm("„\(target)“ už existuje. Přepsat?", ok: "Přepsat") else { return }
        }
        let first = item.url
        runCancellable {
            let flag = CancelFlag()
            self.progress = 0
            self.progressText = "Slepuji \(target)…"
            let report: @Sendable (Double) -> Void = { [weak self] f in
                Task { @MainActor in self?.progress = f }
            }
            do {
                try await withTaskCancellationHandler {
                    try await Task.detached {
                        try LocalFS.combine(first: first, to: dest, flag: flag, progress: report)
                    }.value
                } onCancel: {
                    flag.set()
                }
                self.showNotice("Soubor slepen: \(target)")
            } catch is CancellationError {
                self.showNotice("Slepování zrušeno.")
            } catch {
                Dialogs.error(error.localizedDescription)
            }
            self.progress = nil
            self.progressText = ""
            src.reload()
            self.other.reload()
        }
    }

    func makeSymlink() {
        let src = active
        guard src.connection == nil, let item = src.current, !item.isParent else {
            Dialogs.error("Symbolický odkaz jde vytvořit jen na lokální soubor nebo složku.")
            return
        }
        let dir = localDestination(src)
        guard let name = Dialogs.prompt("Symbolický odkaz na „\(item.name)“", info: "Vytvoří se v \(dir.path)",
                                        initial: item.name + " odkaz", ok: "Vytvořit") else { return }
        do {
            try FileManager.default.createSymbolicLink(at: dir.appendingPathComponent(name), withDestinationURL: item.url)
            src.reload()
            other.reload()
        } catch {
            Dialogs.error(error.localizedDescription)
        }
    }

    // MARK: Uživatelské příkazy

    func expandCommand(_ template: String) -> String {
        func quote(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        let pane = active
        let current = pane.current.flatMap { $0.isParent ? nil : $0 }
        var out = ""
        var it = template.makeIterator()
        while let c = it.next() {
            guard c == "%" else { out.append(c); continue }
            guard let n = it.next() else { out.append("%"); break }
            switch n {
            case "f": out += quote(current?.url.path ?? "")
            case "n": out += quote(current?.name ?? "")
            case "d": out += quote(pane.url.path)
            case "o": out += quote((other.connection == nil ? other.url : pane.url).path)
            case "F": out += pane.targets.map { quote($0.url.path) }.joined(separator: " ")
            case "%": out.append("%")
            default: out.append("%"); out.append(n)
            }
        }
        return out
    }

    func runUser(_ cmd: UserCommand) {
        let pane = active
        guard pane.connection == nil else {
            Dialogs.error("Uživatelské příkazy fungují jen v lokálních složkách.")
            return
        }
        let text = expandCommand(cmd.command)
        guard !text.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        showCommandLine = true
        runner.run(text, in: pane.url) { pane.reload() }
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
