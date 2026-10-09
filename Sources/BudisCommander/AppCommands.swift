import SwiftUI

/// Položky menu se zkratkami; kopírují nastavení z `Settings`.
struct AppCommands: Commands {
    let model: AppModel
    @ObservedObject var settings = Settings.shared

    private func item(_ action: ShortcutAction) -> some View {
        let ch = settings.char(for: action)
        return Group {
            if let c = ch.first {
                Button(action.title) { model.perform(action) }
                    .keyboardShortcut(KeyEquivalent(c), modifiers: .command)
            } else {
                Button(action.title) { model.perform(action) }
            }
        }
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
            item(.search)
            item(.compare)
            item(.compareContent)
            item(.diff)
            item(.sync)
            item(.batchRename)
            item(.commandLine)
            Divider()
            item(.pack)
            item(.unpack)
            item(.split)
            item(.combine)
            Divider()
            item(.checksum)
            item(.attributes)
            item(.dirSizes)
            item(.symlink)
            Divider()
            item(.branch)
            item(.quickView)
            item(.thumbnails)
            Divider()
            item(.connect)
            item(.network)
            Divider()
            item(.favorites)
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
            item(.settings)
        }
    }
}
