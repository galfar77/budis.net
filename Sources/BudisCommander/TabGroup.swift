import SwiftUI

/// Sada záložek jednoho panelu.
@MainActor
final class TabGroup: ObservableObject {
    @Published private(set) var tabs: [PaneState]
    @Published var selected: Int

    var current: PaneState { tabs[selected] }

    init(urls: [URL], selected: Int) {
        tabs = urls.map { PaneState(url: $0) }
        self.selected = min(max(selected, 0), urls.count - 1)
    }

    /// Nahradí všechny záložky novými (otevření sady záložek).
    func replace(urls: [URL], selected: Int) {
        guard !urls.isEmpty else { return }
        tabs = urls.map { PaneState(url: $0) }
        self.selected = min(max(selected, 0), urls.count - 1)
        PaneState.onLocationChange?()
    }

    func newTab() {
        let t = PaneState(url: current.url)
        t.showHidden = current.showHidden
        tabs.insert(t, at: selected + 1)
        selected += 1
        PaneState.onLocationChange?()
    }

    func close(_ index: Int) {
        guard tabs.count > 1, tabs.indices.contains(index) else { return }
        tabs.remove(at: index)
        if index < selected { selected -= 1 }
        selected = min(selected, tabs.count - 1)
        PaneState.onLocationChange?()
    }

    func select(_ index: Int) {
        guard tabs.indices.contains(index) else { return }
        selected = index
        PaneState.onLocationChange?()
    }

    func cycle(_ delta: Int) {
        select((selected + delta + tabs.count) % tabs.count)
    }
}

struct PanelView: View {
    @ObservedObject var group: TabGroup
    let isActive: Bool
    let model: AppModel
    let onActivate: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            tabBar
            PaneView(pane: group.current, isActive: isActive, model: model, onActivate: onActivate)
                .id(ObjectIdentifier(group.current))
        }
    }

    private var tabBar: some View {
        HStack(spacing: 2) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 2) {
                    ForEach(Array(group.tabs.enumerated()), id: \.offset) { index, pane in
                        TabButton(pane: pane, selected: index == group.selected, canClose: group.tabs.count > 1,
                                  onSelect: { onActivate(); group.select(index) },
                                  onClose: { group.close(index) })
                    }
                }
            }
            Button { onActivate(); group.newTab() } label: { Image(systemName: "plus") }
                .buttonStyle(.plain)
                .help(L("Nová záložka (⌘T)"))
                .padding(.horizontal, 6)
        }
        .padding(.horizontal, 4)
        .frame(height: 26)
    }
}

struct TabButton: View {
    @ObservedObject var pane: PaneState
    let selected: Bool
    let canClose: Bool
    let onSelect: () -> Void
    let onClose: () -> Void

    var body: some View {
        HStack(spacing: 4) {
            Text(pane.tabTitle)
                .font(.system(size: 11))
                .lineLimit(1)
                .frame(maxWidth: 140)
            if canClose {
                Button(action: onClose) { Image(systemName: "xmark").font(.system(size: 8, weight: .bold)) }
                    .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 22)
        .background(selected ? Color.accentColor.opacity(0.3) : Color.secondary.opacity(0.12))
        .clipShape(RoundedRectangle(cornerRadius: 4))
        .contentShape(Rectangle())
        .onTapGesture(perform: onSelect)
    }
}
