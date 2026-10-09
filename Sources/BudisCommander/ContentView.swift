import SwiftUI
import AppKit

struct ContentView: View {
    @StateObject private var model = AppModel()
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                PanelView(group: model.leftTabs, isActive: model.activeIsLeft) { model.activeIsLeft = true }
                Divider()
                PanelView(group: model.rightTabs, isActive: !model.activeIsLeft) { model.activeIsLeft = false }
            }
            statusBar
            functionBar
        }
        .background(WindowAccessor())
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onAppear { focused = true }
        .onKeyPress(phases: [.down, .repeat]) { handle($0) }
        .sheet(item: $model.viewer, onDismiss: { focused = true }) { v in
            ViewerSheet(content: v)
        }
        .sheet(item: $model.sheet, onDismiss: { focused = true }) { sheet in
            switch sheet {
            case .server: ConnectSheet(model: model)
            case .network: NetworkSheet(model: model)
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
            }
            .padding(.horizontal, 8)
            .frame(height: 22)
        }
    }

    private var functionBar: some View {
        HStack(spacing: 2) {
            fnButton("F2", "Přejmenovat") { Task { await model.rename() } }
            fnButton("F3", "Zobrazit") { Task { await model.view() } }
            fnButton("F4", "Editovat") { Task { await model.edit() } }
            fnButton("F5", "Kopírovat") { Task { await model.transfer(move: false); focused = true } }
            fnButton("F6", "Přesunout") { Task { await model.transfer(move: true); focused = true } }
            fnButton("F7", "Nový adr.") { Task { await model.makeDirectory() } }
            fnButton("F8", "Smazat") { Task { await model.delete(); focused = true } }
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

    private func handle(_ p: KeyPress) -> KeyPress.Result {
        let pane = model.active

        if p.modifiers.contains(.command) {
            switch p.characters.lowercased() {
            case "r": pane.reload(); return .handled
            case "a": pane.markAll(); return .handled
            case ".", ">": pane.toggleHidden(); return .handled
            case "t": model.activeGroup.newTab(); return .handled
            case "w": model.activeGroup.close(model.activeGroup.selected); return .handled
            case "1", "2", "3", "4", "5", "6", "7", "8", "9":
                model.activeGroup.select((Int(p.characters) ?? 1) - 1); return .handled
            case "k": model.sheet = .server; return .handled
            case "l": model.sheet = .network; return .handled
            case "u": model.activeIsLeft ? model.right.navigate(to: model.left.url) : model.left.navigate(to: model.right.url); return .handled
            default: return .ignored
            }
        }

        // Funkční klávesy (F1 = U+F704, …)
        if let s = p.characters.unicodeScalars.first, (0xF704...0xF70F).contains(s.value) {
            switch s.value {
            case 0xF705: Task { await model.rename() }
            case 0xF706: Task { await model.view() }
            case 0xF707: Task { await model.edit() }
            case 0xF708: Task { await model.transfer(move: false) }
            case 0xF709: Task { await model.transfer(move: true) }
            case 0xF70A: Task { await model.makeDirectory() }
            case 0xF70B: Task { await model.delete() }
            default: return .ignored
            }
            return .handled
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
                Button("Zavřít") { dismiss() }.keyboardShortcut(.cancelAction)
            }
            .padding(10)
            Divider()
            ScrollView([.vertical, .horizontal]) {
                Text(content.text)
                    .font(.system(size: 12, design: .monospaced))
                    .textSelection(.enabled)
                    .padding(10)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .frame(minWidth: 700, minHeight: 500)
    }
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
