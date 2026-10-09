import Foundation

enum LocalFS {
    /// Součet velikostí obyčejných souborů (rekurzivně u adresářů).
    static func totalSize(_ url: URL) -> Int64 {
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { return 0 }
        if !isDir.boolValue {
            return Int64((try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0)
        }
        var sum: Int64 = 0
        let keys: [URLResourceKey] = [.fileSizeKey, .isRegularFileKey]
        if let en = fm.enumerator(at: url, includingPropertiesForKeys: keys) {
            for case let u as URL in en {
                if let v = try? u.resourceValues(forKeys: Set(keys)), v.isRegularFile == true {
                    sum += Int64(v.fileSize ?? 0)
                }
            }
        }
        return sum
    }

    static func sameVolume(_ a: URL, _ b: URL) -> Bool {
        guard let ia = (try? a.resourceValues(forKeys: [.volumeIdentifierKey]))?.volumeIdentifier,
              let ib = (try? b.resourceValues(forKeys: [.volumeIdentifierKey]))?.volumeIdentifier else { return false }
        return ia.isEqual(ib)
    }

    /// Zkopíruje soubor či adresář systémovou kopií a mezitím sleduje velikost cíle,
    /// podle které hlásí průběh.
    static func copy(from source: URL, to dest: URL,
                     progress: @escaping @Sendable (String, Double) -> Void) async throws {
        let name = source.lastPathComponent
        let work = Task.detached { () -> Void in
            try FileManager.default.copyItem(at: source, to: dest)
        }
        let poller = Task.detached {
            let total = max(totalSize(source), 1)
            while !Task.isCancelled {
                let done = totalSize(dest)
                progress(name, min(Double(done) / Double(total), 1))
                try? await Task.sleep(nanoseconds: 400_000_000)
            }
        }
        defer { poller.cancel() }
        try await work.value
    }
}
