import Foundation
import CoreServices

/// Sleduje změny ve složce přes FSEvents a volá `handler` na hlavním vlákně.
final class DirWatcher {
    private var stream: FSEventStreamRef?
    private let handler: () -> Void
    private let basePath: String
    private let recursive: Bool

    init?(path: String, recursive: Bool, handler: @escaping () -> Void) {
        self.handler = handler
        self.basePath = URL(fileURLWithPath: path).resolvingSymlinksInPath().path
        self.recursive = recursive

        var context = FSEventStreamContext(version: 0, info: nil, retain: nil, release: nil, copyDescription: nil)
        context.info = Unmanaged.passUnretained(self).toOpaque()
        let callback: FSEventStreamCallback = { _, info, _, eventPaths, _, _ in
            guard let info else { return }
            let watcher = Unmanaged<DirWatcher>.fromOpaque(info).takeUnretainedValue()
            let paths = (unsafeBitCast(eventPaths, to: NSArray.self) as? [String]) ?? []
            watcher.received(paths)
        }
        let flags = UInt32(kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagUseCFTypes | kFSEventStreamCreateFlagNoDefer)
        guard let s = FSEventStreamCreate(nil, callback, &context, [basePath] as CFArray,
                                          FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 0.5, flags) else { return nil }
        stream = s
        FSEventStreamSetDispatchQueue(s, DispatchQueue.main)
        FSEventStreamStart(s)
    }

    private func received(_ paths: [String]) {
        // Změny hlouběji ve stromě se v obyčejném zobrazení neřeší (seznam je jen jedna úroveň).
        if recursive || paths.contains(where: { $0 == basePath || ($0 as NSString).deletingLastPathComponent == basePath }) {
            handler()
        }
    }

    func stop() {
        guard let s = stream else { return }
        FSEventStreamStop(s)
        FSEventStreamInvalidate(s)
        FSEventStreamRelease(s)
        stream = nil
    }

    deinit { stop() }
}
