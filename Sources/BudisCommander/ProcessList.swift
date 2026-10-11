import SwiftUI
import Darwin

struct ProcInfo: Identifiable, Equatable {
    let pid: Int32
    let name: String
    let cpu: Double
    let rssKB: Int64

    var id: Int32 { pid }
    var memoryText: String { ByteCountFormatter.string(fromByteCount: rssKB * 1024, countStyle: .memory) }
}

/// Seznam běžících procesů (příkaz ps) a jejich ukončení (správce procesů).
enum ProcessList {
    /// Zpracuje výstup `ps -axo pid=,pcpu=,rss=,comm=`.
    static func parse(_ text: String) -> [ProcInfo] {
        var out: [ProcInfo] = []
        for raw in text.split(whereSeparator: \.isNewline) {
            var rest = Substring(raw.trimmingCharacters(in: .whitespaces))
            func token() -> String? {
                guard let end = rest.firstIndex(of: " ") else { return nil }
                let t = String(rest[rest.startIndex..<end])
                rest = Substring(rest[end...].drop(while: { $0 == " " }))
                return t
            }
            guard let p = token(), let pid = Int32(p),
                  let c = token(), let cpu = Double(c.replacingOccurrences(of: ",", with: ".")),
                  let r = token(), let rss = Int64(r), !rest.isEmpty else { continue }
            let name = (String(rest) as NSString).lastPathComponent
            out.append(ProcInfo(pid: pid, name: name, cpu: cpu, rssKB: rss))
        }
        return out
    }

    static func snapshot() async -> [ProcInfo] {
        await Task.detached { () -> [ProcInfo] in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/bin/ps")
            p.arguments = ["-axo", "pid=,pcpu=,rss=,comm="]
            let pipe = Pipe()
            p.standardOutput = pipe
            p.standardError = FileHandle.nullDevice
            do { try p.run() } catch { return [] }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            return parse(String(decoding: data, as: UTF8.self))
        }.value
    }

    enum SortMode { case name, cpu, memory }

    static func filter(_ all: [ProcInfo], text: String, sort: SortMode) -> [ProcInfo] {
        let q = text.trimmingCharacters(in: .whitespaces)
        let list = q.isEmpty ? all : all.filter { $0.name.localizedCaseInsensitiveContains(q) || String($0.pid) == q }
        switch sort {
        case .name: return list.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        case .cpu: return list.sorted { $0.cpu > $1.cpu }
        case .memory: return list.sorted { $0.rssKB > $1.rssKB }
        }
    }

    /// Pošle procesu SIGTERM (nebo SIGKILL při vynucení); vrací chybu nebo nil.
    static func kill(pid: Int32, force: Bool) -> String? {
        if Darwin.kill(pid, force ? SIGKILL : SIGTERM) == 0 { return nil }
        return String(cString: strerror(errno))
    }
}

struct ProcessSheet: View {
    @Environment(\.dismiss) private var dismiss
    @State private var all: [ProcInfo] = []
    @State private var query = ""
    @State private var sort: ProcessList.SortMode = .cpu
    @State private var selection: Int32?

    private var shown: [ProcInfo] { ProcessList.filter(all, text: query, sort: sort) }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Správce procesů")).font(.headline)
            HStack {
                TextField(L("hledat podle názvu nebo PID"), text: $query).textFieldStyle(.roundedBorder)
                Picker("", selection: $sort) {
                    Text(L("CPU")).tag(ProcessList.SortMode.cpu)
                    Text(L("Paměť")).tag(ProcessList.SortMode.memory)
                    Text(L("Název")).tag(ProcessList.SortMode.name)
                }
                .pickerStyle(.segmented)
                .frame(width: 200)
            }
            List(shown, selection: $selection) { p in
                HStack {
                    Text(String(p.pid)).frame(width: 60, alignment: .trailing).foregroundStyle(.secondary)
                    Text(p.name).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                    Text(String(format: "%.1f %%", p.cpu)).frame(width: 60, alignment: .trailing)
                    Text(p.memoryText).frame(width: 80, alignment: .trailing)
                }
                .font(.system(size: 12, design: .monospaced))
            }
            .frame(height: 340)
            HStack {
                Text(L("\(shown.count) z \(all.count) procesů")).font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button(L("Obnovit")) { Task { await reload() } }
                Button(L("Vynutit ukončení")) { end(force: true) }.disabled(selection == nil)
                Button(L("Ukončit")) { end(force: false) }.disabled(selection == nil)
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 600)
        .task { await reload() }
    }

    private func reload() async { all = await ProcessList.snapshot() }

    @MainActor
    private func end(force: Bool) {
        guard let pid = selection, let p = all.first(where: { $0.pid == pid }) else { return }
        let info = force ? L("Proces bude okamžitě ukončen. Neuložená práce se ztratí.") : L("Procesu se pošle požadavek na ukončení.")
        guard Dialogs.confirm(L("Ukončit „\(p.name)“ (PID \(p.pid))?"), info: info, ok: force ? L("Vynutit ukončení") : L("Ukončit")) else { return }
        if let error = ProcessList.kill(pid: pid, force: force) { Dialogs.error(error) }
        Task {
            try? await Task.sleep(nanoseconds: 400_000_000)
            await reload()
        }
    }
}
