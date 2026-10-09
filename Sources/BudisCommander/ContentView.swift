import SwiftUI
import AppKit
import PDFKit

struct ContentView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var settings = Settings.shared
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                if model.quickView && !model.activeIsLeft {
                    QuickViewPanel(group: model.rightTabs)
                } else {
                    PanelView(group: model.leftTabs, isActive: model.activeIsLeft) { model.activeIsLeft = true }
                }
                Divider()
                if model.quickView && model.activeIsLeft {
                    QuickViewPanel(group: model.leftTabs)
                } else {
                    PanelView(group: model.rightTabs, isActive: !model.activeIsLeft) { model.activeIsLeft = false }
                }
            }
            statusBar
            if model.showCommandLine { CommandPanel(model: model, runner: model.runner) }
            functionBar
            if settings.showButtonBar && !settings.userCommands.isEmpty { userBar }
        }
        .background(WindowAccessor())
        .preferredColorScheme(settings.colorScheme)
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onAppear { focused = true }
        .onChange(of: model.showCommandLine) { _, shown in
            if !shown { focused = true }
        }
        .onKeyPress(phases: [.down, .repeat]) { handle($0) }
        .sheet(item: $model.viewer, onDismiss: { focused = true }) { v in
            ViewerSheet(content: v)
        }
        .sheet(item: $model.sheet, onDismiss: { focused = true }) { sheet in
            switch sheet {
            case .server: ConnectSheet(model: model)
            case .network: NetworkSheet(model: model)
            case .batchRename: BatchRenameSheet(model: model)
            case .search: SearchSheet(model: model)
            case .settings: SettingsSheet()
            case .favorites: FavoritesSheet(model: model)
            case .diff: DiffSheet(model: model)
            case .checksum: ChecksumSheet(model: model)
            case .attributes: AttributesSheet(model: model)
            case .userMenu: UserMenuSheet()
            }
        }
    }

    // MARK: Spodní lišty

    @ViewBuilder
    private var statusBar: some View {
        if model.progress != nil || model.notice != nil {
            HStack {
                if let p = model.progress { ProgressView(value: p).frame(width: 200) }
                Text(model.notice ?? model.progressText).font(.system(size: 11))
                Spacer()
                if model.queueCount > 0 {
                    Text("ve frontě: \(model.queueCount)").font(.system(size: 11)).foregroundStyle(.secondary)
                }
                if model.canCancel && model.progress != nil {
                    Button("Zrušit") { model.cancelTransfer() }.controlSize(.small)
                    if model.queueCount > 0 {
                        Button("Zrušit vše") { model.cancelTransfer(all: true) }.controlSize(.small)
                    }
                }
            }
            .padding(.horizontal, 8)
            .frame(height: 22)
        }
    }

    private var userBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                ForEach(settings.userCommands) { cmd in
                    Button(cmd.name) { model.runUser(cmd); focused = true }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                        .help(cmd.command)
                }
            }
            .padding(.horizontal, 4)
        }
        .frame(height: 28)
        .background(Color.secondary.opacity(0.08))
    }

    private var functionBar: some View {
        HStack(spacing: 2) {
            fnButton("F2", "Přejmenovat") { Task { await model.rename() } }
            fnButton("F3", "Zobrazit") { Task { await model.view() } }
            fnButton("F4", "Editovat") { Task { await model.edit() } }
            fnButton("F5", "Kopírovat") { model.startTransfer(move: false) }
            fnButton("F6", "Přesunout") { model.startTransfer(move: true) }
            fnButton("F7", "Nový adr.") { Task { await model.makeDirectory() } }
            fnButton("F8", "Smazat") { model.startDelete() }
            fnButton("⌘S", "Velikosti") { model.calcDirSizes() }
            fnButton("⌘K", "Server") { model.sheet = .server }
            fnButton("⌘L", "Síť") { model.sheet = .network }
        }
        .padding(4)
        .background(Color.secondary.opacity(0.15))
    }

    private func fnButton(_ key: String, _ title: String, _ action: @escaping () -> Void) -> some View {
        Button {
            action()
            focused = true
        } label: {
            HStack(spacing: 4) {
                Text(key).fontWeight(.bold).foregroundStyle(Color.accentColor)
                Text(title)
            }
            .font(.system(size: 12))
            .frame(maxWidth: .infinity)
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
    }

    // MARK: Klávesnice

    private static func isPrintable(_ s: String) -> Bool {
        guard let scalar = s.unicodeScalars.first else { return false }
        return scalar.value >= 0x20 && !(0xF700...0xF8FF).contains(scalar.value) && scalar.value != 0x7F
    }

    private func handle(_ p: KeyPress) -> KeyPress.Result {
        let pane = model.active

        if p.modifiers.contains(.command) {
            var ch = p.characters.lowercased()
            if ch == ">" { ch = "." }
            if ch.count == 1, let n = Int(ch), n >= 1 {
                model.activeGroup.select(n - 1)
                return .handled
            }
            if let action = settings.action(for: ch) {
                model.perform(action)
                return .handled
            }
            return .ignored
        }

        // Funkční klávesy (F1 = U+F704, …)
        if let s = p.characters.unicodeScalars.first, (0xF704...0xF70F).contains(s.value) {
            switch s.value {
            case 0xF705: Task { await model.rename() }
            case 0xF706: Task { await model.view() }
            case 0xF707: Task { await model.edit() }
            case 0xF708: model.startTransfer(move: false)
            case 0xF709: model.startTransfer(move: true)
            case 0xF70A: Task { await model.makeDirectory() }
            case 0xF70B: model.startDelete()
            default: return .ignored
            }
            return .handled
        }

        // Filtr panelu: Alt + znak ho zahájí, pak se pokračuje psaním; Backspace maže, Esc zruší.
        let hasCmdCtrl = p.modifiers.contains(.command) || p.modifiers.contains(.control)
        if p.modifiers.contains(.option) && !hasCmdCtrl {
            let ch = String(p.key.character)
            if Self.isPrintable(ch) {
                pane.appendFilter(ch)
                return .handled
            }
        }
        if !pane.filter.isEmpty {
            if p.key == .escape { pane.clearFilter(); return .handled }
            if p.key == .delete { pane.deleteFilterChar(); return .handled }
            if !hasCmdCtrl, Self.isPrintable(p.characters) {
                pane.appendFilter(p.characters)
                return .handled
            }
        }

        let shift = p.modifiers.contains(.shift)
        switch p.key {
        case .upArrow:
            if shift { pane.toggleMark(advance: false) }
            pane.move(by: -1); return .handled
        case .downArrow:
            if shift { pane.toggleMark(advance: false) }
            pane.move(by: 1); return .handled
        case .pageUp: pane.move(by: -15); return .handled
        case .pageDown: pane.move(by: 15); return .handled
        case .home: pane.moveTo(0); return .handled
        case .end: pane.moveTo(pane.items.count - 1); return .handled
        case .return: pane.enter(); return .handled
        case .rightArrow where pane.current?.isDirectory == true: pane.enter(); return .handled
        case .leftArrow, .delete: pane.goUp(); return .handled
        case .tab:
            if p.modifiers.contains(.control) { model.activeGroup.cycle(shift ? -1 : 1) } else { model.switchPane() }
            return .handled
        case .space: pane.toggleMark(); return .handled
        default: break
        }

        switch p.characters {
        case "+": model.markByMask(on: true); return .handled
        case "-": model.markByMask(on: false); return .handled
        case "*":
            pane.markAll(); return .handled
        default: break
        }

        if p.modifiers.isDisjoint(with: [.control, .option]),
           let s = p.characters.unicodeScalars.first, s.value >= 0x20, !(0xF700...0xF8FF).contains(s.value) {
            pane.quickSearch(p.characters)
            return .handled
        }
        return .ignored
    }
}

struct ViewerSheet: View {
    let content: ViewerContent
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(content.title).font(.headline)
                Spacer()
                if let url = content.openURL {
                    Button("Otevřít v aplikaci") { NSWorkspace.shared.open(url) }
                }
                Button("Tisk…") { ViewerPrinter.printContent(content) }.keyboardShortcut("p", modifiers: .command)
                Button("Zavřít") { dismiss() }.keyboardShortcut(.cancelAction)
            }
            .padding(10)
            Divider()
            switch content.kind {
            case .text(let text):
                ScrollView([.vertical, .horizontal]) {
                    Text(text)
                        .font(.system(size: 12, design: .monospaced))
                        .textSelection(.enabled)
                        .padding(10)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            case .image(let image):
                ScrollView([.vertical, .horizontal]) {
                    Image(nsImage: image)
                        .resizable()
                        .scaledToFit()
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .padding(10)
                }
            case .pdf(let url):
                PDFViewer(url: url)
            }
        }
        .frame(minWidth: 800, minHeight: 600)
    }
}

struct PDFViewer: NSViewRepresentable {
    let url: URL

    func makeNSView(context: Context) -> PDFView {
        let view = PDFView()
        view.autoScales = true
        view.document = PDFDocument(url: url)
        return view
    }

    func updateNSView(_ nsView: PDFView, context: Context) {}
}

/// Zajistí, že si okno pamatuje svou polohu a velikost.
struct WindowAccessor: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { view.window?.setFrameAutosaveName("BudisCommanderMain") }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}
