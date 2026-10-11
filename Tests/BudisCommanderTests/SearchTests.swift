import XCTest
@testable import BudisCommander

final class ArchiveSearchTests: XCTestCase {
    private func makeTar(in base: URL) throws -> URL {
        let fm = FileManager.default
        let src = base.appendingPathComponent("src")
        try fm.createDirectory(at: src.appendingPathComponent("sub"), withIntermediateDirectories: true)
        try "Ahoj světe, tajné slovo je pomeranč".write(to: src.appendingPathComponent("readme.txt"), atomically: true, encoding: .utf8)
        try "nic zajímavého".write(to: src.appendingPathComponent("sub/note.txt"), atomically: true, encoding: .utf8)
        try Data([0, 1, 2, 3, 0, 5]).write(to: src.appendingPathComponent("blob.dat"))
        let archive = base.appendingPathComponent("balik.tar")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        p.arguments = ["-cf", archive.path, "-C", src.path, "readme.txt", "sub", "blob.dat"]
        try p.run(); p.waitUntilExit()
        XCTAssertEqual(p.terminationStatus, 0)
        return archive
    }

    func testFindsEntriesByNameAndContent() throws {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("budis-as-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let archive = try makeTar(in: base)
        let flag = CancelFlag()

        let byName = ArchiveSearch.search(archive: archive, mask: "*.txt", text: "", limit: 100, flag: flag)
        XCTAssertEqual(Set(byName), ["readme.txt", "sub/note.txt"])
        let byText = ArchiveSearch.search(archive: archive, mask: "*", text: "POMERANČ", limit: 100, flag: flag)
        XCTAssertEqual(byText, ["readme.txt"])
        XCTAssertTrue(ArchiveSearch.search(archive: archive, mask: "*", text: "banán", limit: 100, flag: flag).isEmpty)
    }

    func testBrokenArchiveGivesNothing() throws {
        let f = FileManager.default.temporaryDirectory.appendingPathComponent("budis-bad-" + UUID().uuidString + ".zip")
        try "tohle není zip".write(to: f, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: f) }
        XCTAssertTrue(ArchiveSearch.search(archive: f, mask: "*", text: "", limit: 10, flag: CancelFlag()).isEmpty)
        XCTAssertTrue(ArchiveSearch.search(archive: URL(fileURLWithPath: "/neexistuje.zip"), mask: "*", text: "", limit: 10, flag: CancelFlag()).isEmpty)
    }
}
