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

    // MARK: Porovnání obsahu, hex, dělení a slepování

    /// Zda mají dva soubory shodný obsah (čte po 1 MB).
    static func sameContent(_ a: URL, _ b: URL) -> Bool {
        guard let ha = try? FileHandle(forReadingFrom: a), let hb = try? FileHandle(forReadingFrom: b) else { return false }
        defer { try? ha.close(); try? hb.close() }
        while true {
            let da = (try? ha.read(upToCount: 1 << 20)) ?? Data()
            let db = (try? hb.read(upToCount: 1 << 20)) ?? Data()
            if da != db { return false }
            if da.isEmpty { return true }
        }
    }

    /// Klasický výpis: offset, 16 bajtů hexadecimálně a ASCII.
    static func hexDump(_ data: Data) -> String {
        let bytes = [UInt8](data)
        var out = ""
        var offset = 0
        while offset < bytes.count {
            let chunk = bytes[offset..<min(offset + 16, bytes.count)]
            var hex = ""
            var ascii = ""
            for (i, b) in chunk.enumerated() {
                hex += String(format: "%02x ", b)
                if i == 7 { hex += " " }
                ascii += (b >= 32 && b < 127) ? String(UnicodeScalar(b)) : "."
            }
            hex += String(repeating: " ", count: max(0, 49 - hex.count))
            out += String(format: "%08x  ", offset) + hex + " |" + ascii + "|\n"
            offset += 16
        }
        return out
    }

    /// Rozdělí soubor na díly name.001, name.002… Při zrušení nebo chybě díly smaže.
    static func split(file: URL, partSize: Int64, into dir: URL, flag: CancelFlag,
                      progress: @Sendable (Double) -> Void) throws -> Int {
        let input = try FileHandle(forReadingFrom: file)
        defer { try? input.close() }
        let total = max(totalSize(file), 1)
        var done: Int64 = 0
        var created: [URL] = []
        do {
            while true {
                if flag.isSet { throw CancellationError() }
                let dest = dir.appendingPathComponent(file.lastPathComponent + String(format: ".%03d", created.count + 1))
                FileManager.default.createFile(atPath: dest.path, contents: nil)
                created.append(dest)
                let out = try FileHandle(forWritingTo: dest)
                var written: Int64 = 0
                while written < partSize {
                    if flag.isSet { try? out.close(); throw CancellationError() }
                    let n = Int(min(Int64(4 << 20), partSize - written))
                    guard let data = try input.read(upToCount: n), !data.isEmpty else { break }
                    try out.write(contentsOf: data)
                    written += Int64(data.count)
                    done += Int64(data.count)
                    progress(Double(done) / Double(total))
                }
                try out.close()
                if written == 0 {
                    try? FileManager.default.removeItem(at: dest)
                    created.removeLast()
                    break
                }
                if written < partSize { break }
            }
        } catch {
            for u in created { try? FileManager.default.removeItem(at: u) }
            throw error
        }
        return created.count
    }

    /// Spojí díly name.001, name.002… (první díl `first` končí na .001) do souboru `dest`.
    static func combine(first: URL, to dest: URL, flag: CancelFlag,
                        progress: @Sendable (Double) -> Void) throws {
        let dir = first.deletingLastPathComponent()
        let base = String(first.lastPathComponent.dropLast(4))
        var parts: [URL] = []
        while true {
            let u = dir.appendingPathComponent(base + String(format: ".%03d", parts.count + 1))
            guard FileManager.default.fileExists(atPath: u.path) else { break }
            parts.append(u)
        }
        let total = max(parts.reduce(Int64(0)) { $0 + totalSize($1) }, 1)
        FileManager.default.createFile(atPath: dest.path, contents: nil)
        let out = try FileHandle(forWritingTo: dest)
        var done: Int64 = 0
        do {
            for part in parts {
                let input = try FileHandle(forReadingFrom: part)
                defer { try? input.close() }
                while let data = try input.read(upToCount: 4 << 20), !data.isEmpty {
                    if flag.isSet { throw CancellationError() }
                    try out.write(contentsOf: data)
                    done += Int64(data.count)
                    progress(Double(done) / Double(total))
                }
            }
            try out.close()
        } catch {
            try? out.close()
            try? FileManager.default.removeItem(at: dest)
            throw error
        }
    }

    // MARK: Atributy

    struct AttributeChange: Sendable {
        var date: Date?
        var permissions: Int?
        var hidden: Bool?
        var recursive: Bool
    }

    /// Nastaví datum změny, práva a příznak „skrytý“; vrací chyby po položkách.
    static func apply(_ change: AttributeChange, to urls: [URL]) -> [String] {
        var errors: [String] = []
        func set(_ url: URL) {
            do {
                var attrs: [FileAttributeKey: Any] = [:]
                if let d = change.date { attrs[.modificationDate] = d }
                if let p = change.permissions { attrs[.posixPermissions] = p }
                if !attrs.isEmpty { try FileManager.default.setAttributes(attrs, ofItemAtPath: url.path) }
                if let h = change.hidden {
                    var u = url
                    var v = URLResourceValues()
                    v.isHidden = h
                    try u.setResourceValues(v)
                }
            } catch {
                errors.append("\(url.lastPathComponent): \(error.localizedDescription)")
            }
        }
        for url in urls {
            set(url)
            var isDir: ObjCBool = false
            if change.recursive, FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue,
               let en = FileManager.default.enumerator(at: url, includingPropertiesForKeys: nil) {
                for case let child as URL in en { set(child) }
            }
        }
        return errors
    }
}
