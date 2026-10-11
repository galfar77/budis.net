import AppKit

/// Přeloží názvy systémových položek horního menu (Soubor, Úpravy, Zobrazení, Okno, Nápověda…) podle jazyka aplikace.
/// Systém je překládá podle jazyka macOS, takže se bez toho neřídí volbou v Nastavení. Sledují se změny menu,
/// protože SwiftUI menu průběžně přestavuje.
@MainActor
enum MenuLocalizer {
    /// Anglický název a český překlad; „{app}“ se nahradí názvem aplikace.
    static let table: [(en: String, cs: String)] = [
        ("File", "Soubor"), ("Edit", "Úpravy"), ("View", "Zobrazení"), ("Window", "Okno"), ("Help", "Nápověda"),
        ("New Window", "Nové okno"), ("New Tab", "Nová karta"), ("Close", "Zavřít"), ("Close Window", "Zavřít okno"),
        ("Close All", "Zavřít vše"), ("Close Tab", "Zavřít kartu"), ("Print…", "Tisk…"),
        ("Undo", "Zpět"), ("Redo", "Znovu"), ("Cut", "Vyjmout"), ("Copy", "Kopírovat"), ("Paste", "Vložit"),
        ("Paste and Match Style", "Vložit a přizpůsobit styl"), ("Delete", "Smazat"), ("Select All", "Vybrat vše"),
        ("Find", "Hledat"), ("Find…", "Hledat…"), ("Find Next", "Hledat další"), ("Find Previous", "Hledat předchozí"),
        ("Start Dictation…", "Spustit diktování…"), ("Emoji & Symbols", "Emoji a symboly"), ("AutoFill", "Automatické vyplnění"),
        ("Show Tab Bar", "Zobrazit panel karet"), ("Hide Tab Bar", "Skrýt panel karet"),
        ("Show All Tabs", "Zobrazit všechny karty"), ("Show Toolbar", "Zobrazit panel nástrojů"), ("Hide Toolbar", "Skrýt panel nástrojů"),
        ("Show Sidebar", "Zobrazit postranní panel"), ("Hide Sidebar", "Skrýt postranní panel"),
        ("Enter Full Screen", "Přejít do režimu celé obrazovky"), ("Exit Full Screen", "Ukončit režim celé obrazovky"),
        ("Minimize", "Minimalizovat"), ("Zoom", "Zvětšit/zmenšit"), ("Move & Resize", "Přesunout a změnit velikost"),
        ("Fill", "Vyplnit"), ("Center", "Na střed"), ("Return to Previous Size", "Vrátit na původní velikost"),
        ("Bring All to Front", "Přenést vše do popředí"), ("Show Previous Tab", "Zobrazit předchozí kartu"),
        ("Show Next Tab", "Zobrazit další kartu"), ("Move Tab to New Window", "Přesunout kartu do nového okna"),
        ("Merge All Windows", "Sloučit všechna okna"), ("Search", "Hledat"),
        ("About {app}", "O aplikaci {app}"), ("Settings…", "Nastavení…"), ("Preferences…", "Předvolby…"),
        ("Services", "Služby"), ("Hide {app}", "Skrýt {app}"), ("Hide Others", "Skrýt ostatní"), ("Show All", "Zobrazit vše"),
        ("Quit {app}", "Ukončit {app}"), ("{app} Help", "Nápověda k aplikaci {app}"),
    ]

    static let appName = "Budis Commander"

    /// Přeloží název menu do jazyka ("cs" nebo "en"); vrací nil, když není co měnit.
    static func translate(_ title: String, to language: String) -> String? {
        for pair in table {
            let en = pair.en.replacingOccurrences(of: "{app}", with: appName)
            let cs = pair.cs.replacingOccurrences(of: "{app}", with: appName)
            if language == "cs", title == en, en != cs { return cs }
            if language == "en", title == cs, en != cs { return en }
        }
        return nil
    }

    private static var observers: [NSObjectProtocol] = []
    private static var scheduled = false

    static func install() {
        guard observers.isEmpty else { return }
        let nc = NotificationCenter.default
        for name in [NSMenu.didAddItemNotification, NSMenu.didChangeItemNotification,
                     NSApplication.didFinishLaunchingNotification, NSApplication.didBecomeActiveNotification] {
            observers.append(nc.addObserver(forName: name, object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { MenuLocalizer.schedule() }
            })
        }
        schedule()
    }

    private static func schedule() {
        guard !scheduled else { return }
        scheduled = true
        DispatchQueue.main.async {
            MainActor.assumeIsolated {
                scheduled = false
                if let menu = NSApp.mainMenu { apply(menu, language: Tr.language) }
            }
        }
    }

    static func apply(_ menu: NSMenu, language: String) {
        for item in menu.items {
            if let t = translate(item.title, to: language) { item.title = t }
            if let sub = item.submenu {
                if let t = translate(sub.title, to: language) { sub.title = t }
                apply(sub, language: language)
            }
        }
    }
}
