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
                                .onTapGesture(count: 2) {
                                    onActivate(); pane.cursor = index; pane.enter()
                                }
                                .onTapGesture {
                                    onActivate(); pane.cursor = index
                                }
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
            .background(isActive ? Color.accentColor.opacity(0.35) : Color.secondary.opacity(0.15))

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
            }

            Text(pane.summary)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .frame(maxWidth: .infinity, alignment: .leading)
                .frame(height: 20)
                .background(Color.secondary.opacity(0.1))
        }
        .overlay(Rectangle().stroke(isActive ? Color.accentColor : .clear, lineWidth: 1))
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

    private var columnHeader: some View {
        HStack(spacing: 6) {
            headerButton(L("Název"), .name).frame(maxWidth: .infinity, alignment: .leading)
            if settings.showExt {
                headerButton(L("Přípona"), .type).frame(width: 64, alignment: .leading)
            }
            if settings.showPerms {
                Text(L("Práva")).font(.system(size: 11, weight: .semibold)).frame(width: 78, alignment: .leading)
            }
            if settings.showOwner {
                Text(L("Vlastník")).font(.system(size: 11, weight: .semibold)).frame(width: 70, alignment: .leading)
            }
            if settings.showMedia {
                Text(L("Rozměry/délka")).font(.system(size: 11, weight: .semibold)).frame(width: 84, alignment: .trailing)
            }
            headerButton(L("Velikost"), .size, alignment: .trailing).frame(width: 80, alignment: .trailing)
            headerButton(L("Změněno"), .date, alignment: .trailing).frame(width: 130, alignment: .trailing)
        }
        .padding(.horizontal, 8)
        .frame(height: 22)
        .background(Color.secondary.opacity(0.1))
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
                    .foregroundStyle(item.isDirectory ? Color.blue : Color.secondary)
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
            if settings.showExt {
                Text(ext).foregroundStyle(.secondary).frame(width: 64, alignment: .leading)
            }
            if settings.showPerms || settings.showOwner || settings.showMedia {
                ExtraCells(item: item, generation: pane.generation, settings: settings)
            }
            Text(sizeText)
                .frame(width: 80, alignment: .trailing)
            Text(item.modified.map { $0.formatted(date: .numeric, time: .shortened) } ?? "")
                .frame(width: 130, alignment: .trailing)
        }
        .font(.system(size: settings.fontSize))
        .monospacedDigit()
        .foregroundStyle(isMarked ? Color.red : Color.primary)
        .padding(.horizontal, 8)
        .frame(height: rowHeight)
        .background(dropRow == item.id ? Color.green.opacity(0.35)
                    : (isCursor ? (isActive ? Color.accentColor.opacity(0.4) : Color.secondary.opacity(0.3)) : .clear))
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
