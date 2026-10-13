import XCTest
@testable import BudisCommander

@MainActor
final class ShareLinkTests: XCTestCase {
    func testParseLinkTakesTheFirstUrlLine() {
        XCTAssertEqual(Rclone.parseLink("\nhttps://www.dropbox.com/s/abc/file.txt?dl=0\n"), "https://www.dropbox.com/s/abc/file.txt?dl=0")
        XCTAssertEqual(Rclone.parseLink("Link:\nhttp://x.test/a\nhttps://y.test/b"), "http://x.test/a")
        XCTAssertNil(Rclone.parseLink("žádný odkaz"))
        XCTAssertNil(Rclone.parseLink(""))
    }

    func testCloudPanesOfferShareLinkOthersDoNot() {
        func cmds(_ kind: ContextKind, remote: Bool, cloud: Bool) -> [ContextCommand] {
            ContextMenus.entries(kind: kind, remote: remote, cloud: cloud).compactMap { if case .command(let c) = $0 { return c } else { return nil } }
        }
        XCTAssertTrue(cmds(.file, remote: true, cloud: true).contains(.action(.shareLink)))
        XCTAssertTrue(cmds(.folder, remote: true, cloud: true).contains(.action(.shareLink)))
        XCTAssertFalse(cmds(.file, remote: true, cloud: false).contains(.action(.shareLink)))
        XCTAssertFalse(cmds(.file, remote: false, cloud: false).contains(.action(.shareLink)))
        XCTAssertFalse(cmds(.empty, remote: true, cloud: true).contains(.action(.shareLink)))
    }

    func testRcloneLinkRunsRcloneAndReportsFailure() async throws {
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("budis-link-" + UUID().uuidString)
        defer { try? fm.removeItem(at: base) }
        try fm.createDirectory(at: base, withIntermediateDirectories: true)
        let script = base.appendingPathComponent("rclone")
        try """
        #!/bin/bash
        if [ "$1" != link ]; then exit 2; fi
        case "$2" in
          nuceny:*) echo 'ERROR : Failed to link: this backend can not create public links' >&2; exit 1;;
          *) echo "https://share.test/$2";;
        esac
        """.write(to: script, atomically: true, encoding: .utf8)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)

        let ok = RemoteConnection(proto: .rclone, host: "gdrive", port: 0, user: "", password: "", insecure: false, rcloneExe: script.path)
        let link = try await ok.rcloneLink("/Fotky/a.jpg")
        XCTAssertEqual(link, "https://share.test/gdrive:Fotky/a.jpg")

        let bad = RemoteConnection(proto: .rclone, host: "nuceny", port: 0, user: "", password: "", insecure: false, rcloneExe: script.path)
        do { _ = try await bad.rcloneLink("/x"); XCTFail("mělo selhat") }
        catch { XCTAssertTrue(error.localizedDescription.contains("public links")) }
    }
}
