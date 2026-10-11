import SwiftUI

enum JobState { case queued, running, done, cancelled, failed }

/// Záznam o úloze ve správci přenosů.
@MainActor
final class JobRecord: ObservableObject, Identifiable {
    let id: Int
    let title: String
    let work: (@MainActor () async -> Void)?
    let added = Date()
    @Published var state: JobState = .queued
    @Published var started: Date?
    @Published var finished: Date?
    @Published var error: String?

    init(id: Int, title: String, work: (@MainActor () async -> Void)?) {
        self.id = id
        self.title = title
        self.work = work
    }

    var line: String {
        let icon: String
        let stateText: String
        switch state {
        case .running: icon = "▶"; stateText = L("probíhá")
        case .queued: icon = "⏳"; stateText = L("ve frontě")
        case .done: icon = "✔"; stateText = L("hotovo")
        case .cancelled: icon = "⏹"; stateText = L("zrušeno")
        case .failed: icon = "⚠"; stateText = L("chyba")
        }
        let when = (finished ?? started ?? added).formatted(date: .omitted, time: .shortened)
        return "\(icon) \(when)  \(stateText)  \(L(title))" + (error.map { "  – \($0)" } ?? "")
    }
}

extension AppModel {
    /// Zruší úlohu podle čísla: běžící se přeruší, úloha ve frontě se odebere.
    func cancelJob(id: Int) {
        if runningJob?.id == id { transferTask?.cancel(); return }
        guard let index = queue.firstIndex(where: { $0.record.id == id }) else { return }
        let rec = queue.remove(at: index).record
        queueCount = queue.count
        rec.state = .cancelled
        rec.finished = Date()
        objectWillChange.send()
    }

    /// Spustí znovu dokončenou, zrušenou nebo selhanou úlohu.
    func retryJob(id: Int) {
        guard let old = jobs.first(where: { $0.id == id }), let work = old.work,
              old.state != .running, old.state != .queued else { return }
        runCancellable(queue: true, title: old.title, work)
    }

    func clearFinishedJobs() {
        jobs.removeAll { $0.state == .done || $0.state == .cancelled || $0.state == .failed }
    }

    func transferTitle(_ plan: TransferPlan) -> String {
        "\(plan.move ? L("Přesun") : L("Kopírování")): \(describe(plan.items)) → \(plan.dst.title)"
    }
}

struct JobRow: View {
    @ObservedObject var job: JobRecord
    let model: AppModel

    var body: some View {
        HStack {
            Text(job.line).lineLimit(1).truncationMode(.middle)
            Spacer()
            if job.state == .running || job.state == .queued {
                Button(L("Zrušit")) { model.cancelJob(id: job.id) }.controlSize(.small)
            } else if job.work != nil {
                Button(L("Spustit znovu")) { model.retryJob(id: job.id) }.controlSize(.small)
            }
        }
    }
}

struct TransfersSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("Správce přenosů")).font(.headline)
            if let p = model.progress { ProgressView(value: p) }
            if model.canCancel { Text(model.progressText).font(.system(size: 11)).foregroundStyle(.secondary) }
            if model.jobs.isEmpty {
                Text(L("Zatím žádné přenosy."))
                    .font(.system(size: 12)).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            } else {
                List(model.jobs) { job in JobRow(job: job, model: model) }
                    .frame(height: 300)
            }
            HStack {
                Button(L("Zrušit vše")) { model.cancelTransfer(all: true) }
                Button(L("Vyčistit hotové")) { model.clearFinishedJobs() }
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
            Text(L("Nové přenosy (F5, F6) se řadí do fronty a běží postupně. Zrušenou nebo selhanou úlohu lze spustit znovu."))
                .font(.system(size: 11)).foregroundStyle(.secondary)
        }
        .padding(16)
        .frame(width: 640)
    }
}
