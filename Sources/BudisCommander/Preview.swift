import SwiftUI
import PDFKit
import QuickLookThumbnailing

enum PreviewContent {
    case message(String)
    case text(String)
    case hex(String)
    case image(NSImage)
    case pdf(URL)
}

enum PreviewLoader {
    static let imageExtensions: Set<String> = ["png", "jpg", "jpeg", "gif", "heic", "tif", "tiff", "bmp", "webp"]

    static func load(_ item: FileItem?, remote: Bool) async -> PreviewContent {
        guard let item, !item.isParent else { return .message("Nic k zobrazení.") }
        if remote { return .message("Náhled souboru na serveru: stiskněte F3.") }
        let url = item.url
        let isDir = item.isDirectory
        return await Task.detached { () -> PreviewContent in
            if isDir {
                let names = (try? FileManager.default.contentsOfDirectory(atPath: url.path)) ?? []
                let size = ByteCountFormatter.string(fromByteCount: LocalFS.totalSize(url), countStyle: .file)
                return .message("Složka\n\(names.count) položek, \(size)")
            }
            let ext = url.pathExtension.lowercased()
            if ext == "pdf" { return .pdf(url) }
            if imageExtensions.contains(ext), LocalFS.totalSize(url) < 60 * 1024 * 1024, let img = NSImage(contentsOf: url) {
                return .image(img)
            }
            guard let handle = try? FileHandle(forReadingFrom: url) else { return .message("Soubor nelze přečíst.") }
            defer { try? handle.close() }
            let data = (try? handle.read(upToCount: 200 * 1024)) ?? Data()
            if data.isEmpty { return .message("Prázdný soubor.") }
            if data.prefix(4096).contains(0) { return .hex(LocalFS.hexDump(data.prefix(4096))) }
            return .text(String(decoding: data, as: UTF8.self))
        }.value
    }
}

/// Panel rychlého náhledu: místo neaktivního panelu ukazuje soubor pod kurzorem aktivního panelu.
struct QuickViewPanel: View {
    @ObservedObject var group: TabGroup

    var body: some View {
        QuickViewContent(pane: group.current)
            .id(ObjectIdentifier(group.current))
    }
}

struct QuickViewContent: View {
    @ObservedObject var pane: PaneState
    @State private var content: PreviewContent = .message("")

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Image(systemName: "eye")
                Text(pane.current?.name ?? "Rychlý náhled").lineLimit(1).truncationMode(.middle)
                Spacer()
                Text("⌘V zavře").font(.system(size: 10)).foregroundStyle(.secondary)
            }
            .font(.system(size: 12, weight: .medium))
            .padding(.horizontal, 8)
            .frame(height: 24)
            .background(Color.secondary.opacity(0.15))

            switch content {
            case .message(let text):
                Text(text).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            case .text(let text):
                ScrollView {
                    Text(text).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(8)
                }
            case .hex(let text):
                ScrollView([.vertical, .horizontal]) {
                    Text(text).font(.system(size: 11, design: .monospaced)).textSelection(.enabled).padding(8)
                }
            case .image(let image):
                Image(nsImage: image).resizable().scaledToFit().padding(8)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            case .pdf(let url):
                PDFViewer(url: url)
            }
        }
        .task(id: pane.current?.id ?? "") {
            content = await PreviewLoader.load(pane.current, remote: pane.connection != nil)
        }
    }
}

/// Miniatury souborů přes Quick Look s mezipamětí.
final class ThumbnailCache: @unchecked Sendable {
    static let shared = ThumbnailCache()
    private let cache = NSCache<NSURL, NSImage>()

    func thumbnail(for url: URL, side: CGFloat) async -> NSImage? {
        if let cached = cache.object(forKey: url as NSURL) { return cached }
        let request = QLThumbnailGenerator.Request(fileAt: url, size: CGSize(width: side, height: side),
                                                   scale: 2, representationTypes: .thumbnail)
        guard let rep = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request) else { return nil }
        let image = rep.nsImage
        cache.setObject(image, forKey: url as NSURL)
        return image
    }
}

struct ThumbView: View {
    let item: FileItem
    let side: CGFloat
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().scaledToFit()
            } else {
                Image(systemName: item.isParent ? "arrow.turn.left.up" : (item.isDirectory ? "folder.fill" : "doc"))
                    .foregroundStyle(item.isDirectory ? Color.blue : Color.secondary)
            }
        }
        .frame(width: side, height: side)
        .task(id: item.id) {
            guard !item.isParent, item.remotePath == nil else { return }
            image = await ThumbnailCache.shared.thumbnail(for: item.url, side: side)
        }
    }
}
