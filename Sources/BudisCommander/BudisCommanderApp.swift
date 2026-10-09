import SwiftUI
import AppKit

@main
struct BudisCommanderApp: App {
    @StateObject private var model = AppModel()

    init() {
        NSApplication.shared.setActivationPolicy(.regular)
    }

    var body: some Scene {
        WindowGroup("Budis Commander") {
            ContentView(model: model)
                .frame(minWidth: 900, minHeight: 480)
                .onAppear { NSApp.activate(ignoringOtherApps: true) }
        }
        .commands { AppCommands(model: model) }
    }
}
