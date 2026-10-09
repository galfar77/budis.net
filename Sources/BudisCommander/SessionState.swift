import Foundation

/// Stav, který se pamatuje mezi spuštěními: adresáře záložek a aktivní panel.
struct SessionState: Codable {
    var left: [String]
    var leftSel: Int
    var right: [String]
    var rightSel: Int
    var activeLeft: Bool

    private static let key = "sessionState"

    static func load() -> SessionState? {
        guard let data = UserDefaults.standard.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(SessionState.self, from: data)
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) {
            UserDefaults.standard.set(data, forKey: Self.key)
        }
    }

    static func existingDirectories(_ paths: [String]?, fallback: URL) -> [URL] {
        let urls = (paths ?? []).map { URL(fileURLWithPath: $0) }.filter {
            var isDir: ObjCBool = false
            return FileManager.default.fileExists(atPath: $0.path, isDirectory: &isDir) && isDir.boolValue
        }
        return urls.isEmpty ? [fallback] : urls
    }
}
