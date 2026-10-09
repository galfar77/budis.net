import Foundation

struct FileItem: Identifiable, Hashable {
    let url: URL
    let name: String
    let isDirectory: Bool
    let size: Int64
    let modified: Date?
    let isParent: Bool

    var id: String { isParent ? ".." : url.path }

    static func parent(of url: URL) -> FileItem {
        FileItem(url: url.deletingLastPathComponent(), name: "..", isDirectory: true,
                 size: 0, modified: nil, isParent: true)
    }

    static func load(_ url: URL) -> FileItem? {
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey, .contentModificationDateKey]
        guard let v = try? url.resourceValues(forKeys: keys) else { return nil }
        var isDir = v.isDirectory ?? false
        if v.isSymbolicLink == true {
            var d: ObjCBool = false
            if FileManager.default.fileExists(atPath: url.path, isDirectory: &d) { isDir = d.boolValue }
        }
        return FileItem(url: url, name: url.lastPathComponent, isDirectory: isDir,
                        size: Int64(v.fileSize ?? 0), modified: v.contentModificationDate, isParent: false)
    }
}
