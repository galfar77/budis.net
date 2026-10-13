import XCTest
@testable import BudisCommander

final class ServerBookTests: XCTestCase {
    private func s(_ host: String, name: String? = nil, group: String? = nil) -> SavedServer {
        SavedServer(proto: .sftp, host: host, port: 22, user: "u", insecure: false, keyPath: nil, passwordSaved: false, name: name, group: group)
    }

    func testTreePutsUngroupedFirstThenGroupsAlphabetically() {
        let list = [s("z.cz", group: "Práce"), s("a.cz"), s("m.cz", name: "Moje", group: "Doma"), s("b.cz", group: "práce")]
        let tree = SavedServer.tree(list)
        XCTAssertEqual(tree.map(\.group), ["", "Doma", "Práce"])
        XCTAssertEqual(tree[2].servers.count, 2)                                 // „Práce“ a „práce“ jsou jedna skupina
        XCTAssertTrue(s("m.cz", name: "Moje").displayName.hasPrefix("Moje"))
        XCTAssertEqual(s("x.cz").displayName, s("x.cz").title)
    }

    func testRememberReplacesInPlaceKeepsNameAndGroupAndTrimsOnlyUnmanaged() {
        var list = [s("a.cz", name: "Moje A", group: "Práce"), s("b.cz")]
        list = SavedServer.remember(s("a.cz"), in: list)
        XCTAssertEqual(list.count, 2)
        XCTAssertEqual(list[0].host, "a.cz"); XCTAssertEqual(list[0].name, "Moje A"); XCTAssertEqual(list[0].group, "Práce")
        for i in 0..<30 { list = SavedServer.remember(s("h\(i).cz"), in: list) }
        XCTAssertTrue(list.contains { $0.host == "a.cz" && $0.name == "Moje A" })
        XCTAssertEqual(list.filter { !$0.isManaged }.count, SavedServer.maxUnmanaged)
        XCTAssertEqual(list.first?.host, "h29.cz")
    }

    func testOldSavedServersWithoutNewFieldsStillDecode() throws {
        let old = #"[{"proto":"SFTP","host":"x.cz","port":22,"user":"u","insecure":false}]"#.data(using: .utf8)!
        let list = try JSONDecoder().decode([SavedServer].self, from: old)
        XCTAssertEqual(list.count, 1)
        XCTAssertNil(list[0].name); XCTAssertFalse(list[0].isManaged)
    }
}
