import XCTest
@testable import BudisCommander

final class ContextMenuTests: XCTestCase {
    private func commands(_ kind: ContextKind, remote: Bool = false) -> [ContextCommand] {
        ContextMenus.entries(kind: kind, remote: remote).compactMap { entry -> ContextCommand? in
            if case .command(let c) = entry { return c }
            return nil
        }
    }

    func testFileMenuHasOpenCopyDeleteAndCleanDividers() {
        let entries = ContextMenus.entries(kind: .file, remote: false)
        let cmds = commands(.file)
        for c in [ContextCommand.open, .view, .edit, .copy, .move, .rename, .delete, .action(.checksum), .action(.attributes)] {
            XCTAssertTrue(cmds.contains(c), "\(c)")
        }
        XCTAssertNotEqual(entries.first, .divider)
        XCTAssertNotEqual(entries.last, .divider)
        for i in 1..<entries.count { XCTAssertFalse(entries[i] == .divider && entries[i - 1] == .divider) }
    }

    func testRemoteMenusOmitLocalOnlyCommands() {
        let local: [ContextCommand] = [.action(.pack), .action(.unpack), .action(.checksum), .action(.attributes), .action(.copyFiles),
                                       .action(.cutFiles), .action(.pasteFiles), .action(.terminal), .action(.dirSizes),
                                       .action(.batchRename), .action(.quickLook), .action(.tags)]
        for kind in [ContextKind.empty, .file, .archive, .folder, .multiple] {
            let cmds = commands(kind, remote: true)
            for c in local { XCTAssertFalse(cmds.contains(c), "\(kind) \(c)") }
        }
        XCTAssertTrue(commands(.empty, remote: true).contains(.mkdir))
    }

    func testArchiveOffersUnpackAndEmptyAreaOffersNewFolder() {
        XCTAssertTrue(commands(.archive).contains(.action(.unpack)))
        XCTAssertTrue(commands(.empty).contains(.mkdir))
        XCTAssertTrue(commands(.multiple).contains(.unmark))
    }

    @MainActor
    func testKindDependsOnItemAndSelection() {
        let url = URL(fileURLWithPath: "/tmp/a.txt")
        let file = FileItem(url: url, name: "a.txt", isDirectory: false, size: 1, modified: nil, isParent: false)
        let dir = FileItem(url: url, name: "d", isDirectory: true, size: 0, modified: nil, isParent: false)
        let zip = FileItem(url: url, name: "x.zip", isDirectory: false, size: 1, modified: nil, isParent: false)
        let parent = FileItem.parent(of: url)
        XCTAssertEqual(ContextMenus.kind(markedCount: 0, underIsMarked: false, under: nil), .empty)
        XCTAssertEqual(ContextMenus.kind(markedCount: 0, underIsMarked: false, under: parent), .empty)
        XCTAssertEqual(ContextMenus.kind(markedCount: 0, underIsMarked: false, under: file), .file)
        XCTAssertEqual(ContextMenus.kind(markedCount: 0, underIsMarked: false, under: dir), .folder)
        XCTAssertEqual(ContextMenus.kind(markedCount: 0, underIsMarked: false, under: zip), .archive)
        XCTAssertEqual(ContextMenus.kind(markedCount: 3, underIsMarked: true, under: file), .multiple)
        XCTAssertEqual(ContextMenus.kind(markedCount: 3, underIsMarked: false, under: file), .file)
    }
}
