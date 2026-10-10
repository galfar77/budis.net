import Foundation

/// Položka složky pro porovnání (místní i na serveru).
struct MirrorEntry {
    let name: String
    let isDir: Bool
    let size: Int64
    let date: Date?
}

/// Plán zrcadlení mezi místní složkou a serverem.
struct MirrorPlan {
    var mkdirs: [String] = []
    var copies: [(from: String, to: String, size: Int64)] = []
    var deletes: [(path: String, isDir: Bool)] = []
    var bytes: Int64 = 0

    var isEmpty: Bool { mkdirs.isEmpty && copies.isEmpty && deletes.isEmpty }
}

enum TreeMirror {
    typealias Lister = @Sendable (String) async throws -> [MirrorEntry]

    static func localLister() -> Lister {
        { path in
            let keys: [URLResourceKey] = [.isDirectoryKey, .fileSizeKey, .contentModificationDateKey]
            guard let urls = try? FileManager.default.contentsOfDirectory(
                at: URL(fileURLWithPath: path), includingPropertiesForKeys: keys, options: []) else { return [] }
            var out: [MirrorEntry] = []
            for u in urls where u.lastPathComponent != ".DS_Store" {
                let v = try? u.resourceValues(forKeys: Set(keys))
                out.append(MirrorEntry(name: u.lastPathComponent, isDir: v?.isDirectory ?? false,
                                       size: Int64(v?.fileSize ?? 0), date: v?.contentModificationDate))
            }
            return out
        }
    }

    static func remoteLister(_ conn: RemoteConnection) -> Lister {
        { path in
            let items = (try? await conn.list(path)) ?? []
            return items.filter { !$0.isParent }.map {
                MirrorEntry(name: $0.name, isDir: $0.isDirectory, size: $0.size, date: $0.modified)
            }
        }
    }

    /// Spočítá, co zkopírovat ze zdroje do cíle a co v cíli přebývá. Server si čas souboru po nahrání nastaví
    /// po svém, proto se kopíruje, jen když se liší velikost nebo je zdroj novější než cíl o víc než `tolerance` sekund.
    static func plan(src: @escaping Lister, srcRoot: String, dst: @escaping Lister, dstRoot: String,
                     tolerance: TimeInterval) async throws -> MirrorPlan {
        var plan = MirrorPlan()
        try await walk(src, srcRoot, dst, dstRoot, true, tolerance, &plan)
        return plan
    }

    private static func walk(_ src: Lister, _ srcDir: String, _ dst: Lister, _ dstDir: String, _ dstExists: Bool,
                             _ tol: TimeInterval, _ plan: inout MirrorPlan) async throws {
        try Task.checkCancellation()
        let s = try await src(srcDir)
        var d: [MirrorEntry] = []
        if dstExists { d = try await dst(dstDir) }
        var dmap: [String: MirrorEntry] = [:]
        for e in d { dmap[e.name] = e }
        let sNames = Set(s.map { $0.name })

        for a in s {
            let from = (srcDir as NSString).appendingPathComponent(a.name)
            let to = (dstDir as NSString).appendingPathComponent(a.name)
            let b = dmap[a.name]
            if a.isDir {
                let exists = b?.isDir == true
                if !exists {
                    if b != nil { plan.deletes.append((path: to, isDir: false)) }
                    plan.mkdirs.append(to)
                }
                try await walk(src, from, dst, to, exists, tol, &plan)
            } else {
                if let b, !b.isDir {
                    var newer = false
                    if let da = a.date, let db = b.date { newer = da.timeIntervalSince(db) > tol }
                    if a.size == b.size && !newer { continue }
                } else if b != nil {
                    plan.deletes.append((path: to, isDir: true))
                }
                plan.copies.append((from: from, to: to, size: a.size))
                plan.bytes += a.size
            }
        }
        for b in d where !sNames.contains(b.name) {
            plan.deletes.append((path: (dstDir as NSString).appendingPathComponent(b.name), isDir: b.isDir))
        }
    }
}

extension AppModel {
    /// Zrcadlení mezi místní složkou a serverem (jeden z panelů je server): udělá z neaktivního panelu kopii aktivního.
    func syncWithServer(src: PaneState, dst: PaneState) async {
        guard !src.isArchive, !dst.isArchive else {
            Dialogs.error("Archiv se zrcadlit nedá.")
            return
        }
        let upload = dst.connection != nil
        guard let conn = upload ? dst.connection : src.connection else { return }
        let srcRoot = src.connection != nil ? src.remotePath : src.url.path
        let dstRoot = dst.connection != nil ? dst.remotePath : dst.url.path
        let srcList = src.connection != nil ? TreeMirror.remoteLister(conn) : TreeMirror.localLister()
        let dstList = dst.connection != nil ? TreeMirror.remoteLister(conn) : TreeMirror.localLister()

        notice = "Porovnávám se serverem…"
        let plan: MirrorPlan
        do {
            plan = try await TreeMirror.plan(src: srcList, srcRoot: srcRoot, dst: dstList, dstRoot: dstRoot, tolerance: 120)
        } catch {
            notice = nil
            Dialogs.error(error.localizedDescription)
            return
        }
        notice = nil
        guard !plan.isEmpty else {
            showNotice("Složky jsou shodné, není co zrcadlit.")
            return
        }
        let size = ByteCountFormatter.string(fromByteCount: plan.bytes, countStyle: .file)
        var info = "Kopírovat nebo přepsat: \(plan.copies.count) souborů (\(size))\nVytvořit složek: \(plan.mkdirs.count)\n"
        info += upload ? "Smazat na serveru: \(plan.deletes.count)" : "Přesunout do koše (jen v cíli): \(plan.deletes.count)"
        let preview = plan.deletes.prefix(6).map { "  • " + ($0.path as NSString).lastPathComponent }
        if !preview.isEmpty { info += "\n" + preview.joined(separator: "\n") + (plan.deletes.count > 6 ? "\n  …" : "") }
        info += "\n\nKopírují se soubory s jinou velikostí nebo novější než v cíli."
        let title = upload ? "Nahrát změny na server?" : "Stáhnout změny ze serveru?"
        guard Dialogs.confirm(title, info: info, ok: upload ? "Nahrát" : "Stáhnout") else { return }

        var errors: [String] = []
        let total = plan.deletes.count + plan.mkdirs.count + plan.copies.count
        var step = 0
        func tick(_ text: String) {
            progress = Double(step) / Double(max(total, 1))
            progressText = text
            step += 1
        }
        for (path, isDir) in plan.deletes {
            if Task.isCancelled { break }
            let name = (path as NSString).lastPathComponent
            tick((upload ? "Mažu: " : "Do koše: ") + name)
            do {
                if upload {
                    try await conn.delete(path: path, isDirectory: isDir)
                } else {
                    try await Task.detached {
                        try FileManager.default.trashItem(at: URL(fileURLWithPath: path), resultingItemURL: nil)
                    }.value
                }
            } catch {
                errors.append("\(name): \(error.localizedDescription)")
            }
        }
        for path in plan.mkdirs {
            if Task.isCancelled { break }
            let name = (path as NSString).lastPathComponent
            tick("Složka: " + name)
            do {
                if upload {
                    try await conn.mkdir(path)
                } else {
                    try FileManager.default.createDirectory(atPath: path, withIntermediateDirectories: true)
                }
            } catch {
                errors.append("\(name): \(error.localizedDescription)")
            }
        }
        let totalBytes = plan.bytes
        var done: Int64 = 0
        for (from, to, fileSize) in plan.copies {
            if Task.isCancelled { break }
            let name = (from as NSString).lastPathComponent
            let verb = upload ? "Nahrávám: " : "Stahuji: "
            tick(verb + name)
            let base = done
            let report: @Sendable (String, Double) -> Void = { [weak self] fileName, fraction in
                Task { @MainActor in
                    guard let self else { return }
                    self.progress = totalBytes > 0 ? (Double(base) + fraction * Double(fileSize)) / Double(totalBytes) : nil
                    self.progressText = verb + fileName
                }
            }
            do {
                if upload {
                    try await conn.upload(local: URL(fileURLWithPath: from), to: to, progress: report)
                } else {
                    try FileManager.default.createDirectory(atPath: (to as NSString).deletingLastPathComponent,
                                                            withIntermediateDirectories: true)
                    try await conn.download(path: from, isDirectory: false, size: fileSize,
                                            to: URL(fileURLWithPath: to), progress: report)
                }
            } catch is CancellationError {
                break
            } catch {
                errors.append("\(name): \(error.localizedDescription)")
            }
            done += fileSize
        }
        progress = nil
        progressText = ""
        src.reload()
        dst.reload()
        showNotice(Task.isCancelled ? "Zrcadlení zrušeno." : "Zrcadlení hotovo.")
        if !errors.isEmpty { Dialogs.error(errors.prefix(12).joined(separator: "\n")) }
    }
}
