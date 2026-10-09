import SwiftUI
import AppKit

@main
struct BudisCommanderApp: App {
    init() {
        NSApplication.shared.setActivationPolicy(.regular)
    }

    var body: some Scene {
        WindowGroup("Budis Commander") {
            ContentView()
                .frame(minWidth: 900, minHeight: 480)
                .onAppear { NSApp.activate(ignoringOtherApps: true) }
        }
    }
}
