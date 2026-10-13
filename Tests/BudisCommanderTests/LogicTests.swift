import XCTest
@testable import BudisCommander

private final class Box<T>: @unchecked Sendable {
    var value: T?
}

@MainActor
final class TranslationTests: XCTestCase {
    override func setUp() { Tr.language = "cs" }
    override func tearDown() { Tr.language = "cs" }

    func testCzechIsUnchanged() {
        XCTAssertEqual(L("Přejmenovat"), "Přejmenovat")
    }

    func testEnglishExactPatternAndPrefix() {
        Tr.language = "en"
        XCTAssertEqual(L("Přejmenovat"), "Rename")
        XCTAssertEqual(L("Označeno 3 z 10, 1 MB"), "Marked 3 of 10, 1 MB")
        XCTAssertEqual(L("Kopíruji: a.txt"), "Copying: a.txt")
        XCTAssertEqual(L("Něco neznámého"), "Něco neznámého")
    }

    func testEveryInfoLineHasEnglish() {
        let lines = InfoTexts.news + InfoTexts.features
        Tr.language = "en"
        for line in lines {
            XCTAssertNotEqual(Tr.t(line), line, "Chybí překlad: \(line)")
        }
    }
}

final class MirrorPlanTests: XCTestCase {
    func testPlanCopiesNewAndChangedAndDeletesExtras() async throws {
        let now = Date()
        let src: TreeMirror.Lister = { path in
            switch path {
            case "/s":
                return [MirrorEntry(name: "a.txt", isDir: false, size: 3, date: now),
                        MirrorEntry(name: "sub", isDir: true, size: 0, date: nil),
                        MirrorEntry(name: "same.txt", isDir: false, size: 5, date: Date(timeIntervalSince1970: 0))]
            case "/s/sub":
                return [MirrorEntry(name: "b.txt", isDir: false, size: 1, date: now)]
            default:
                return []
            }
        }
        let dst: TreeMirror.Lister = { path in
            path == "/d" ? [MirrorEntry(name: "same.txt", isDir: false, size: 5, date: now),
                            MirrorEntry(name: "junk.txt", isDir: false, size: 1, date: now)] : []
        }
        let plan = try await TreeMirror.plan(src: src, srcRoot: "/s", dst: dst, dstRoot: "/d", tolerance: 120)
        XCTAssertEqual(plan.copies.map { $0.from }.sorted(), ["/s/a.txt", "/s/sub/b.txt"])
        XCTAssertEqual(plan.mkdirs, ["/d/sub"])
        XCTAssertEqual(plan.deletes.map { $0.path }, ["/d/junk.txt"])
        XCTAssertEqual(plan.bytes, 4)
    }

    func testNewerSourceWithSameSizeIsCopied() async throws {
        let src: TreeMirror.Lister = { _ in [MirrorEntry(name: "x", isDir: false, size: 2, date: Date())] }
        let dst: TreeMirror.Lister = { _ in [MirrorEntry(name: "x", isDir: false, size: 2, date: Date(timeIntervalSinceNow: -3600))] }
        let plan = try await TreeMirror.plan(src: src, srcRoot: "/a", dst: dst, dstRoot: "/b", tolerance: 120)
        XCTAssertEqual(plan.copies.count, 1)
    }
}

final class RcloneAndSearchTests: XCTestCase {
    func testParseDateDropsFractionAndKeepsZone() {
        XCTAssertEqual(Rclone.parseDate("2024-05-01T10:00:00.123456789Z")?.timeIntervalSince1970, 1_714_557_600)
        XCTAssertEqual(Rclone.parseDate("2024-05-01T12:00:00+02:00")?.timeIntervalSince1970, 1_714_557_600)
        XCTAssertNil(Rclone.parseDate("nesmysl"))
    }

    func testProgressParserReadsStats() {
        let box = Box<Double>()
        let parser = Rclone.progressParser { box.value = $0 }
        parser?("{\"level\":\"info\",\"stats\":{\"bytes\":50,\"totalBytes\":200}}")
        XCTAssertEqual(box.value, 0.25)
        parser?("obyčejný řádek bez statistik")
        XCTAssertEqual(box.value, 0.25)
    }

    func testRemoteNamesAndProviders() {
        XCTAssertTrue(RcloneSetup.isValidName("moje-uloziste_1"))
        XCTAssertFalse(RcloneSetup.isValidName("s mezerou"))
        XCTAssertFalse(RcloneSetup.isValidName("a:b"))
        XCTAssertFalse(RcloneSetup.isValidName(""))
        XCTAssertEqual(CloudProvider.all.count, Set(CloudProvider.all.map(\.id)).count)
    }

    func testSpotlightQuery() {
        XCTAssertEqual(FileSearch.spotlightQuery(mask: "*.jpg;report", text: ""),
                       "(kMDItemFSName == \"*.jpg\"c || kMDItemFSName == \"*report*\"c)")
        XCTAssertEqual(FileSearch.spotlightQuery(mask: "", text: "a\"b"),
                       "(kMDItemFSName == \"*\"c) && kMDItemTextContent == \"a\\\"b\"cd")
    }
}

@MainActor
final class StoresAndModelsTests: XCTestCase {
    func testFolderViewStoreRemembersAndForgets() {
        let path = "/tmp/budis-test-\(UUID().uuidString)"
        FolderViewStore.shared.save(sort: .size, ascending: false, for: path)
        XCTAssertEqual(FolderViewStore.shared.view(for: path), FolderView(sort: "size", ascending: false))
        FolderViewStore.shared.save(sort: .name, ascending: true, for: path)
        XCTAssertNil(FolderViewStore.shared.view(for: path))
    }

    func testTabSetStoreSavesReplacesAndDeletes() {
        let name = "test-\(UUID().uuidString)"
        let store = TabSetStore.shared
        store.save(TabSet(name: name, left: ["/a"], leftSel: 0, right: ["/b", "/c"], rightSel: 1))
        store.save(TabSet(name: name.uppercased(), left: ["/z"], leftSel: 0, right: ["/b"], rightSel: 0))
        XCTAssertEqual(store.sets.filter { $0.name.caseInsensitiveCompare(name) == .orderedSame }.count, 1)
        store.delete(name.uppercased())
        XCTAssertFalse(store.sets.contains { $0.name.caseInsensitiveCompare(name) == .orderedSame })
    }

    func testShortcutDefaultsAreUniqueAndTitled() {
        let chars = ShortcutAction.allCases.map(\.defaultChar).filter { !$0.isEmpty }
        XCTAssertEqual(chars.count, Set(chars).count, "Zkratky se opakují: \(chars)")
        for action in ShortcutAction.allCases { XCTAssertFalse(action.title.isEmpty) }
    }

    func testTypeFiltersAndThemes() {
        XCTAssertTrue(TypeFilter.images.extensions.contains("jpg"))
        XCTAssertTrue(TypeFilter.docs.extensions.contains("pdf"))
        XCTAssertFalse(TypeFilter.audio.extensions.contains("jpg"))
        XCTAssertEqual(ColorTheme.all.count, Set(ColorTheme.all.map(\.id)).count)
        XCTAssertEqual(ColorTheme.get("nesmysl").id, "default")
    }
}

final class ProcessListTests: XCTestCase {
    func testParsePsOutput() {
        let text = """
          1  0.0  12345 /sbin/launchd
         99  1.5    100 /Applications/My App.app/Contents/MacOS/My App
        nesmysl
        """
        let list = ProcessList.parse(text)
        XCTAssertEqual(list.map(\.pid), [1, 99])
        XCTAssertEqual(list.map(\.name), ["launchd", "My App"])
        XCTAssertEqual(list[1].cpu, 1.5)
        XCTAssertEqual(list[0].rssKB, 12345)
    }

    func testFilterAndSort() {
        let all = [ProcInfo(pid: 1, name: "b", cpu: 1, rssKB: 10), ProcInfo(pid: 2, name: "a", cpu: 5, rssKB: 5), ProcInfo(pid: 3, name: "c", cpu: 3, rssKB: 50)]
        XCTAssertEqual(ProcessList.filter(all, text: "", sort: .name).map(\.pid), [2, 1, 3])
        XCTAssertEqual(ProcessList.filter(all, text: "", sort: .cpu).map(\.pid), [2, 3, 1])
        XCTAssertEqual(ProcessList.filter(all, text: "", sort: .memory).map(\.pid), [3, 1, 2])
        XCTAssertEqual(ProcessList.filter(all, text: "3", sort: .name).map(\.pid), [3])
    }

    func testSnapshotContainsThisProcess() async {
        let me = ProcessInfo.processInfo.processIdentifier
        let all = await ProcessList.snapshot()
        XCTAssertTrue(all.contains { $0.pid == me })
    }
}

final class BatchRenameTokenTests: XCTestCase {
    private func one(_ item: RenameInput, _ o: RenameOptions) -> String { BatchRename.newNames(for: [item], o)[0] }

    func testDateParentAndRangeTokens() {
        var cal = DateComponents(); cal.year = 2025; cal.month = 7; cal.day = 9; cal.hour = 14; cal.minute = 5; cal.second = 3
        let date = Calendar.current.date(from: cal)
        let item = RenameInput(name: "Dovolená u moře.JPG", isDirectory: false, modified: date, parent: "Fotky")
        var o = RenameOptions(); o.mask = "[Y]-[M]-[D]_[h]-[m]-[s] [P].[E]"
        XCTAssertEqual(one(item, o), "2025-07-09_14-05-03 Fotky.JPG")
        o.mask = "[N1-3]-[N12-99].[E]"
        XCTAssertEqual(one(item, o), "Dov-moře.JPG")
        o.mask = "[N50-60].[E]"
        XCTAssertEqual(one(item, o), ".JPG")
        o.mask = "[Y]-[M]"
        XCTAssertEqual(one(RenameInput(name: "a.txt", isDirectory: false), o), "-")
    }

    func testDiacriticsAndSpacesOptions() {
        let item = RenameInput(name: "Příliš žluťoučký kůň Łódź.txt", isDirectory: false)
        var o = RenameOptions(); o.removeDiacritics = true; o.spacesToUnderscore = true
        XCTAssertEqual(one(item, o), "Prilis_zlutoucky_kun_Lodz.txt")
        var u = RenameOptions(); u.removeDiacritics = true; u.caseMode = 2
        XCTAssertEqual(one(item, u), "PRILIS ZLUTOUCKY KUN LODZ.TXT")
        XCTAssertEqual(one(item, RenameOptions()), "Příliš žluťoučký kůň Łódź.txt")
    }

    func testOldTupleApiStillWorks() {
        var o = RenameOptions(); o.mask = "[C]_[N].[E]"; o.start = 5; o.digits = 3
        XCTAssertEqual(BatchRename.newNames(for: [("a.jpg", false), ("b.jpg", false)], o), ["005_a.jpg", "006_b.jpg"])
    }
}
