import SwiftUI
import AppKit

/// Zavření okna ukončí aplikaci (jediné okno nejde jinak znovu otevřít).
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

@main
struct BudisCommanderApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var model = AppModel()

    init() {
        NSApplication.shared.setActivationPolicy(.regular)
    }

    var body: some Scene {
        WindowGroup(L("Budis Commander")) {
            ContentView(model: model)
                .frame(minWidth: 900, minHeight: 480)
                .onAppear { NSApp.activate(ignoringOtherApps: true) }
        }
        .commands { AppCommands(model: model) }
    }
}
