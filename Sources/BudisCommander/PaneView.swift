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
        return HStack(spacing: 6) {
            Image(systemName: item.isParent ? "arrow.turn.left.up" : (item.isDirectory ? "folder.fill" : "doc"))
                .foregroundStyle(item.isDirectory ? Color.blue : Color.secondary)
                .frame(width: 16)
            Text(item.name)
                .lineLimit(1)
                .truncationMode(.middle)
                .fontWeight(item.isDirectory ? .semibold : .regular)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(item.isDirectory ? "‹DIR›" : ByteCountFormatter.string(fromByteCount: item.size, countStyle: .file))
                .frame(width: 80, alignment: .trailing)
            Text(item.modified.map { $0.formatted(date: .numeric, time: .shortened) } ?? "")
                .frame(width: 130, alignment: .trailing)
        }
        .font(.system(size: settings.fontSize))
        .monospacedDigit()
        .foregroundStyle(isMarked ? Color.red : Color.primary)
        .padding(.horizontal, 8)
        .frame(height: settings.fontSize + 8)
        .background(isCursor ? (isActive ? Color.accentColor.opacity(0.4) : Color.secondary.opacity(0.3)) : .clear)
    }
}
