import Foundation

enum Shell {
    /// Spustí externí příkaz mimo hlavní vlákno; při chybě vyhodí text z jeho stderr.
    static func run(_ executable: String, _ args: [String], cwd: URL? = nil) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            DispatchQueue.global().async {
                let p = Process()
                p.executableURL = URL(fileURLWithPath: executable)
                p.arguments = args
                p.currentDirectoryURL = cwd
                let err = Pipe()
                p.standardError = err
                p.standardOutput = FileHandle.nullDevice
                do { try p.run() } catch { cont.resume(throwing: error); return }
                let data = err.fileHandleForReading.readDataToEndOfFile()
                p.waitUntilExit()
                if p.terminationStatus == 0 {
                    cont.resume()
                } else {
                    let msg = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                    cont.resume(throwing: RemoteError(message: msg.isEmpty ? "Příkaz skončil s kódem \(p.terminationStatus)" : msg))
                }
            }
        }
    }
}
