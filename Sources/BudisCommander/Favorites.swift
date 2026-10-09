import SwiftUI

/// Oblíbené složky a naposledy navštívené adresáře.
@MainActor
final class FavoritesStore: ObservableObject {
    static let shared = FavoritesStore()

    @Published private(set) var favorites: [String]
    @Published private(set) var recents: [String]

    private init() {
        let d = UserDefaults.standard
        favorites = d.stringArray(forKey: "favorites") ?? []
        recents = d.stringArray(forKey: "recents") ?? []
    }

    func add(_ path: String) {
        guard !favorites.contains(path) else { return }
        favorites.append(path)
        UserDefaults.standard.set(favorites, forKey: "favorites")
    }

    func remove(_ path: String) {
        favorites.removeAll { $0 == path }
        UserDefaults.standard.set(favorites, forKey: "favorites")
    }

    func visited(_ url: URL) {
        let path = url.path
        recents.removeAll { $0 == path }
        recents.insert(path, at: 0)
        if recents.count > 20 { recents.removeLast(recents.count - 20) }
        UserDefaults.standard.set(recents, forKey: "recents")
    }

    static var builtIn: [(String, String)] {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return [("Domů", home), ("Plocha", home + "/Desktop"), ("Dokumenty", home + "/Documents"),
                ("Stažené", home + "/Downloads"), ("Aplikace", "/Applications"), ("Disky", "/Volumes")]
    }
}

struct FavoritesSheet: View {
    @ObservedObject var model: AppModel
    @ObservedObject var store = FavoritesStore.shared
    @Environment(\.dismiss) private var dismiss

    private func go(_ path: String) {
        model.active.navigate(to: URL(fileURLWithPath: path))
        dismiss()
    }

    private func row(_ title: String, _ path: String, removable: Bool = false) -> some View {
        HStack {
            Button { go(path) } label: {
                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                    if title != path { Text(path).font(.system(size: 10)).foregroundStyle(.secondary) }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.plain)
            if removable {
                Button { store.remove(path) } label: { Image(systemName: "minus.circle") }
                    .buttonStyle(.plain)
                    .help("Odebrat z oblíbených")
            }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Oblíbené a poslední složky").font(.headline)
                Spacer()
                Button("Přidat aktuální složku") { store.add(model.active.url.path) }
                    .disabled(model.active.connection != nil || model.active.isArchive)
            }
            List {
                Section("Rychlý přístup") {
                    ForEach(FavoritesStore.builtIn, id: \.1) { row($0.0, $0.1) }
                }
                if !store.favorites.isEmpty {
                    Section("Oblíbené") {
                        ForEach(store.favorites, id: \.self) { row(($0 as NSString).lastPathComponent, $0, removable: true) }
                    }
                }
                if !store.recents.isEmpty {
                    Section("Naposledy navštívené") {
                        ForEach(store.recents, id: \.self) { row($0, $0) }
                    }
                }
            }
            .frame(height: 340)
            HStack {
                Text("Zpět a vpřed v historii panelu: ⌘[ a ⌘].").font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button("Zavřít") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 480)
    }
}
