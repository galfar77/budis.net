import SwiftUI
import CryptoKit

struct FileHashes: Sendable {
    var md5 = ""
    var sha1 = ""
    var sha256 = ""
}

enum FileHasher {
    private static func hex<D: Sequence>(_ digest: D) -> String where D.Element == UInt8 {
        digest.map { String(format: "%02x", $0) }.joined()
    }

    static func hashes(of url: URL) -> FileHashes? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        var md5 = Insecure.MD5(), sha1 = Insecure.SHA1(), sha256 = SHA256()
        while let data = try? handle.read(upToCount: 1 << 20), !data.isEmpty {
            md5.update(data: data)
            sha1.update(data: data)
            sha256.update(data: data)
        }
        return FileHashes(md5: hex(md5.finalize()), sha1: hex(sha1.finalize()), sha256: hex(sha256.finalize()))
    }
}

struct ChecksumSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var results: [String: FileHashes] = [:]
    @State private var running = true
    @State private var expected = ""

    private var items: [FileItem] { model.checksumItems }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Kontrolní součty").font(.headline)
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(items) { item in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.name).font(.system(size: 12, weight: .semibold))
                            if let h = results[item.id] {
                                line("MD5", h.md5)
                                line("SHA-1", h.sha1)
                                line("SHA-256", h.sha256)
                            } else if running {
                                ProgressView().controlSize(.small)
                            } else {
                                Text("Soubor se nepodařilo přečíst.").foregroundStyle(.red)
                            }
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(height: 240)

            HStack {
                TextField("Očekávaný součet (vložte pro ověření)", text: $expected)
                    .textFieldStyle(.roundedBorder)
                verdict
            }

            HStack {
                Button("Kopírovat vše") { copyAll() }.disabled(results.isEmpty)
                Button("Uložit .sha256 vedle souborů") { saveSidecars() }.disabled(results.isEmpty)
                Spacer()
                Button("Zavřít") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 640)
        .task {
            let urls = items.map { ($0.id, $0.url) }
            for (id, url) in urls {
                let h = await Task.detached { FileHasher.hashes(of: url) }.value
                if let h { results[id] = h }
            }
            running = false
        }
    }

    private func line(_ label: String, _ value: String) -> some View {
        HStack(spacing: 8) {
            Text(label).frame(width: 60, alignment: .leading).foregroundStyle(.secondary)
            Text(value).textSelection(.enabled)
        }
        .font(.system(size: 11, design: .monospaced))
    }

    @ViewBuilder
    private var verdict: some View {
        let e = expected.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if !e.isEmpty {
            let match = results.values.contains { [$0.md5, $0.sha1, $0.sha256].contains(e) }
            Label(match ? "Shoduje se" : "Neshoduje se", systemImage: match ? "checkmark.circle.fill" : "xmark.circle.fill")
                .foregroundStyle(match ? Color.green : Color.red)
                .font(.system(size: 12, weight: .semibold))
        }
    }

    private func copyAll() {
        var text = ""
        for item in items {
            guard let h = results[item.id] else { continue }
            text += "\(item.name)\nMD5     \(h.md5)\nSHA-1   \(h.sha1)\nSHA-256 \(h.sha256)\n\n"
        }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    private func saveSidecars() {
        var errors: [String] = []
        for item in items {
            guard let h = results[item.id] else { continue }
            let file = item.url.appendingPathExtension("sha256")
            do { try "\(h.sha256)  \(item.name)\n".write(to: file, atomically: true, encoding: .utf8) }
            catch { errors.append("\(item.name): \(error.localizedDescription)") }
        }
        if errors.isEmpty { model.active.reload() } else { Dialogs.error(errors.joined(separator: "\n")) }
    }
}
