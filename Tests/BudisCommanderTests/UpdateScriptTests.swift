import XCTest
@testable import BudisCommander

final class UpdateScriptTests: XCTestCase {
    private func finishedPid() throws -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/true")
        try p.run(); p.waitUntilExit()
        return String(p.processIdentifier)
    }

    private func run(_ script: String, _ args: [String]) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = ["-c", script, "bash"] + args
        p.environment = ProcessInfo.processInfo.environment.merging(["BUDIS_OPEN": "true"]) { _, new in new }
        try p.run(); p.waitUntilExit()
        XCTAssertEqual(p.terminationStatus, 0)
    }

    private func app(_ url: URL, _ marker: String) throws {
        try FileManager.default.createDirectory(at: url.appendingPathComponent("Contents"), withIntermediateDirectories: true)
        try marker.write(to: url.appendingPathComponent("Contents/marker.txt"), atomically: true, encoding: .utf8)
    }

    private func marker(_ url: URL) -> String? {
        try? String(contentsOf: url.appendingPathComponent("Contents/marker.txt"), encoding: .utf8)
    }

    func testInstallKeepsPreviousVersionAndRollbackSwapsBack() throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("budis-upd-" + UUID().uuidString)
        defer { try? fm.removeItem(at: base) }
        let target = base.appendingPathComponent("Aplikace/BudisCommander.app")
        let fresh = base.appendingPathComponent("stazeno/BudisCommander.app")
        let previous = base.appendingPathComponent("Support/Previous/BudisCommander.app")
        try app(target, "STARA"); try app(fresh, "NOVA")
        let pid = try finishedPid()

        try run(Updater.installScript, [pid, fresh.path, target.path, previous.path, "0"])
        XCTAssertEqual(marker(target), "NOVA")
        XCTAssertEqual(marker(previous), "STARA")
        XCTAssertFalse(fm.fileExists(atPath: fresh.path))

        try run(Updater.rollbackScript, [pid, "", target.path, previous.path])
        XCTAssertEqual(marker(target), "STARA")
        XCTAssertEqual(marker(previous), "NOVA")
        try run(Updater.rollbackScript, [pid, "", target.path, previous.path])
        XCTAssertEqual(marker(target), "NOVA")
        XCTAssertEqual(marker(previous), "STARA")
    }

    func testSecondInstallReplacesOlderPreviousVersion() throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("budis-upd-" + UUID().uuidString)
        defer { try? fm.removeItem(at: base) }
        let target = base.appendingPathComponent("BudisCommander.app")
        let previous = base.appendingPathComponent("Previous/BudisCommander.app")
        try app(target, "V1")
        let pid = try finishedPid()
        for v in ["V2", "V3"] {
            let fresh = base.appendingPathComponent("dl-\(v)/BudisCommander.app")
            try app(fresh, v)
            try run(Updater.installScript, [pid, fresh.path, target.path, previous.path, "0"])
        }
        XCTAssertEqual(marker(target), "V3")
        XCTAssertEqual(marker(previous), "V2")
    }
}
