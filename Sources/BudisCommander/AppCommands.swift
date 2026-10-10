import SwiftUI

/// Položky menu se zkratkami; kopírují nastavení z `Settings`.
struct AppCommands: Commands {
    @ObservedObject var model: AppModel
    @ObservedObject var settings = Settings.shared

    private func title(_ action: ShortcutAction) -> String {
        if action == .undo, let t = model.undoTitle { return "Vrátit: \(t)" }
        return action.title
    }

    private func item(_ action: ShortcutAction) -> some View {
        let ch = settings.char(for: action)
        return Group {
            if let c = ch.first {
                Button(title(action)) { model.perform(action) }
                    .keyboardShortcut(KeyEquivalent(c), modifiers: .command)
            } else {
                Button(title(action)) { model.perform(action) }
            }
        }
        // Při otevřeném dialogu zkratky nic nedělají (v dialogu mají vlastní, třeba ⌘P pro tisk).
        .disabled(model.sheet != nil || model.viewer != nil)
    }

    var body: some Commands {
        // Nahradí systémové „Zavřít okno“ (⌘W), aby zavíralo záložku.
        CommandGroup(replacing: .saveItem) {
            item(.closeTab)
        }
        CommandGroup(replacing: .newItem) {
            item(.newTab)
        }
        CommandMenu("Nástroje") {
            item(.undo)
            Divider()
            item(.copyFiles)
            item(.cutFiles)
            item(.pasteFiles)
            item(.copyPath)
            item(.copyName)
            item(.copyDirPath)
            Divider()
            item(.search)
            item(.duplicates)
            item(.compare)
            item(.compareContent)
            item(.diff)
            item(.sync)
            item(.batchRename)
            item(.commandLine)
            Divider()
            item(.pack)
            item(.unpack)
            item(.addToArchive)
            item(.split)
            item(.combine)
            Divider()
            item(.checksum)
            item(.attributes)
            item(.tags)
            item(.quickLook)
            item(.dirSizes)
            item(.symlink)
            Divider()
            item(.branch)
            item(.quickView)
            item(.thumbnails)
            Divider()
            item(.connect)
            item(.cloud)
            item(.network)
            Divider()
            item(.favorites)
            item(.tabSets)
            item(.back)
            item(.forward)
            Divider()
            item(.resumeTransfer)
            Divider()
            Menu("Uživatelské příkazy") {
                ForEach(settings.userCommands) { cmd in
                    Button(cmd.name) { model.runUser(cmd) }
                }
                Divider()
                item(.userMenu)
            }
            Divider()
            item(.refresh)
            item(.markAll)
            item(.hidden)
            item(.mirror)
            Divider()
            item(.checkUpdate)
            item(.settings)
        }
    }
}
