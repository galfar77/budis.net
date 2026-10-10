import SwiftUI

/// Pojmenovaná sada záložek obou panelů.
struct TabSet: Codable, Identifiable, Hashable {
    var name: String
    var left: [String]
    var leftSel: Int
    var right: [String]
    var rightSel: Int

    var id: String { name }
}

@MainActor
final class TabSetStore: ObservableObject {
    static let shared = TabSetStore()
    private static let key = "tabSets"

    @Published private(set) var sets: [TabSet] = []

    private init() {
        if let data = UserDefaults.standard.data(forKey: Self.key),
           let list = try? JSONDecoder().decode([TabSet].self, from: data) {
            sets = list
        }
    }

    /// Uloží sadu; sada stejného jména (bez ohledu na velikost písmen) se přepíše.
    func save(_ set: TabSet) {
        sets.removeAll { $0.name.caseInsensitiveCompare(set.name) == .orderedSame }
        sets.append(set)
        sets.sort { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        persist()
    }

    func delete(_ name: String) {
        sets.removeAll { $0.name == name }
        persist()
    }

    private func persist() {
        if let data = try? JSONEncoder().encode(sets) {
            UserDefaults.standard.set(data, forKey: Self.key)
        }
    }
}

extension AppModel {
    /// Uloží aktuální záložky obou panelů pod jménem; záložky na serveru a v archivech se vynechají.
    func saveTabSet(_ name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        func snapshot(_ group: TabGroup) -> ([String], Int) {
            var paths: [String] = []
            var selected = 0
            for (index, tab) in group.tabs.enumerated() {
                if tab.connection != nil || tab.isArchive { continue }
                if index == group.selected { selected = paths.count }
                paths.append(tab.persistentURL.path)
            }
            return (paths, selected)
        }
        let (l, ls) = snapshot(leftTabs)
        let (r, rs) = snapshot(rightTabs)
        TabSetStore.shared.save(TabSet(name: trimmed, left: l, leftSel: ls, right: r, rightSel: rs))
    }

    /// Otevře sadu záložek v obou panelech; složky, které už neexistují, se vynechají.
    func openTabSet(_ set: TabSet) {
        let home = FileManager.default.homeDirectoryForCurrentUser
        func fix(_ paths: [String], _ selected: Int) -> ([URL], Int) {
            var urls: [URL] = []
            var sel = 0
            for (index, path) in paths.enumerated() {
                var isDir: ObjCBool = false
                guard FileManager.default.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue else { continue }
                if index == selected { sel = urls.count }
                urls.append(URL(fileURLWithPath: path))
            }
            if urls.isEmpty { urls = [home] }
            return (urls, sel)
        }
        let (l, ls) = fix(set.left, set.leftSel)
        let (r, rs) = fix(set.right, set.rightSel)
        leftTabs.replace(urls: l, selected: ls)
        rightTabs.replace(urls: r, selected: rs)
        saveState()
        showNotice(L("Sada záložek „\(set.name)“ otevřena."))
    }
}

struct TabSetsSheet: View {
    @ObservedObject var model: AppModel
    @ObservedObject var store = TabSetStore.shared
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(L("Sady záložek")).font(.headline)
                Spacer()
                Button(L("Uložit aktuální…")) {
                    if let name = Dialogs.prompt(L("Uložit sadu záložek"),
                                                 info: L("Zapíše záložky obou panelů (záložky na serveru se vynechají)."),
                                                 ok: L("Uložit")) {
                        model.saveTabSet(name)
                    }
                }
            }
            if store.sets.isEmpty {
                Text(L("Zatím není uložená žádná sada. Nastavte záložky v obou panelech a klikněte na „Uložit aktuální…“."))
                    .font(.system(size: 12)).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 120, alignment: .center)
            } else {
                List {
                    ForEach(store.sets) { set in
                        HStack {
                            Button {
                                model.openTabSet(set)
                                dismiss()
                            } label: {
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(set.name)
                                    Text("\(set.left.count) vlevo, \(set.right.count) vpravo")
                                        .font(.system(size: 10)).foregroundStyle(.secondary)
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .buttonStyle(.plain)
                            Button { store.delete(set.name) } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.plain)
                                .help(L("Smazat sadu"))
                        }
                    }
                }
                .frame(height: 300)
            }
            HStack {
                Text(L("Klepnutím sadu otevřete. Sada pamatuje záložky obou panelů."))
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button(L("Zavřít")) { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 480)
    }
}
