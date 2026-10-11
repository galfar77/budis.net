import SwiftUI
import AppKit
import SwiftTerm

/// Drží jedno trvale běžící sezení terminálu, aby přežilo skrytí a znovu zobrazení panelu.
@MainActor
final class TerminalHost {
    static let shared = TerminalHost()
    private(set) var view: LocalProcessTerminalView?

    /// Vrátí terminál; při prvním použití v něm spustí přihlašovací shell ve složce `directory`.
    func ensure(directory: String) -> LocalProcessTerminalView {
        if let view { return view }
        let v = LocalProcessTerminalView(frame: NSRect(x: 0, y: 0, width: 800, height: 240))
        var env = ProcessInfo.processInfo.environment
        env["TERM"] = "xterm-256color"
        env["COLORTERM"] = "truecolor"
        env["BUDIS_CWD"] = directory
        let shell = env["SHELL"] ?? "/bin/zsh"
        env["SHELL"] = shell
        v.startProcess(executable: shell,
                       args: ["-l", "-c", "cd \"$BUDIS_CWD\" 2>/dev/null; exec \"$SHELL\" -l"],
                       environment: env.map { "\($0.key)=\($0.value)" },
                       execName: nil)
        view = v
        return v
    }

    /// Pošle do terminálu příkaz `cd` do dané složky.
    func cd(_ path: String) {
        let quoted = "'" + path.replacingOccurrences(of: "'", with: "'\\''") + "'"
        view?.send(txt: "cd \(quoted)\r")
    }
}

struct TerminalViewRepresentable: NSViewRepresentable {
    let directory: String

    func makeNSView(context: Context) -> LocalProcessTerminalView {
        let v = TerminalHost.shared.ensure(directory: directory)
        DispatchQueue.main.async { v.window?.makeFirstResponder(v) }
        return v
    }

    func updateNSView(_ nsView: LocalProcessTerminalView, context: Context) {}
}

/// Vestavěný terminál dole pod panely (plnohodnotný, i pro ssh, vim a podobné programy).
struct TerminalPanel: View {
    @ObservedObject var model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            Divider()
            HStack {
                Text(L("Terminál")).font(.system(size: 11, weight: .semibold))
                Spacer()
                Button(L("Přejít do složky panelu")) { TerminalHost.shared.cd(model.active.url.path) }
                    .controlSize(.small)
                    .disabled(model.active.connection != nil)
                Button { model.showTerminal = false } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain)
                    .help(L("Skrýt terminál"))
            }
            .padding(.horizontal, 8)
            .frame(height: 22)
            TerminalViewRepresentable(directory: model.active.url.path)
                .frame(height: 240)
        }
    }
}
