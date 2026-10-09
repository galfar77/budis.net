import Foundation

private final class OutBox: @unchecked Sendable { var data = Data() }

enum Shell {
    /// Spustí externí příkaz mimo hlavní vlákno; při chybě vyhodí text z jeho výstupu.
    static func run(_ executable: String, _ args: [String], cwd: URL? = nil) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            DispatchQueue.global().async {
                let p = Process()
                p.executableURL = URL(fileURLWithPath: executable)
                p.arguments = args
                p.currentDirectoryURL = cwd
                p.standardInput = FileHandle.nullDevice
                let err = Pipe(), out = Pipe()
                p.standardError = err
                p.standardOutput = out
                do { try p.run() } catch { cont.resume(throwing: error); return }

                let outBox = OutBox()
                let group = DispatchGroup()
                group.enter()
                DispatchQueue.global().async {
                    outBox.data = out.fileHandleForReading.readDataToEndOfFile()
                    group.leave()
                }
                let errData = err.fileHandleForReading.readDataToEndOfFile()
                p.waitUntilExit()
                group.wait()

                if p.terminationStatus == 0 {
                    cont.resume()
                } else {
                    var msg = String(decoding: errData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                    if msg.isEmpty { msg = String(decoding: outBox.data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines) }
                    cont.resume(throwing: RemoteError(message: msg.isEmpty ? "Příkaz skončil s kódem \(p.terminationStatus)" : msg))
                }
            }
        }
    }
}
