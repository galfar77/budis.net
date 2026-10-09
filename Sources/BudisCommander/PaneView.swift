import SwiftUI

struct PaneView: View {
    @ObservedObject var pane: PaneState
    let isActive: Bool
    let onActivate: () -> Void
    @ObservedObject var settings = Settings.shared

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
                if pane.connection != nil {
                    Button { pane.disconnect() } label: { Image(systemName: "eject.fill") }
                        .buttonStyle(.plain)
                        .help("Odpojit od serveru")
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
                        .help("Zrušit filtr (Esc)")
                }
                .padding(.horizontal, 8)
                .frame(height: 22)
                .background(Color.yellow.opacity(0.25))
            }

            columnHeader

            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(Array(pane.items.enumerated()), id: \.element.id) { index, item in
                            row(item, index: index)
                                .id(item.id)
                                .contentShape(Rectangle())
                                .onTapGesture(count: 2) {
                                    onActivate(); pane.cursor = index; pane.enter()
                                }
                                .onTapGesture {
                                    onActivate(); pane.cursor = index
                                }
                        }
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
    }

    private var columnHeader: some View {
        HStack(spacing: 6) {
            headerButton("Název", .name).frame(maxWidth: .infinity, alignment: .leading)
            if settings.showExt {
                Text("Přípona").font(.system(size: 11, weight: .semibold)).frame(width: 64, alignment: .leading)
            }
            headerButton("Velikost", .size).frame(width: 80, alignment: .trailing)
            headerButton("Změněno", .date).frame(width: 130, alignment: .trailing)
        }
        .padding(.horizontal, 8)
        .frame(height: 22)
        .background(Color.secondary.opacity(0.1))
    }

    private func headerButton(_ title: String, _ key: SortKey) -> some View {
        Button {
            pane.setSort(key)
        } label: {
            Text(title + (pane.sortKey == key ? (pane.ascending ? " ▲" : " ▼") : ""))
                .font(.system(size: 11, weight: .semibold))
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
            if settings.showExt {
                Text(ext).foregroundStyle(.secondary).frame(width: 64, alignment: .leading)
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
        .background(isCursor ? (isActive ? Color.accentColor.opacity(0.4) : Color.secondary.opacity(0.3)) : .clear)
    }
}
