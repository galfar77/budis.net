import SwiftUI

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
}

/// Obsah místní nabídky (pravé tlačítko) v seznamu souborů podle toho, co je pod ukazatelem.
enum ContextMenus {
    static func entries(kind: ContextKind, remote: Bool) -> [ContextEntry] {
        var list: [ContextEntry] = []
        func add(_ c: ContextCommand...) { list.append(contentsOf: c.map { .command($0) }) }
        func act(_ a: ShortcutAction...) { list.append(contentsOf: a.map { .command(.action($0)) }) }
        func local(_ a: ShortcutAction...) { if !remote { list.append(contentsOf: a.map { .command(.action($0)) }) } }
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
            add(.rename, .delete); sep()
            local(.pack, .dirSizes, .attributes, .tags)
        case .archive:
            add(.open); sep()
            add(.copy, .move); sep()
            local(.copyFiles, .cutFiles); act(.copyPath, .copyName); sep()
            add(.rename, .delete); sep()
            local(.unpack, .pack, .checksum, .attributes, .tags)
        case .file:
            add(.open, .view, .edit); sep()
            add(.copy, .move); sep()
            local(.copyFiles, .cutFiles); act(.copyPath, .copyName); sep()
            add(.rename, .delete); sep()
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
