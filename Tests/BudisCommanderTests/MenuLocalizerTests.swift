import XCTest
@testable import BudisCommander

@MainActor
final class MenuLocalizerTests: XCTestCase {
    func testTopLevelMenusToCzech() {
        XCTAssertEqual(MenuLocalizer.translate("File", to: "cs"), "Soubor")
        XCTAssertEqual(MenuLocalizer.translate("Edit", to: "cs"), "Úpravy")
        XCTAssertEqual(MenuLocalizer.translate("View", to: "cs"), "Zobrazení")
        XCTAssertEqual(MenuLocalizer.translate("Window", to: "cs"), "Okno")
        XCTAssertEqual(MenuLocalizer.translate("Help", to: "cs"), "Nápověda")
        XCTAssertNil(MenuLocalizer.translate("Nástroje", to: "cs"))
        XCTAssertNil(MenuLocalizer.translate("Soubor", to: "cs"))
    }

    func testBackToEnglishAndAppName() {
        XCTAssertEqual(MenuLocalizer.translate("Soubor", to: "en"), "File")
        XCTAssertEqual(MenuLocalizer.translate("Quit Budis Commander", to: "cs"), "Ukončit Budis Commander")
        XCTAssertEqual(MenuLocalizer.translate("Ukončit Budis Commander", to: "en"), "Quit Budis Commander")
        XCTAssertNil(MenuLocalizer.translate("File", to: "en"))
    }

    func testAppliesToWholeMenuTree() {
        let main = NSMenu()
        let file = NSMenuItem(title: "File", action: nil, keyEquivalent: "")
        let fileMenu = NSMenu(title: "File")
        fileMenu.addItem(NSMenuItem(title: "Close", action: nil, keyEquivalent: ""))
        fileMenu.addItem(NSMenuItem(title: "Vlastní položka", action: nil, keyEquivalent: ""))
        file.submenu = fileMenu
        main.addItem(file)
        MenuLocalizer.apply(main, language: "cs")
        XCTAssertEqual(file.title, "Soubor")
        XCTAssertEqual(fileMenu.title, "Soubor")
        XCTAssertEqual(fileMenu.items.map(\.title), ["Zavřít", "Vlastní položka"])
    }
}
