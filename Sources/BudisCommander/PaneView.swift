import SwiftUI
import UniformTypeIdentifiers

/// Načte adresy souborů z přetažených položek.
enum DropLoader {
    static func urls(from providers: [NSItemProvider]) async -> [URL] {
        var out: [URL] = []
        for provider in providers {
            let url: URL? = await withCheckedContinuation { (c: CheckedContinuation<URL?, Never>) in
                _ = provider.loadObject(ofClass: URL.self) { url, _ in c.resume(returning: url) }
            }
            if let url, url.isFileURL { out.append(url) }
        }
        return out
    }
}

struct PaneView: View {
    @ObservedObject var pane: PaneState
    let isActive: Bool
    let model: AppModel
    let onActivate: () -> Void
    @ObservedObject var settings = Settings.shared
    @State private var paneDropTargeted = false
    @State private var dropRow: String?
    @State private var draftWidth: [String: Double] = [:]
    @State private var dragStart: [String: Double] = [:]

    @ViewBuilder
    private var rows: some View {
        ForEach(Array(pane.items.enumerated()), id: \.element.id) { index, item in
                            row(item, index: index)
                                .id(item.id)
                                .contentShape(Rectangle())
                                .onDrag {
                                    model.dragSource = pane
                                    guard pane.connection == nil, !item.isParent else { return NSItemProvider() }
                                    return NSItemProvider(object: item.url as NSURL)
                                }
                                .modifier(FolderDrop(item: item, pane: pane, model: model, dropRow: $dropRow, onActivate: onActivate))
                                // Jednoduché kliknutí se vyhodnocuje hned: kdyby šlo o onTapGesture vedle onTapGesture(count: 2),
                                // systém by čekal na možné druhé kliknutí a přepnutí panelu by se zpozdilo.
                                .simultaneousGesture(TapGesture().onEnded {
                                    onActivate(); pane.cursor = index
                                })
                                .simultaneousGesture(TapGesture(count: 2).onEnded {
                                    onActivate(); pane.cursor = index; pane.enter()
                                })
                                .contextMenu { contextMenu(for: item, index: index) }
                        }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                if pane.connection != nil {
                    Image(systemName: "network")
                }
                if pane.branch { Image(systemName: "list.bullet.indent") }
                Text(pane.title)
                    .font(.system(size: 12, weight: .medium))
                    .lineLimit(1)
                    .truncationMode(.head)
                Spacer(minLength: 0)
                if pane.isLoading { ProgressView().controlSize(.small) }
                Menu {
                    Button(L("Všechny soubory")) { pane.setTypeFilter(nil) }
                    Divider()
                    ForEach(TypeFilter.allCases) { t in
                        Button(t.label) { pane.setTypeFilter(t) }
                    }
                } label: {
                    Image(systemName: pane.typeFilter == nil ? "line.3.horizontal.decrease.circle"
                                                             : "line.3.horizontal.decrease.circle.fill")
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                .help(L("Filtr podle typu souboru"))
                if pane.connection != nil {
                    Button { pane.disconnect() } label: { Image(systemName: "eject.fill") }
                        .buttonStyle(.plain)
                        .help(L("Odpojit od serveru"))
                }
            }
            .padding(.horizontal, 8)
            .frame(height: 24)
            .background(isActive ? Palette.accent.opacity(0.35) : Color.secondary.opacity(0.15))
            .liquidGlass(cornerRadius: 6)

            if !pane.filter.isEmpty {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass")
                    Text(pane.filter).font(.system(size: 12, weight: .semibold))
                    Spacer()
                    Button { pane.clearFilter() } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain)
                        .help(L("Zrušit filtr (Esc)"))
                }
                .padding(.horizontal, 8)
                .frame(height: 22)
                .background(Color.yellow.opacity(0.25))
            }

            columnHeader

            ScrollViewReader { proxy in
                ScrollView {
                    // LazyVStack se při skocích scrollTo občas „vyprázdní“ (řádky zmizí), proto se pro běžně
                    // velké složky použije obyčejný VStack; líný jen pro opravdu velké.
                    if pane.items.count <= 3000 {
                        VStack(spacing: 0) { rows }
                    } else {
                        LazyVStack(spacing: 0) { rows }
                    }
                }
                .onChange(of: pane.cursor) { _, new in
                    if pane.items.indices.contains(new) { proxy.scrollTo(pane.items[new].id) }
                }
                .contextMenu { contextMenu(for: nil, index: nil) }
            }

            Text(pane.summary)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .frame(maxWidth: .infinity, alignment: .leading)
                .frame(height: 20)
                .background(Color.secondary.opacity(0.1))
        }
        .overlay(Rectangle().stroke(isActive ? Palette.accent : .clear, lineWidth: 1))
        .overlay(Rectangle().stroke(Color.green, lineWidth: paneDropTargeted ? 3 : 0))
        .onDrop(of: [.fileURL], isTargeted: $paneDropTargeted) { providers in
            onActivate()
            Task {
                let urls = await DropLoader.urls(from: providers)
                model.dropFiles(urls, onto: pane, folder: nil)
            }
            return true
        }
    }

    /// Místní nabídka pro řádek (nebo pro prázdné místo v panelu, když je `item` nil).
    @ViewBuilder
    private func contextMenu(for item: FileItem?, index: Int?) -> some View {
        let underMarked = item.map { pane.marked.contains($0.id) } ?? false
        let kind = ContextMenus.kind(markedCount: pane.marked.count, underIsMarked: underMarked, under: item)
        let entries = ContextMenus.entries(kind: kind, remote: pane.connection != nil, cloud: pane.connection?.proto == .rclone)
        ForEach(Array(entries.enumerated()), id: \.offset) { _, entry in
            switch entry {
            case .divider:
                Divider()
            case .openWith:
                if let item { OpenWithMenu(urls: [item.url]) }
            case .command(let command):
                Button(command.title) { runContext(command, item: item, index: index) }
            }
        }
    }

    private func runContext(_ command: ContextCommand, item: FileItem?, index: Int?) {
        onActivate()
        // Pravé tlačítko mimo označené položky pracuje jen s položkou pod ukazatelem.
        if let item, let index, !pane.marked.contains(item.id) { pane.cursor = index }
        switch command {
        case .open: pane.enter()
        case .view: Task { await model.view() }
        case .edit: Task { await model.edit() }
        case .copy: model.startTransfer(move: false)
        case .move: model.startTransfer(move: true)
        case .rename: Task { await model.rename() }
        case .delete: model.startDelete()
        case .mkdir: Task { await model.makeDirectory() }
        case .unmark: pane.marked = []
        case .action(let action): model.perform(action)
        }
    }

    private var showExtra: Bool { settings.showPerms || settings.showOwner || settings.showMedia }
    private var visibleColumns: [String] { pane.columns.visible(showExt: settings.showExt, showExtra: showExtra) }
    private func colWidth(_ column: String) -> Double {
        if column == "extra" {
            return (settings.showPerms ? 78 : 0) + (settings.showOwner ? 70 : 0) + (settings.showMedia ? 84 : 0)
                + Double(max(0, [settings.showPerms, settings.showOwner, settings.showMedia].filter { $0 }.count - 1)) * 6
        }
        return draftWidth[column] ?? pane.columns.width(column)
    }

    private var columnHeader: some View {
        HStack(spacing: 6) {
            headerButton(L("Název"), .name).frame(maxWidth: .infinity, alignment: .leading)
            ForEach(visibleColumns, id: \.self) { column in
                headerCell(column)
                    .frame(width: colWidth(column), alignment: column == "ext" ? .leading : .trailing)
                    .overlay(alignment: .leading) { if column != "extra" { resizeHandle(column) } }
                    .contextMenu { headerMenu(column) }
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 22)
        .background(Color.secondary.opacity(0.1))
    }

    @ViewBuilder
    private func headerCell(_ column: String) -> some View {
        switch column {
        case "ext": headerButton(L("Přípona"), .type)
        case "size": headerButton(L("Velikost"), .size, alignment: .trailing)
        case "date": headerButton(L("Změněno"), .date, alignment: .trailing)
        default:
            HStack(spacing: 6) {
                if settings.showPerms { Text(L("Práva")).frame(width: 78, alignment: .leading) }
                if settings.showOwner { Text(L("Vlastník")).frame(width: 70, alignment: .leading) }
                if settings.showMedia { Text(L("Rozměry/délka")).frame(width: 84, alignment: .trailing) }
            }
            .font(.system(size: 11, weight: .semibold))
        }
    }

    /// Úchyt na levé hraně sloupce: tažením se mění šířka a po pustění se uloží pro tuto složku.
    private func resizeHandle(_ column: String) -> some View {
        Rectangle()
            .fill(Color.clear)
            .frame(width: 8)
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .global)
                .onChanged { value in
                    let start = dragStart[column] ?? pane.columns.width(column)
                    dragStart[column] = start
                    draftWidth[column] = min(max(start - value.translation.width, ColumnLayout.minWidth), ColumnLayout.maxWidth)
                }
                .onEnded { _ in
                    if let w = draftWidth[column] {
                        var layout = pane.columns
                        layout.resize(column, to: w)
                        pane.setColumns(layout)
                    }
                    draftWidth[column] = nil
                    dragStart[column] = nil
                })
    }

    @ViewBuilder
    private func headerMenu(_ column: String) -> some View {
        let shown = visibleColumns
        let idx = shown.firstIndex(of: column) ?? 0
        Button(L("Posunout sloupec doleva")) { moveColumn(column, by: -1) }.disabled(idx == 0)
        Button(L("Posunout sloupec doprava")) { moveColumn(column, by: 1) }.disabled(idx >= shown.count - 1)
        Divider()
        Button(L("Použít toto rozložení pro všechny složky")) { pane.setDefaultColumns(pane.columns) }
        Button(L("Výchozí rozložení této složky")) { pane.resetColumns() }.disabled(pane.customColumns == nil)
    }

    private func moveColumn(_ column: String, by delta: Int) {
        var layout = pane.columns
        if layout.move(column, by: delta, showExt: settings.showExt, showExtra: showExtra) { pane.setColumns(layout) }
    }

    private func headerButton(_ title: String, _ key: SortKey, alignment: Alignment = .leading) -> some View {
        Button {
            pane.setSort(key)
        } label: {
            Text(title + (pane.sortKey == key ? (pane.ascending ? " ▲" : " ▼") : ""))
                .font(.system(size: 11, weight: .semibold))
                .frame(maxWidth: .infinity, alignment: alignment)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private func rowCell(_ column: String, item: FileItem, ext: String, sizeText: String) -> some View {
        switch column {
        case "ext": Text(ext).foregroundStyle(.secondary).lineLimit(1)
        case "size": Text(sizeText).lineLimit(1)
        case "date": Text(item.modified.map { $0.formatted(date: .numeric, time: .shortened) } ?? "").lineLimit(1)
        default: ExtraCells(item: item, generation: pane.generation, settings: settings)
        }
    }

    private func row(_ item: FileItem, index: Int) -> some View {
        let isCursor = index == pane.cursor
        let isMarked = pane.marked.contains(item.id)
        let ext = item.isDirectory || item.isParent ? "" : (item.name as NSString).pathExtension
        let shownName: String
        if let sub = item.subpath { shownName = sub }
        else if settings.showExt && !ext.isEmpty { shownName = (item.name as NSString).deletingPathExtension }
        else { shownName = item.name }
        let rowHeight = settings.showThumbs ? max(settings.fontSize + 8, 34) : settings.fontSize + 8
        let sizeText: String
        if item.isDirectory {
            sizeText = pane.dirSizes[item.id].map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) }
                ?? (settings.autoDirSizes && pane.connection == nil && !item.isParent ? "…" : "‹DIR›")
        } else {
            sizeText = ByteCountFormatter.string(fromByteCount: item.size, countStyle: .file)
        }
        return HStack(spacing: 6) {
            if settings.showThumbs {
                ThumbView(item: item, side: 28)
            } else {
                Image(systemName: item.isParent ? "arrow.turn.left.up" : (item.isDirectory ? "folder.fill" : "doc"))
                    .foregroundStyle(item.isDirectory ? Palette.folder : Color.secondary)
                    .frame(width: 16)
            }
            Text(shownName)
                .lineLimit(1)
                .truncationMode(.middle)
                .fontWeight(item.isDirectory ? .semibold : .regular)
                .frame(maxWidth: .infinity, alignment: .leading)
            if settings.showTags && !item.isParent && item.remotePath == nil {
                TagDots(item: item, generation: pane.generation)
            }
            ForEach(visibleColumns, id: \.self) { column in
                rowCell(column, item: item, ext: ext, sizeText: sizeText)
                    .frame(width: colWidth(column), alignment: column == "ext" ? .leading : .trailing)
            }
        }
        .font(.system(size: settings.fontSize))
        .monospacedDigit()
        .foregroundStyle(isMarked ? Color.red : Color.primary)
        .padding(.horizontal, 8)
        .frame(height: rowHeight)
        .background(dropRow == item.id ? Color.green.opacity(0.35)
                    : (isCursor ? (isActive ? Palette.accent.opacity(0.4) : Color.secondary.opacity(0.3)) : .clear))
    }
}

/// Přijímá soubory puštěné na řádek se složkou (jen u lokálních panelů).
struct FolderDrop: ViewModifier {
    let item: FileItem
    let pane: PaneState
    let model: AppModel
    @Binding var dropRow: String?
    let onActivate: () -> Void

    func body(content: Content) -> some View {
        if item.isDirectory && pane.connection == nil && !pane.isArchive {
            content.onDrop(of: [.fileURL], isTargeted: Binding(
                get: { dropRow == item.id },
                set: { targeted in
                    if targeted { dropRow = item.id } else if dropRow == item.id { dropRow = nil }
                })) { providers in
                onActivate()
                Task {
                    let urls = await DropLoader.urls(from: providers)
                    model.dropFiles(urls, onto: pane, folder: item.url)
                }
                return true
            }
        } else {
            content
        }
    }
}
