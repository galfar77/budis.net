import SwiftUI

/// Barevné téma: zvýrazňovací barva (výběr, rámeček aktivního panelu, tlačítka) a barva složek.
struct ColorTheme: Identifiable, Equatable {
    let id: String
    let title: String
    let accent: Color
    let folder: Color

    static let all: [ColorTheme] = [
        ColorTheme(id: "default", title: L("Výchozí (modrá)"), accent: .accentColor, folder: .blue),
        ColorTheme(id: "ocean", title: L("Oceán"), accent: Color(red: 0.055, green: 0.561, blue: 0.651), folder: Color(red: 0.039, green: 0.667, blue: 0.769)),
        ColorTheme(id: "forest", title: L("Les"), accent: Color(red: 0.180, green: 0.620, blue: 0.357), folder: Color(red: 0.239, green: 0.690, blue: 0.431)),
        ColorTheme(id: "sunset", title: L("Západ slunce"), accent: Color(red: 0.898, green: 0.451, blue: 0.169), folder: Color(red: 0.929, green: 0.553, blue: 0.239)),
        ColorTheme(id: "violet", title: L("Fialová"), accent: Color(red: 0.541, green: 0.333, blue: 0.839), folder: Color(red: 0.612, green: 0.416, blue: 0.890)),
        ColorTheme(id: "graphite", title: L("Grafit"), accent: Color(red: 0.420, green: 0.463, blue: 0.514), folder: Color(red: 0.502, green: 0.545, blue: 0.600)),
    ]

    static func get(_ id: String) -> ColorTheme { all.first { $0.id == id } ?? all[0] }
}

/// Aktuální barvy aplikace podle zvoleného tématu (čte se v těle pohledů; při změně se celé okno překreslí).
@MainActor
enum Palette {
    static var accent: Color { Settings.shared.palette.accent }
    static var folder: Color { Settings.shared.palette.folder }
}
