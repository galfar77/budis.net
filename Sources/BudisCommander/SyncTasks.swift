import SwiftUI
import AppKit

/// Úloha synchronizace na pozadí: složka se jednosměrně kopíruje do cíle (složka nebo cloud přes rclone).
struct SyncTask: Codable, Identifiable, Equatable {
    var id = UUID().uuidString
    var name = ""
    var source = ""
    /// Místní složka nebo „rclone:název:/cesta“.
    var target = ""
    /// 0 = jen ručně.
    var intervalMinutes = 0
    /// Mazat v cíli soubory, které ve zdroji nejsou (jinak se jen přidává a přepisuje).
    var deleteExtra = false
    /// Obousměrně: nové a novější soubory se kopírují na obě strany, nic se nemaže. (Uloženo jako nepovinná hodnota,
    /// aby šly načíst i úlohy uložené před přidáním této volby.)
    var twoWayStored: Bool? = nil
    var twoWay: Bool {
        get { twoWayStored ?? false }
        set { twoWayStored = newValue }
    }
    var lastRun: Date?
    var lastResult = ""

    func isDue(_ now: Date) -> Bool {
        guard intervalMinutes > 0 else { return false }
        guard let last = lastRun else { return true }
        return now.timeIntervalSince(last) >= Double(intervalMinutes) * 60
    }

    /// Rozebere cíl typu „rclone:název:/cesta“; jinak vrací nil (místní složka).
    static func parseRclone(_ target: String) -> (remote: String, path: String)? {
        guard target.lowercased().hasPrefix("rclone:") else { return nil }
        let rest = String(target.dropFirst("rclone:".count))
        guard let colon = rest.firstIndex(of: ":"), colon != rest.startIndex else { return nil }
        let remote = String(rest[rest.startIndex..<colon])
        var path = String(rest[rest.index(after: colon)...])
        if path.isEmpty { path = "/" } else if !path.hasPrefix("/") { path = "/" + path }
        return (remote, path)
    }
}

@MainActor
final class SyncTaskStore: ObservableObject {
    static let shared = SyncTaskStore()
    private static let key = "syncTasks"

    @Published private(set) var tasks: [SyncTask] = []

    private init() {
        if let data = UserDefaults.standard.data(forKey: Self.key),
           let list = try? JSONDecoder().decode([SyncTask].self, from: data) {
            tasks = list
        }
    }

    func upsert(_ task: SyncTask) {
        if let i = tasks.firstIndex(where: { $0.id == task.id }) { tasks[i] = task } else { tasks.append(task) }
        persist()
    }

    func delete(_ id: String) {
        tasks.removeAll { $0.id == id }
        persist()
    }

    private func persist() {
        if let data = try? JSONEncoder().encode(tasks) { UserDefaults.standard.set(data, forKey: Self.key) }
    }
}

extension AppModel {
    /// Jednou za minutu zkontroluje, jestli některá úloha synchronizace nemá běžet (jen dokud aplikace běží).
    func startSyncScheduler() {
        guard syncTimer == nil else { return }
        syncTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            guard let self else { return }
            Task { @MainActor in await self.checkDueSyncTasks() }
        }
    }

    func checkDueSyncTasks() async {
        guard !syncBusy else { return }
        syncBusy = true
        defer { syncBusy = false }
        for task in SyncTaskStore.shared.tasks where task.isDue(Date()) {
            _ = await runSyncNow(task)
        }
    }

    /// Spustí úlohu synchronizace, zapíše ji do správce přenosů a uloží výsledek.
    @discardableResult
    func runSyncNow(_ task: SyncTask) async -> String {
        nextJobId += 1
        let rec = JobRecord(id: nextJobId, title: L("Synchronizace: \(task.name)"), work: nil)
        rec.state = .running
        rec.started = Date()
        addJob(rec)
        var result: String
        do {
            result = try await runSyncTask(task)
            rec.state = .done
        } catch {
            result = L("Chyba: \(error.localizedDescription)")
            rec.state = .failed
            rec.error = error.localizedDescription
        }
        rec.finished = Date()
        var updated = task
        updated.lastRun = Date()
        updated.lastResult = result
        SyncTaskStore.shared.upsert(updated)
        showNotice(L("Synchronizace „\(task.name)“: \(result)"))
        return result
    }

    /// Zkopíruje soubor, starší verzi na cíli pošle do Koše a zachová čas změny (jinak by se kopie příště považovala za novější).
    nonisolated static func copyKeepingTime(from: String, to: String) throws {
        let fm = FileManager.default
        try fm.createDirectory(atPath: (to as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        if fm.fileExists(atPath: to) { try fm.trashItem(at: URL(fileURLWithPath: to), resultingItemURL: nil) }
        try fm.copyItem(atPath: from, toPath: to)
        if let date = (try? fm.attributesOfItem(atPath: from))?[.modificationDate] as? Date {
            try? fm.setAttributes([.modificationDate: date], ofItemAtPath: to)
        }
    }

    /// Obousměrná synchronizace: nové a novější soubory jdou na obě strany, nic se nemaže.
    private func runTwoWay(source: String, dstRoot: String, dstList: @escaping TreeMirror.Lister,
                           tolerance: TimeInterval, conn: RemoteConnection?) async throws -> String {
        let plan = try await TreeMirror.planTwoWay(a: TreeMirror.localLister(), aRoot: source, b: dstList, bRoot: dstRoot, tolerance: tolerance)
        if plan.isEmpty && plan.conflicts.isEmpty { return L("beze změn") }
        var up = 0, down = 0
        var errors: [String] = []
        for dir in plan.mkdirsA {
            do { try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true) }
            catch { errors.append("\((dir as NSString).lastPathComponent): \(error.localizedDescription)") }
        }
        for dir in plan.mkdirsB {
            do {
                if let conn { try await conn.mkdir(dir) } else { try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true) }
            } catch { errors.append("\((dir as NSString).lastPathComponent): \(error.localizedDescription)") }
        }
        for (from, to, _) in plan.aToB {
            do {
                if let conn { try await conn.upload(local: URL(fileURLWithPath: from), to: to) }
                else { try Self.copyKeepingTime(from: from, to: to) }
                up += 1
            } catch { errors.append("\((from as NSString).lastPathComponent): \(error.localizedDescription)") }
        }
        for (from, to, size) in plan.bToA {
            do {
                if let conn {
                    if FileManager.default.fileExists(atPath: to) { try FileManager.default.trashItem(at: URL(fileURLWithPath: to), resultingItemURL: nil) }
                    try await conn.download(path: from, isDirectory: false, size: size, to: URL(fileURLWithPath: to))
                } else { try Self.copyKeepingTime(from: from, to: to) }
                down += 1
            } catch { errors.append("\((from as NSString).lastPathComponent): \(error.localizedDescription)") }
        }
        let size = ByteCountFormatter.string(fromByteCount: plan.bytes, countStyle: .file)
        var text = L("na cíl \(up), ze cíle \(down) souborů (\(size))")
        if let first = plan.conflicts.first {
            text += L(", konflikty (přeskočeno) \(plan.conflicts.count): \((first as NSString).lastPathComponent)")
        }
        if !errors.isEmpty { text += L(", chyb \(errors.count): \(errors[0])") }
        return text
    }

    /// Provede synchronizaci: zkopíruje nové a změněné soubory, mazání jen když je zapnuté.
    func runSyncTask(_ task: SyncTask) async throws -> String {
        var source = task.source.trimmingCharacters(in: .whitespaces)
        while source.count > 1 && source.hasSuffix("/") { source.removeLast() }
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: source, isDirectory: &isDir), isDir.boolValue else {
            throw RemoteError(message: L("Zdrojová složka „\(source)“ neexistuje."))
        }
        var conn: RemoteConnection?
        let dstRoot: String
        let dstList: TreeMirror.Lister
        let tolerance: TimeInterval
        if let rc = SyncTask.parseRclone(task.target) {
            guard let exe = Rclone.findExecutable(custom: Settings.shared.rclonePath) else {
                throw RemoteError(message: L("Program rclone není nainstalovaný."))
            }
            let c = RemoteConnection(proto: .rclone, host: rc.remote, port: 0, user: "", password: "",
                                     insecure: false, rcloneExe: exe)
            conn = c
            dstRoot = rc.path
            dstList = TreeMirror.remoteLister(c)
            tolerance = 120
            try? await c.mkdir(dstRoot)
        } else {
            var target = task.target.trimmingCharacters(in: .whitespaces)
            while target.count > 1 && target.hasSuffix("/") { target.removeLast() }
            if target == source || target.hasPrefix(source + "/") || source.hasPrefix(target + "/") {
                throw RemoteError(message: L("Zdrojová a cílová složka se nesmí překrývat."))
            }
            try FileManager.default.createDirectory(atPath: target, withIntermediateDirectories: true)
            dstRoot = target
            dstList = TreeMirror.localLister()
            tolerance = 2
        }

        if task.twoWay {
            return try await runTwoWay(source: source, dstRoot: dstRoot, dstList: dstList, tolerance: tolerance, conn: conn)
        }
        let plan = try await TreeMirror.plan(src: TreeMirror.localLister(), srcRoot: source,
                                             dst: dstList, dstRoot: dstRoot, tolerance: tolerance)
        if plan.copies.isEmpty && plan.mkdirs.isEmpty && (!task.deleteExtra || plan.deletes.isEmpty) {
            return L("beze změn")
        }
        var copied = 0, deleted = 0
        var errors: [String] = []
        for dir in plan.mkdirs {
            do {
                if let conn { try await conn.mkdir(dir) } else { try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true) }
            } catch { errors.append("\((dir as NSString).lastPathComponent): \(error.localizedDescription)") }
        }
        for (from, to, _) in plan.copies {
            do {
                if let conn {
                    try await conn.upload(local: URL(fileURLWithPath: from), to: to)
                } else {
                    let fm = FileManager.default
                    try fm.createDirectory(atPath: (to as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
                    if fm.fileExists(atPath: to) { try fm.removeItem(atPath: to) }
                    try fm.copyItem(atPath: from, toPath: to)
                }
                copied += 1
            } catch { errors.append("\((from as NSString).lastPathComponent): \(error.localizedDescription)") }
        }
        if task.deleteExtra {
            for (path, isDirectory) in plan.deletes {
                do {
                    if let conn {
                        try await conn.delete(path: path, isDirectory: isDirectory)
                    } else {
                        try FileManager.default.trashItem(at: URL(fileURLWithPath: path), resultingItemURL: nil)
                    }
                    deleted += 1
                } catch { errors.append("\((path as NSString).lastPathComponent): \(error.localizedDescription)") }
            }
        }
        let size = ByteCountFormatter.string(fromByteCount: plan.bytes, countStyle: .file)
        var text = L("zkopírováno \(copied) souborů (\(size))")
        if task.deleteExtra { text += L(", smazáno \(deleted)") }
        if !errors.isEmpty { text += L(", chyb \(errors.count): \(errors[0])") }
        return text
    }
}

struct SyncTaskEditSheet: View {
    @State var task: SyncTask
    let onSave: (SyncTask) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var error = ""

    private static let intervals: [(String, Int)] = [
        (L("Jen ručně"), 0), (L("Každých 5 minut"), 5), (L("Každých 15 minut"), 15), (L("Každých 30 minut"), 30),
        (L("Každou hodinu"), 60), (L("Každých 6 hodin"), 360), (L("Jednou denně"), 1440),
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(task.name.isEmpty ? L("Nová synchronizace") : L("Upravit synchronizaci")).font(.headline)
            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 8) {
                GridRow { Text(L("Název")); TextField(L("např. Dokumenty na zálohu"), text: $task.name) }
                GridRow {
                    Text(L("Zdroj"))
                    HStack {
                        TextField("", text: $task.source)
                        Button(L("Vybrat…")) { if let p = chooseFolder() { task.source = p } }
                    }
                }
                GridRow {
                    Text(L("Cíl"))
                    HStack {
                        TextField(L("složka, nebo rclone:název:/cesta"), text: $task.target)
                        Button(L("Vybrat…")) { if let p = chooseFolder() { task.target = p } }
                    }
                }
                GridRow {
                    Text(L("Spouštět"))
                    Picker("", selection: $task.intervalMinutes) {
                        ForEach(Self.intervals, id: \.1) { Text(L($0.0)).tag($0.1) }
                    }
                    .labelsHidden()
                }
            }
            .textFieldStyle(.roundedBorder)
            Picker(L("Směr"), selection: $task.twoWay) {
                Text(L("Jednosměrně (zdroj → cíl)")).tag(false)
                Text(L("Obousměrně (nové a novější soubory na obě strany, nic se nemaže)")).tag(true)
            }
            .pickerStyle(.radioGroup)
            .onChange(of: task.twoWay) { _, two in if two { task.deleteExtra = false } }
            Toggle(L("Mazat v cíli soubory, které ve zdroji nejsou"), isOn: $task.deleteExtra)
                .font(.system(size: 12)).disabled(task.twoWay)
            Text(L("Jednosměrně: zdroj se kopíruje do cíle. Cíl v cloudu zapište jako rclone:název:/cesta (úložiště přidáte v menu Nástroje → Cloudová úložiště)."))
                .font(.system(size: 11)).foregroundStyle(.secondary)
            if !error.isEmpty { Text(error).foregroundStyle(.red).font(.system(size: 12)) }
            HStack {
                Spacer()
                Button(L("Zrušit")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("Uložit")) { save() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 560)
    }

    @MainActor
    private func chooseFolder() -> String? {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        return panel.runModal() == .OK ? panel.url?.path : nil
    }

    @MainActor
    private func save() {
        var isDir: ObjCBool = false
        if task.name.trimmingCharacters(in: .whitespaces).isEmpty { error = L("Zadejte název."); return }
        if !FileManager.default.fileExists(atPath: task.source, isDirectory: &isDir) || !isDir.boolValue {
            error = L("Zdrojová složka neexistuje."); return
        }
        if task.target.trimmingCharacters(in: .whitespaces).isEmpty { error = L("Zadejte cíl."); return }
        if task.target.lowercased().hasPrefix("rclone:") && SyncTask.parseRclone(task.target) == nil {
            error = L("Cíl v cloudu zapište jako rclone:název:/cesta."); return
        }
        onSave(task)
        dismiss()
    }
}

struct SyncTasksSheet: View {
    @ObservedObject var model: AppModel
    @ObservedObject var store = SyncTaskStore.shared
    @Environment(\.dismiss) private var dismiss
    @State private var editing: SyncTask?
    @State private var status = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(L("Synchronizace na pozadí")).font(.headline)
                Spacer()
                Button(L("Přidat…")) {
                    var t = SyncTask()
                    if model.active.connection == nil { t.source = model.active.url.path }
                    editing = t
                }
            }
            if store.tasks.isEmpty {
                Text(L("Zatím není žádná úloha. Klikněte na „Přidat…“."))
                    .font(.system(size: 12)).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            } else {
                List {
                    ForEach(store.tasks) { task in
                        HStack {
                            VStack(alignment: .leading, spacing: 1) {
                                Text(task.name)
                                Text("\(task.source) \(task.twoWay ? "⇄" : "→") \(task.target)").font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                                Text(info(task)).font(.system(size: 10)).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button(L("Spustit teď")) {
                                status = L("Synchronizuji…")
                                Task { status = "„\(task.name)“: " + (await model.runSyncNow(task)) }
                            }.controlSize(.small)
                            Button(L("Upravit…")) { editing = task }.controlSize(.small)
                            Button { store.delete(task.id) } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.plain)
                                .help(L("Smazat"))
                        }
                    }
                }
                .frame(height: 280)
            }
            if !status.isEmpty { Text(status).font(.system(size: 11)).foregroundStyle(.secondary) }
            HStack {
                Text(L("Úlohy se spouštějí podle nastavené doby, dokud je aplikace spuštěná. Běh a výsledek je vidět ve správci přenosů."))
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 640)
        .sheet(item: $editing) { task in
            SyncTaskEditSheet(task: task) { store.upsert($0) }
        }
    }

    private func info(_ t: SyncTask) -> String {
        let every = t.intervalMinutes > 0 ? L("každých \(t.intervalMinutes) min") : L("ručně")
        guard let last = t.lastRun else { return every }
        return "\(every) – " + L("naposledy \(last.formatted(date: .abbreviated, time: .shortened)): \(t.lastResult)")
    }
}
