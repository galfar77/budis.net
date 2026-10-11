import XCTest
@testable import BudisCommander

final class AdbParsingTests: XCTestCase {
    func testParsesDevicesAndListing() {
        let devices = Adb.parseDevices("List of devices attached\nR58M123  device usb:1-1 product:x model:Pixel_7 device:panther transport_id:3\nABCD  unauthorized transport_id:4\n* daemon started\n")
        XCTAssertEqual(devices.count, 2)
        XCTAssertTrue(devices[0].ready)
        XCTAssertEqual(devices[0].model, "Pixel_7")
        XCTAssertFalse(devices[1].ready)

        let list = Adb.parseListing("""
        total 24
        drwxrwx--x  4 root sdcard_rw  4096 2024-05-01 10:00 DCIM
        -rw-rw----  1 root sdcard_rw 12345 2024-05-02 11:30 Moje fotka (1).jpg
        lrwxrwxrwx  1 root root 21 2024-01-01 00:00 sdcard -> /storage/self/primary
        crw-rw-rw-  1 root root 1, 3 2024-01-01 00:00 null
        """, in: "/sdcard")
        XCTAssertEqual(list.count, 3)
        XCTAssertTrue(list[0].isDirectory)
        XCTAssertEqual(list[1].name, "Moje fotka (1).jpg")
        XCTAssertEqual(list[1].size, 12345)
        XCTAssertEqual(list[1].remotePath, "/sdcard/Moje fotka (1).jpg")
        XCTAssertTrue(list[2].isDirectory)
        XCTAssertEqual(list[2].name, "sdcard")
        XCTAssertEqual(Adb.quote("a'b"), "'a'\\''b'")
    }
}

@MainActor
final class AdbIntegrationTests: XCTestCase {
    /// Napodobenina programu adb: příkazy provádí nad složkou na disku, jako by to byl telefon.
    private func makeFakeAdb(base: URL, root: URL) throws -> String {
        let script = base.appendingPathComponent("adb")
        let text = #"""
        #!/bin/bash
        ROOT="\#(root.path)"
        export TIME_STYLE=long-iso
        if [ "$1" = "-s" ]; then shift 2; fi
        cmd="$1"; shift
        case "$cmd" in
          devices) echo "List of devices attached"; echo "FAKE123  device product:fake model:Fake_Phone device:fake transport_id:1";;
          get-state) echo device;;
          shell)
            c=$(printf '%s' "$*" | sed "s#'/#'$ROOT/#g")
            pre=""
            if [ "$(uname)" = "Darwin" ]; then pre="ls() { command ls -D '%Y-%m-%d %H:%M' \"\$@\"; };"; fi
            cd "$ROOT" && sh -c "$pre $c";;
          pull) mkdir -p "$(dirname "$2")"; cp -r "$ROOT$1" "$2"; echo "[100%] $1";;
          push) cp -r "$1" "$ROOT$2"; echo "[100%] $2";;
        esac
        """#
        try text.write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        return script.path
    }

    func testBrowsesAndTransfersThroughFakeAdb() async throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("budis-adb-test-" + UUID().uuidString)
        let root = base.appendingPathComponent("phone")
        defer { try? fm.removeItem(at: base) }
        try fm.createDirectory(at: root.appendingPathComponent("sdcard/DCIM"), withIntermediateDirectories: true)
        try "JPG".write(to: root.appendingPathComponent("sdcard/DCIM/foto.jpg"), atomically: true, encoding: .utf8)
        try "Ahoj".write(to: root.appendingPathComponent("sdcard/poznamka.txt"), atomically: true, encoding: .utf8)
        let exe = try makeFakeAdb(base: base, root: root)

        let devices = try await Adb.listDevices(exe: exe)
        XCTAssertEqual(devices.map(\.serial), ["FAKE123"])

        let conn = RemoteConnection(proto: .adb, host: "FAKE123", port: 0, user: "Fake_Phone", password: "",
                                    insecure: false, rcloneExe: exe)
        XCTAssertEqual(conn.displayName, "phone Fake Phone")
        XCTAssertEqual(conn.startPath, "/sdcard")
        try await conn.adbConnect()
        let list = try await conn.list("/sdcard")
        XCTAssertTrue(list.contains { $0.name == "DCIM" && $0.isDirectory })
        XCTAssertTrue(list.contains { $0.name == "poznamka.txt" && $0.size == 4 })

        let down = base.appendingPathComponent("down")
        try await conn.download(path: "/sdcard/poznamka.txt", isDirectory: false, size: 4,
                                to: down.appendingPathComponent("poznamka.txt"))
        XCTAssertEqual(try String(contentsOf: down.appendingPathComponent("poznamka.txt")), "Ahoj")
        try await conn.download(path: "/sdcard/DCIM", isDirectory: true, to: down.appendingPathComponent("DCIM"))
        XCTAssertEqual(try String(contentsOf: down.appendingPathComponent("DCIM/foto.jpg")), "JPG")

        let up = base.appendingPathComponent("up.txt")
        try "Nahrano".write(to: up, atomically: true, encoding: .utf8)
        try await conn.upload(local: up, to: "/sdcard/up.txt")
        XCTAssertEqual(try String(contentsOf: root.appendingPathComponent("sdcard/up.txt")), "Nahrano")
        try await conn.mkdir("/sdcard/Nová složka")
        XCTAssertTrue(fm.fileExists(atPath: root.appendingPathComponent("sdcard/Nová složka").path))
        try await conn.rename(from: "/sdcard/up.txt", to: "/sdcard/Nová složka/up2.txt")
        XCTAssertTrue(fm.fileExists(atPath: root.appendingPathComponent("sdcard/Nová složka/up2.txt").path))
        try await conn.delete(path: "/sdcard/Nová složka", isDirectory: true)
        XCTAssertFalse(fm.fileExists(atPath: root.appendingPathComponent("sdcard/Nová složka").path))
    }
}

@MainActor
final class SyncTaskTests: XCTestCase {
    func testDueLogicAndRcloneTargetParsing() {
        let now = Date()
        XCTAssertFalse(SyncTask(intervalMinutes: 0).isDue(now))
        XCTAssertTrue(SyncTask(intervalMinutes: 5).isDue(now))
        XCTAssertFalse(SyncTask(intervalMinutes: 5, lastRun: now.addingTimeInterval(-120)).isDue(now))
        XCTAssertTrue(SyncTask(intervalMinutes: 5, lastRun: now.addingTimeInterval(-360)).isDue(now))
        XCTAssertEqual(SyncTask.parseRclone("rclone:gdrive:/Zaloha")?.remote, "gdrive")
        XCTAssertEqual(SyncTask.parseRclone("rclone:gdrive:/Zaloha")?.path, "/Zaloha")
        XCTAssertEqual(SyncTask.parseRclone("rclone:gdrive:Zaloha")?.path, "/Zaloha")
        XCTAssertEqual(SyncTask.parseRclone("rclone:gdrive:")?.path, "/")
        XCTAssertNil(SyncTask.parseRclone("/Users/x/slozka"))
    }

    func testLocalSyncCopiesAndOnlyDeletesWhenAsked() async throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("budis-sync-test-" + UUID().uuidString)
        defer { try? fm.removeItem(at: base) }
        let src = base.appendingPathComponent("src"), dst = base.appendingPathComponent("dst")
        try fm.createDirectory(at: src.appendingPathComponent("sub"), withIntermediateDirectories: true)
        try fm.createDirectory(at: dst, withIntermediateDirectories: true)
        try "A".write(to: src.appendingPathComponent("a.txt"), atomically: true, encoding: .utf8)
        try "B".write(to: src.appendingPathComponent("sub/b.txt"), atomically: true, encoding: .utf8)
        try "E".write(to: dst.appendingPathComponent("extra.txt"), atomically: true, encoding: .utf8)

        let model = AppModel()
        var task = SyncTask(name: "t", source: src.path, target: dst.path)
        let first = try await model.runSyncTask(task)
        XCTAssertTrue(first.contains("2"), first)
        XCTAssertEqual(try String(contentsOf: dst.appendingPathComponent("a.txt")), "A")
        XCTAssertEqual(try String(contentsOf: dst.appendingPathComponent("sub/b.txt")), "B")
        XCTAssertTrue(fm.fileExists(atPath: dst.appendingPathComponent("extra.txt").path))
        let second = try await model.runSyncTask(task)
        XCTAssertEqual(second, "beze změn")

        task.deleteExtra = true
        _ = try await model.runSyncTask(task)
        XCTAssertFalse(fm.fileExists(atPath: dst.appendingPathComponent("extra.txt").path))
    }

    func testOverlappingFoldersAreRejected() async throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("budis-sync-test-" + UUID().uuidString)
        defer { try? fm.removeItem(at: base) }
        try fm.createDirectory(at: base, withIntermediateDirectories: true)
        let model = AppModel()
        do {
            _ = try await model.runSyncTask(SyncTask(name: "x", source: base.path, target: base.appendingPathComponent("dole").path))
            XCTFail("Překrývající se složky měly být odmítnuty")
        } catch {
            XCTAssertFalse(error.localizedDescription.isEmpty)
        }
    }
}
