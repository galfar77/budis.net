import Foundation

/// Úprava souboru uvnitř archivu: zapsání změněného souboru zpět do zipu (ostatní formáty jsou jen pro čtení).
enum ArchiveEdit {
    static func canUpdate(_ archive: URL) -> Bool {
        ["zip", "jar"].contains(archive.pathExtension.lowercased())
    }

    /// Název položky v archivu podle cesty souboru v rozbalené dočasné složce.
    static func entryName(root: URL, file: URL) -> String {
        let prefix = root.standardizedFileURL.path.hasSuffix("/") ? root.standardizedFileURL.path : root.standardizedFileURL.path + "/"
        let path = file.standardizedFileURL.path
        return path.hasPrefix(prefix) ? String(path.dropFirst(prefix.count)) : file.lastPathComponent
    }

    /// Nahradí (nebo přidá) položku v zipu souborem z rozbalené složky; používá systémový příkaz zip.
    static func replaceInZip(archive: URL, root: URL, entry: String) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        p.currentDirectoryURL = root
        p.arguments = ["-q", archive.path, entry]
        let err = Pipe()
        p.standardError = err
        p.standardOutput = FileHandle.nullDevice
        try p.run()
        let data = err.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else {
            throw RemoteError(message: String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }
}
