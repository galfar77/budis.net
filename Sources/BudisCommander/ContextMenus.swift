import SwiftUI
import AppKit
import UniformTypeIdentifiers

/// Co je pod ukazatelem při kliknutí pravým tlačítkem.
enum ContextKind { case empty, file, archive, folder, multiple }

/// Příkaz v místní nabídce: buď vlastní funkce aplikace, nebo akce se zkratkou.
enum ContextCommand: Hashable {
    case open, view, edit, copy, move, rename, delete, mkdir, unmark
    case action(ShortcutAction)

    var title: String {
        switch self {
        case .open: return L("Otevřít")
        case .view: return L("Zobrazit")
        case .edit: return L("Editovat")
        case .copy: return L("Kopírovat do druhého panelu")
        case .move: return L("Přesunout do druhého panelu")
        case .rename: return L("Přejmenovat")
        case .delete: return L("Smazat")
        case .mkdir: return L("Nová složka")
        case .unmark: return L("Zrušit označení")
        case .action(let a): return a.title
        }
    }
}

/// Položka nabídky: příkaz nebo oddělovač.
enum ContextEntry: Hashable {
    case command(ContextCommand)
    case divider
    /// Podnabídka „Otevřít v aplikaci“ se seznamem programů pro daný soubor.
    case openWith
}

/// Obsah místní nabídky (pravé tlačítko) v seznamu souborů podle toho, co je pod ukazatelem.
enum ContextMenus {
    static func entries(kind: ContextKind, remote: Bool, cloud: Bool = false) -> [ContextEntry] {
        var list: [ContextEntry] = []
        func add(_ c: ContextCommand...) { list.append(contentsOf: c.map { .command($0) }) }
        func act(_ a: ShortcutAction...) { list.append(contentsOf: a.map { .command(.action($0)) }) }
        func local(_ a: ShortcutAction...) { if !remote { list.append(contentsOf: a.map { .command(.action($0)) }) } }
        func cloudOnly(_ a: ShortcutAction...) { if cloud { list.append(contentsOf: a.map { .command(.action($0)) }) } }
        func sep() { if let last = list.last, last != .divider { list.append(.divider) } }

        switch kind {
        case .empty:
            add(.mkdir); local(.pasteFiles); sep()
            act(.refresh, .markAll, .invertMarks); sep()
            act(.hidden); local(.dirSizes, .terminal); sep()
            act(.favorites)
        case .multiple:
            add(.copy, .move); sep()
            local(.copyFiles, .cutFiles); act(.copyPath, .copyName); sep()
            local(.batchRename); add(.delete); sep()
            local(.pack, .attributes, .tags); sep()
            act(.invertMarks); add(.unmark)
        case .folder:
            add(.open); sep()
            add(.copy, .move); sep()
            local(.copyFiles, .cutFiles, .pasteFiles); act(.copyPath); sep()
            add(.rename, .delete); cloudOnly(.shareLink); sep()
            local(.pack, .dirSizes, .attributes, .tags)
        case .archive:
            add(.open); if !remote { list.append(.openWith) }; sep()
            add(.copy, .move); sep()
            local(.copyFiles, .cutFiles); act(.copyPath, .copyName); sep()
            add(.rename, .delete); cloudOnly(.shareLink); sep()
            local(.unpack, .pack, .checksum, .attributes, .tags)
        case .file:
            add(.open, .view, .edit); if !remote { list.append(.openWith) }; sep()
            add(.copy, .move); sep()
            local(.copyFiles, .cutFiles); act(.copyPath, .copyName); sep()
            add(.rename, .delete); cloudOnly(.shareLink); sep()
            local(.quickLook, .pack, .checksum, .attributes, .tags)
        }
        while list.last == .divider { list.removeLast() }
        return list
    }

    @MainActor
    static func kind(markedCount: Int, underIsMarked: Bool, under: FileItem?) -> ContextKind {
        if markedCount > 1, under == nil || underIsMarked { return .multiple }
        guard let under, !under.isParent else { return .empty }
        if under.isDirectory { return .folder }
        return ArchiveInfo.isArchive(under.name) ? .archive : .file
    }
}

/// Programy, kterými jde soubor otevřít (jako „Otevřít v aplikaci“ ve Finderu).
enum OpenWith {
    static func apps(for url: URL) -> [URL] {
        var list = NSWorkspace.shared.urlsForApplications(toOpen: url)
        if let def = NSWorkspace.shared.urlForApplication(toOpen: url), let i = list.firstIndex(of: def) {
            list.remove(at: i); list.insert(def, at: 0)
        }
        return Array(list.prefix(15))
    }

    static func name(_ app: URL) -> String {
        FileManager.default.displayName(atPath: app.path).replacingOccurrences(of: ".app", with: "")
    }

    @MainActor
    static func open(_ urls: [URL], with app: URL) {
        NSWorkspace.shared.open(urls, withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration())
    }

    /// Nechá vybrat libovolný program ze složky Aplikace.
    @MainActor
    static func chooseAndOpen(_ urls: [URL]) {
        let panel = NSOpenPanel()
        panel.title = L("Vyberte aplikaci")
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        panel.allowedContentTypes = [.application]
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let app = panel.url { open(urls, with: app) }
    }
}

struct OpenWithMenu: View {
    let urls: [URL]

    var body: some View {
        Menu(L("Otevřít v aplikaci")) {
            ForEach(urls.first.map { OpenWith.apps(for: $0) } ?? [], id: \.self) { app in
                Button(OpenWith.name(app)) { OpenWith.open(urls, with: app) }
            }
            Divider()
            Button(L("Jiná aplikace…")) { OpenWith.chooseAndOpen(urls) }
        }
    }
}
