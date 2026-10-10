import Foundation

/// Rychlý filtr podle druhu souboru; složky zůstávají vždy vidět.
enum TypeFilter: String, CaseIterable, Identifiable {
    case images, docs, audio, video, archives

    var id: String { rawValue }

    var label: String {
        switch self {
        case .images: return L("Jen obrázky")
        case .docs: return L("Jen dokumenty")
        case .audio: return L("Jen hudba")
        case .video: return L("Jen video")
        case .archives: return L("Jen archivy")
        }
    }

    var extensions: Set<String> {
        switch self {
        case .images: return ["jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "svg", "ico",
                              "raw", "cr2", "nef", "arw", "dng"]
        case .docs: return ["pdf", "doc", "docx", "odt", "rtf", "txt", "md", "pages", "xls", "xlsx", "ods", "csv",
                            "numbers", "ppt", "pptx", "odp", "key", "epub"]
        case .audio: return ["mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "aiff", "aif"]
        case .video: return ["mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "mpg", "mpeg", "flv"]
        case .archives: return ["zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "iso", "dmg"]
        }
    }
}

/// Řazení zapamatované pro jednu složku.
struct FolderView: Codable, Equatable {
    var sort: String
    var ascending: Bool
}

/// Pamatuje řazení zvlášť pro každou složku (jen ty, kde se liší od výchozího: název vzestupně).
@MainActor
final class FolderViewStore {
    static let shared = FolderViewStore()
    private static let key = "folderViews"
    private var views: [String: FolderView] = [:]

    private init() {
        if let data = UserDefaults.standard.data(forKey: Self.key),
           let decoded = try? JSONDecoder().decode([String: FolderView].self, from: data) {
            views = decoded
        }
    }

    func view(for path: String) -> FolderView? { views[path] }

    func save(sort: SortKey, ascending: Bool, for path: String) {
        if sort == .name && ascending {
            views.removeValue(forKey: path)
        } else {
            views[path] = FolderView(sort: sort.rawValue, ascending: ascending)
        }
        if let data = try? JSONEncoder().encode(views) {
            UserDefaults.standard.set(data, forKey: Self.key)
        }
    }
}
