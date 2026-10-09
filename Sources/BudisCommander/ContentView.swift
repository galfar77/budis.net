import SwiftUI

struct ContentView: View {
    @StateObject private var model = AppModel()
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                PaneView(pane: model.left, isActive: model.activeIsLeft) { model.activeIsLeft = true }
                Divider()
                PaneView(pane: model.right, isActive: !model.activeIsLeft) { model.activeIsLeft = false }
            }
            statusBar
            functionBar
        }
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onAppear { focused = true }
        .onKeyPress(phases: [.down, .repeat]) { handle($0) }
        .sheet(item: $model.viewer, onDismiss: { focused = true }) { v in
            ViewerSheet(content: v)
        }
    }

    // MARK: Spodní lišty

    @ViewBuilder
    private var statusBar: some View {
        if let p = model.progress {
            HStack {
                ProgressView(value: p).frame(width: 200)
                Text(model.progressText).font(.system(size: 11))
                Spacer()
            }
            .padding(.horizontal, 8)
            .frame(height: 22)
        }
    }

    private var functionBar: some View {
        HStack(spacing: 2) {
            fnButton("F2", "Přejmenovat") { model.rename() }
            fnButton("F3", "Zobrazit") { model.view() }
            fnButton("F4", "Editovat") { model.edit() }
            fnButton("F5", "Kopírovat") { Task { await model.transfer(move: false); focused = true } }
            fnButton("F6", "Přesunout") { Task { await model.transfer(move: true); focused = true } }
            fnButton("F7", "Nový adr.") { model.makeDirectory() }
            fnButton("F8", "Smazat") { Task { await model.delete(); focused = true } }
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
            case "u": model.activeIsLeft ? model.right.navigate(to: model.left.url) : model.left.navigate(to: model.right.url); return .handled
            default: return .ignored
            }
        }

        // Funkční klávesy (F1 = U+F704, …)
        if let s = p.characters.unicodeScalars.first, (0xF704...0xF70F).contains(s.value) {
            switch s.value {
            case 0xF705: model.rename()
            case 0xF706: model.view()
            case 0xF707: model.edit()
            case 0xF708: Task { await model.transfer(move: false) }
            case 0xF709: Task { await model.transfer(move: true) }
            case 0xF70A: model.makeDirectory()
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
        case .tab: model.switchPane(); return .handled
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
