import SwiftUI

/// Spouští příkazy shellu v adresáři panelu a ukazuje jejich výstup.
final class CommandRunner: ObservableObject {
    @Published var output = ""
    @Published var running = false
    private var process: Process?
    private static let limit = 200_000

    func run(_ command: String, in dir: URL, completion: @escaping () -> Void) {
        guard !running else { return }
        output = "$ \(command)\n"
        running = true

        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/zsh")
        p.arguments = ["-l", "-c", command]
        p.currentDirectoryURL = dir
        p.standardInput = FileHandle.nullDevice
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe

        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty { return }
            let text = String(decoding: data, as: UTF8.self)
            DispatchQueue.main.async { self?.append(text) }
        }
        p.terminationHandler = { [weak self] proc in
            pipe.fileHandleForReading.readabilityHandler = nil
            let rest = pipe.fileHandleForReading.readDataToEndOfFile()
            DispatchQueue.main.async {
                if !rest.isEmpty { self?.append(String(decoding: rest, as: UTF8.self)) }
                self?.append(proc.terminationStatus == 0 ? "" : "\n[kód \(proc.terminationStatus)]\n")
                self?.running = false
                self?.process = nil
                completion()
            }
        }
        do {
            try p.run()
            process = p
        } catch {
            output += "\(error.localizedDescription)\n"
            running = false
        }
    }

    func cancel() { process?.terminate() }

    private func append(_ text: String) {
        output += text
        if output.count > Self.limit { output = String(output.suffix(Self.limit)) }
    }
}

struct CommandPanel: View {
    @ObservedObject var model: AppModel
    @ObservedObject var runner: CommandRunner
    @State private var command = ""
    @FocusState private var fieldFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            if !runner.output.isEmpty {
                ScrollViewReader { proxy in
                    ScrollView {
                        Text(runner.output)
                            .font(.system(size: 11, design: .monospaced))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(6)
                        Color.clear.frame(height: 1).id("end")
                    }
                    .frame(height: 130)
                    .onChange(of: runner.output) { _, _ in proxy.scrollTo("end") }
                }
                .background(Color.secondary.opacity(0.08))
            }
            HStack(spacing: 6) {
                Text(cwdLabel).font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.head).frame(maxWidth: 220, alignment: .leading)
                Text("$").font(.system(size: 12, design: .monospaced))
                TextField(L("příkaz (Enter spustí, Esc zavře)"), text: $command)
                    .textFieldStyle(.plain)
                    .font(.system(size: 12, design: .monospaced))
                    .focused($fieldFocused)
                    .onSubmit(execute)
                    .onKeyPress(.escape) {
                        model.showCommandLine = false
                        return .handled
                    }
                if runner.running { Button(L("Zastavit")) { runner.cancel() }.controlSize(.small) }
                Button { model.showCommandLine = false } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain)
            }
            .padding(.horizontal, 8)
            .frame(height: 26)
            .background(Color.secondary.opacity(0.12))
        }
        .onAppear { fieldFocused = true }
    }

    private var cwdLabel: String {
        model.active.connection == nil ? model.active.url.path : L("(jen lokální složky)")
    }

    private func execute() {
        let cmd = command.trimmingCharacters(in: .whitespaces)
        guard !cmd.isEmpty else { return }
        guard model.active.connection == nil else {
            runner.output = L("Příkazy fungují jen v lokální složce.\n")
            return
        }
        command = ""
        let pane = model.active
        runner.run(cmd, in: pane.url) { pane.reload() }
    }
}
