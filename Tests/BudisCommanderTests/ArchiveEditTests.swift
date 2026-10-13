import XCTest
@testable import BudisCommander

final class ArchiveEditTests: XCTestCase {
    private func run(_ exe: String, _ args: [String], cwd: URL? = nil) throws -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = args
        p.currentDirectoryURL = cwd
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        try p.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        XCTAssertEqual(p.terminationStatus, 0, "\(exe) \(args)")
        return String(decoding: data, as: UTF8.self)
    }

    func testReplacesAndAddsEntriesInZip() throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("budis-ae-" + UUID().uuidString)
        defer { try? fm.removeItem(at: base) }
        let src = base.appendingPathComponent("src")
        try fm.createDirectory(at: src.appendingPathComponent("sub"), withIntermediateDirectories: true)
        try "STARE".write(to: src.appendingPathComponent("a.txt"), atomically: true, encoding: .utf8)
        try "B".write(to: src.appendingPathComponent("sub/b.txt"), atomically: true, encoding: .utf8)
        let zip = base.appendingPathComponent("balik.zip")
        _ = try run("/usr/bin/zip", ["-q", "-r", zip.path, "a.txt", "sub"], cwd: src)
        XCTAssertTrue(ArchiveEdit.canUpdate(zip))
        XCTAssertFalse(ArchiveEdit.canUpdate(base.appendingPathComponent("x.7z")))

        try "NOVE ěščř".write(to: src.appendingPathComponent("a.txt"), atomically: true, encoding: .utf8)
        try ArchiveEdit.replaceInZip(archive: zip, root: src, entry: ArchiveEdit.entryName(root: src, file: src.appendingPathComponent("a.txt")))
        try "NOVY".write(to: src.appendingPathComponent("sub/c.txt"), atomically: true, encoding: .utf8)
        try ArchiveEdit.replaceInZip(archive: zip, root: src, entry: "sub/c.txt")

        XCTAssertEqual(try run("/usr/bin/unzip", ["-p", zip.path, "a.txt"]), "NOVE ěščř")
        XCTAssertEqual(try run("/usr/bin/unzip", ["-p", zip.path, "sub/b.txt"]), "B")
        XCTAssertEqual(try run("/usr/bin/unzip", ["-p", zip.path, "sub/c.txt"]), "NOVY")
        let listing = try run("/usr/bin/unzip", ["-Z1", zip.path])
        XCTAssertEqual(listing.split(separator: "\n").filter { $0 == "a.txt" }.count, 1)
    }

    func testEntryNameIsRelativeToExtractRoot() {
        let root = URL(fileURLWithPath: "/tmp/BudisArchives/abc")
        XCTAssertEqual(ArchiveEdit.entryName(root: root, file: root.appendingPathComponent("sub/dir/f.txt")), "sub/dir/f.txt")
    }
}
