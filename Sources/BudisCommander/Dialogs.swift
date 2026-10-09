import AppKit

enum ConflictChoice { case overwrite, overwriteAll, skip, cancel }

@MainActor
enum Dialogs {
    static func error(_ message: String) {
        let a = NSAlert()
        a.alertStyle = .warning
        a.messageText = "Chyba"
        a.informativeText = message
        a.addButton(withTitle: "OK")
        a.runModal()
    }

    static func confirm(_ title: String, info: String = "", ok: String) -> Bool {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = info
        a.addButton(withTitle: ok)
        a.addButton(withTitle: "Zrušit")
        return a.runModal() == .alertFirstButtonReturn
    }

    static func prompt(_ title: String, info: String = "", initial: String = "", ok: String = "OK") -> String? {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = info
        a.addButton(withTitle: ok)
        a.addButton(withTitle: "Zrušit")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 280, height: 24))
        field.stringValue = initial
        a.accessoryView = field
        a.window.initialFirstResponder = field
        guard a.runModal() == .alertFirstButtonReturn else { return nil }
        let value = field.stringValue.trimmingCharacters(in: .whitespaces)
        return value.isEmpty ? nil : value
    }

    static func conflict(_ name: String) -> ConflictChoice {
        let a = NSAlert()
        a.messageText = "„\(name)“ již v cíli existuje"
        a.informativeText = "Chcete existující položku přepsat?"
        a.addButton(withTitle: "Přepsat")
        a.addButton(withTitle: "Přepsat vše")
        a.addButton(withTitle: "Přeskočit")
        a.addButton(withTitle: "Zrušit")
        switch a.runModal() {
        case .alertFirstButtonReturn: return .overwrite
        case .alertSecondButtonReturn: return .overwriteAll
        case .alertThirdButtonReturn: return .skip
        default: return .cancel
        }
    }
}
