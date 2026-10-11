import XCTest
@testable import BudisCommander

final class SelectionCriteriaTests: XCTestCase {
    func testParsesSizes() {
        XCTAssertEqual(SelectionCriteria.parseSize("500").value, 500)
        XCTAssertEqual(SelectionCriteria.parseSize("10 KB").value, 10240)
        XCTAssertEqual(SelectionCriteria.parseSize("1,5 MB").value, 1_572_864)
        XCTAssertEqual(SelectionCriteria.parseSize("2gb").value, 2_147_483_648)
        XCTAssertTrue(SelectionCriteria.parseSize("").ok)
        XCTAssertNil(SelectionCriteria.parseSize("").value)
        XCTAssertFalse(SelectionCriteria.parseSize("hodně").ok)
    }

    func testParsesDates() {
        XCTAssertFalse(SelectionCriteria.parseDate("zítra").ok)
        XCTAssertTrue(SelectionCriteria.parseDate("").ok)
        let a = SelectionCriteria.parseDate("31. 12. 2025").value, b = SelectionCriteria.parseDate("2025-12-31").value
        XCTAssertNotNil(a)
        XCTAssertEqual(a, b)
    }

    func testCombinesMaskKindSizeAndDate() {
        let cal = Calendar.current
        func day(_ m: Int, _ d: Int, _ y: Int = 2025, hour: Int = 12) -> Date {
            cal.date(from: DateComponents(year: y, month: m, day: d, hour: hour))!
        }
        var c = SelectionCriteria()
        c.mask = "*.jpg;*.png"; c.kind = 1; c.minSize = 100; c.maxSize = 1000
        c.modifiedFrom = day(1, 1); c.modifiedTo = day(1, 31)
        XCTAssertTrue(c.matches(name: "a.JPG", isDirectory: false, size: 500, modified: day(1, 31, hour: 23)))
        XCTAssertFalse(c.matches(name: "a.txt", isDirectory: false, size: 500, modified: day(1, 10)))
        XCTAssertFalse(c.matches(name: "a.jpg", isDirectory: false, size: 50, modified: day(1, 10)))
        XCTAssertFalse(c.matches(name: "a.jpg", isDirectory: false, size: 5000, modified: day(1, 10)))
        XCTAssertFalse(c.matches(name: "a.jpg", isDirectory: false, size: 500, modified: day(2, 1)))
        XCTAssertFalse(c.matches(name: "a.jpg", isDirectory: false, size: 500, modified: day(12, 31, 2024, hour: 23)))
        XCTAssertFalse(c.matches(name: "a.jpg", isDirectory: false, size: 500, modified: nil))
        XCTAssertFalse(c.matches(name: "dir.jpg", isDirectory: true, size: 0, modified: day(1, 10)))
    }
}

@MainActor
final class PaneSelectionTests: XCTestCase {
    func testMarksByCriteriaSameExtensionAndInvert() throws {
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("budis-sel-" + UUID().uuidString)
        defer { try? fm.removeItem(at: dir) }
        try fm.createDirectory(at: dir.appendingPathComponent("d.jpg"), withIntermediateDirectories: true)
        try String(repeating: "x", count: 10).write(to: dir.appendingPathComponent("a.jpg"), atomically: true, encoding: .utf8)
        try String(repeating: "x", count: 2000).write(to: dir.appendingPathComponent("b.jpg"), atomically: true, encoding: .utf8)
        try String(repeating: "x", count: 2000).write(to: dir.appendingPathComponent("c.txt"), atomically: true, encoding: .utf8)
        let p = PaneState(url: dir, ephemeral: true)

        var c = SelectionCriteria()
        c.mask = "*.jpg"; c.kind = 1; c.minSize = 1000
        XCTAssertEqual(p.markWhere(c, on: true), 1)
        XCTAssertEqual(p.items.filter { p.marked.contains($0.id) }.map(\.name), ["b.jpg"])

        p.marked = []
        if let i = p.items.firstIndex(where: { $0.name == "a.jpg" }) { p.moveTo(i) }
        XCTAssertEqual(p.markSameExtension(), 2)
        XCTAssertEqual(Set(p.items.filter { p.marked.contains($0.id) }.map(\.name)), ["a.jpg", "b.jpg"])

        var only = SelectionCriteria()
        only.mask = "a.*"; only.marked = true
        XCTAssertEqual(p.markWhere(only, on: false), 1)
        XCTAssertEqual(p.marked.count, 1)

        p.invertMarks()
        XCTAssertEqual(Set(p.items.filter { p.marked.contains($0.id) }.map(\.name)), ["a.jpg", "c.txt", "d.jpg"])
    }
}
