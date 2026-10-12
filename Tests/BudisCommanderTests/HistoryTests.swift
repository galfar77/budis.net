import XCTest
@testable import BudisCommander

final class OperationLogFileTests: XCTestCase {
    private func temp() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("budis-hist-" + UUID().uuidString)
    }

    @MainActor
    func testPersistsAndListsNewestFirst() {
        let base = temp(); defer { try? FileManager.default.removeItem(at: base) }
        let file = base.appendingPathComponent("history.json")
        let log = OperationLog(url: file)
        log.add(.copy, "první", items: [OpItem(from: "/a", to: "/b")])
        log.add(.delete, "druhé", items: [OpItem(from: "/c", to: "", trash: "/t/c")], permanent: true)
        XCTAssertEqual(log.entries.map(\.summary), ["druhé", "první"])
        let again = OperationLog(url: file)
        XCTAssertEqual(again.entries.count, 2)
        XCTAssertTrue(again.entries[0].permanent)
        XCTAssertEqual(again.add(.move, "třetí", items: [OpItem(from: "/x", to: "/y")]).id, 3)
    }

    @MainActor
    func testKeepsOnlyTheLatestEntries() {
        let log = OperationLog(url: nil)
        for i in 0..<(OperationLog.maxEntries + 20) { log.add(.rename, "r\(i)", items: [OpItem(from: "/a\(i)", to: "/b\(i)")]) }
        XCTAssertEqual(log.entries.count, OperationLog.maxEntries)
        XCTAssertEqual(log.entries.first?.summary, "r\(OperationLog.maxEntries + 19)")
    }

    func testRevertMoveRenameAndDeleteFromTrash() throws {
        let fm = FileManager.default
        let base = temp(); defer { try? fm.removeItem(at: base) }
        try fm.createDirectory(at: base.appendingPathComponent("a"), withIntermediateDirectories: true)
        try fm.createDirectory(at: base.appendingPathComponent("b"), withIntermediateDirectories: true)
        try fm.createDirectory(at: base.appendingPathComponent("trash"), withIntermediateDirectories: true)
        let from = base.appendingPathComponent("a/f.txt").path, to = base.appendingPathComponent("b/f.txt").path
        try "A".write(toFile: from, atomically: true, encoding: .utf8)
        try fm.moveItem(atPath: from, toPath: to)
        let move = OpEntry(id: 1, time: Date(), kind: .move, summary: "m", items: [OpItem(from: from, to: to)])
        XCTAssertTrue(OperationLog.canRevert(move))
        var r = OperationLog.revert(move)
        XCTAssertEqual(r.done, 1); XCTAssertTrue(r.errors.isEmpty)
        XCTAssertTrue(fm.fileExists(atPath: from)); XCTAssertFalse(fm.fileExists(atPath: to))

        try "B".write(toFile: to, atomically: true, encoding: .utf8)             // původní místo je obsazené
        try fm.moveItem(atPath: from, toPath: base.appendingPathComponent("a/h.txt").path)
        try "jiný".write(toFile: from, atomically: true, encoding: .utf8)
        r = OperationLog.revert(move)
        XCTAssertEqual(r.done, 0); XCTAssertEqual(r.errors.count, 1)
        XCTAssertEqual(try String(contentsOfFile: from, encoding: .utf8), "jiný")

        // smazání: soubor v „Koši“ se vrátí na původní místo, při obsazeném názvu s číslem
        let trash = base.appendingPathComponent("trash/doc.txt").path
        try "obsah".write(toFile: trash, atomically: true, encoding: .utf8)
        let orig = base.appendingPathComponent("kam/doc.txt").path
        let del = OpEntry(id: 2, time: Date(), kind: .delete, summary: "d", items: [OpItem(from: orig, to: "", trash: trash)])
        XCTAssertTrue(OperationLog.canRevert(del))
        r = OperationLog.revert(del)
        XCTAssertEqual(r.done, 1)
        XCTAssertEqual(try String(contentsOfFile: orig, encoding: .utf8), "obsah")
        try "obsah2".write(toFile: trash, atomically: true, encoding: .utf8)
        r = OperationLog.revert(del)
        XCTAssertEqual(try String(contentsOfFile: base.appendingPathComponent("kam/doc (2).txt").path, encoding: .utf8), "obsah2")
        XCTAssertEqual(try String(contentsOfFile: orig, encoding: .utf8), "obsah")
    }

    func testCanRevertRules() {
        let item = OpItem(from: "/a", to: "", trash: "/t")
        XCTAssertFalse(OperationLog.canRevert(OpEntry(id: 1, time: Date(), kind: .delete, summary: "", items: [item], permanent: true)))
        XCTAssertFalse(OperationLog.canRevert(OpEntry(id: 1, time: Date(), kind: .delete, summary: "", items: [OpItem(from: "/a", to: "")])))
        XCTAssertFalse(OperationLog.canRevert(OpEntry(id: 1, time: Date(), kind: .move, summary: "", items: [OpItem(from: "/a", to: "/b")], reverted: true)))
        XCTAssertTrue(OperationLog.canRepeat(OpEntry(id: 1, time: Date(), kind: .copy, summary: "", items: [OpItem(from: "/a", to: "/b")])))
        XCTAssertFalse(OperationLog.canRepeat(OpEntry(id: 1, time: Date(), kind: .move, summary: "", items: [OpItem(from: "/a", to: "/b")])))
    }

    func testRepeatCopyCopiesAgain() throws {
        let fm = FileManager.default
        let base = temp(); defer { try? fm.removeItem(at: base) }
        try fm.createDirectory(at: base, withIntermediateDirectories: true)
        let src = base.appendingPathComponent("src.txt").path, dst = base.appendingPathComponent("novy/dst.txt").path
        try "NOVE".write(toFile: src, atomically: true, encoding: .utf8)
        let e = OpEntry(id: 1, time: Date(), kind: .copy, summary: "", items: [OpItem(from: src, to: dst)])
        XCTAssertEqual(try OperationLog.repeatCopy(e), 1)
        XCTAssertEqual(try String(contentsOfFile: dst, encoding: .utf8), "NOVE")
    }
}

final class ColumnLayoutTests: XCTestCase {
    func testOrderVisibilityMoveAndResize() {
        var l = ColumnLayout()
        XCTAssertTrue(l.isDefault)
        XCTAssertEqual(l.visible(showExt: false, showExtra: false), ["size", "date"])
        XCTAssertEqual(l.visible(showExt: true, showExtra: true), ["ext", "extra", "size", "date"])
        XCTAssertTrue(l.move("date", by: -1, showExt: false, showExtra: false))
        XCTAssertEqual(l.visible(showExt: false, showExtra: false), ["date", "size"])
        XCTAssertFalse(l.isDefault)
        XCTAssertFalse(l.move("date", by: -1, showExt: false, showExtra: false))
        l.resize("size", to: 5); XCTAssertEqual(l.width("size"), ColumnLayout.minWidth)
        l.resize("date", to: 9999); XCTAssertEqual(l.width("date"), ColumnLayout.maxWidth)
        l.resize("extra", to: 100); XCTAssertNil(l.widths["extra"])
        XCTAssertEqual(ColumnLayout(order: ["date", "zzz", "date"]).normalized().first, "date")
        XCTAssertEqual(ColumnLayout(order: ["date", "zzz", "date"]).normalized().count, 4)
    }

    func testFolderViewCodableKeepsOldDataReadable() throws {
        let old = #"{"sort":"size","ascending":false}"#.data(using: .utf8)!
        let v = try JSONDecoder().decode(FolderView.self, from: old)
        XCTAssertNil(v.columns)
        var layout = ColumnLayout(); layout.resize("size", to: 120)
        let data = try JSONEncoder().encode(FolderView(sort: "name", ascending: true, columns: layout))
        XCTAssertEqual(try JSONDecoder().decode(FolderView.self, from: data).columns?.width("size"), 120)
    }

    @MainActor
    func testPaneRemembersColumnsPerFolder() throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("budis-cols-" + UUID().uuidString)
        defer { try? fm.removeItem(at: base); UserDefaults.standard.removeObject(forKey: "folderViews") }
        try fm.createDirectory(at: base.appendingPathComponent("a"), withIntermediateDirectories: true)
        try fm.createDirectory(at: base.appendingPathComponent("b"), withIntermediateDirectories: true)
        let p = PaneState(url: base.appendingPathComponent("a"))
        var l = ColumnLayout(); l.resize("size", to: 120)
        p.setColumns(l)
        XCTAssertNotNil(p.customColumns)
        p.navigate(to: base.appendingPathComponent("b"))
        XCTAssertNil(p.customColumns)
        p.navigate(to: base.appendingPathComponent("a"))
        XCTAssertEqual(p.columns.width("size"), 120)
        p.resetColumns()
        XCTAssertEqual(p.columns.width("size"), ColumnLayout.defaultWidth("size"))
    }
}
