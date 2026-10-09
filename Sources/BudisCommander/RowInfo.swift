import SwiftUI
import AVFoundation
import ImageIO

/// Doplňkové informace o položce: práva, vlastník a štítky Finderu.
final class RowInfo: NSObject {
    let perms: String
    let owner: String
    let tags: [String]

    init(perms: String, owner: String, tags: [String]) {
        self.perms = perms
        self.owner = owner
        self.tags = tags
    }
}

enum RowInfoLoader {
    private static let infoCache = NSCache<NSURL, RowInfo>()
    private static let mediaCache = NSCache<NSURL, NSString>()
    private static let avExtensions: Set<String> = ["mp3", "m4a", "aac", "wav", "aiff", "flac", "mp4", "mov", "m4v", "avi", "mkv"]

    static func invalidate() {
        infoCache.removeAllObjects()
        mediaCache.removeAllObjects()
    }

    static func permString(_ mode: Int) -> String {
        let letters = Array("rwxrwxrwx")
        var out = ""
        for i in 0..<9 { out.append(mode & (1 << (8 - i)) != 0 ? letters[i] : "-") }
        return out
    }

    static func info(_ url: URL) async -> RowInfo {
        if let cached = infoCache.object(forKey: url as NSURL) { return cached }
        let result = await Task.detached { () -> RowInfo in
            let attrs = (try? FileManager.default.attributesOfItem(atPath: url.path)) ?? [:]
            let mode = (attrs[.posixPermissions] as? NSNumber)?.intValue ?? 0
            let owner = attrs[.ownerAccountName] as? String ?? ""
            let values = try? (url as NSURL).resourceValues(forKeys: [.tagNamesKey])
            let tags = (values?[.tagNamesKey] as? [String]) ?? []
            return RowInfo(perms: permString(mode), owner: owner, tags: tags)
        }.value
        infoCache.setObject(result, forKey: url as NSURL)
        return result
    }

    /// Rozměry obrázku nebo délka zvuku a videa; jinak prázdný text.
    static func media(_ url: URL) async -> String {
        if let cached = mediaCache.object(forKey: url as NSURL) { return cached as String }
        let ext = url.pathExtension.lowercased()
        var text = ""
        if PreviewLoader.imageExtensions.contains(ext) {
            text = await Task.detached { () -> String in
                guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
                      let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any],
                      let w = props[kCGImagePropertyPixelWidth] as? Int,
                      let h = props[kCGImagePropertyPixelHeight] as? Int else { return "" }
                return "\(w)×\(h)"
            }.value
        } else if avExtensions.contains(ext) {
            let asset = AVURLAsset(url: url)
            if let d = try? await asset.load(.duration), d.isNumeric {
                let s = Int(CMTimeGetSeconds(d))
                text = s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, (s % 3600) / 60, s % 60)
                                 : String(format: "%d:%02d", s / 60, s % 60)
            }
        }
        mediaCache.setObject(text as NSString, forKey: url as NSURL)
        return text
    }
}

enum TagPalette {
    static var names: [String] { NSWorkspace.shared.fileLabels }
    static var colors: [NSColor] { NSWorkspace.shared.fileLabelColors }

    /// Indexy 1…7 jsou barevné štítky Finderu (0 = žádný).
    static func index(of tag: String) -> Int? {
        names.firstIndex { $0.caseInsensitiveCompare(tag) == .orderedSame }.flatMap { $0 > 0 ? $0 : nil }
    }

    static func color(for tag: String) -> Color {
        if let i = index(of: tag), colors.indices.contains(i) { return Color(nsColor: colors[i]) }
        return Color.secondary
    }
}

/// Barevné tečky štítků Finderu u názvu souboru.
struct TagDots: View {
    let item: FileItem
    let generation: Int
    @State private var tags: [String] = []

    var body: some View {
        HStack(spacing: 2) {
            ForEach(tags, id: \.self) { tag in
                Circle().fill(TagPalette.color(for: tag)).frame(width: 9, height: 9).help(tag)
            }
        }
        .task(id: "\(item.id)#\(generation)") {
            guard !item.isParent, item.remotePath == nil else { tags = []; return }
            tags = await RowInfoLoader.info(item.url).tags
        }
    }
}

/// Volitelné sloupce: práva, vlastník, rozměry nebo délka.
struct ExtraCells: View {
    let item: FileItem
    let generation: Int
    @ObservedObject var settings: Settings
    @State private var info: RowInfo?
    @State private var media = ""

    var body: some View {
        HStack(spacing: 6) {
            if settings.showPerms {
                Text(info?.perms ?? "").frame(width: 78, alignment: .leading).foregroundStyle(.secondary)
            }
            if settings.showOwner {
                Text(info?.owner ?? "").lineLimit(1).frame(width: 70, alignment: .leading).foregroundStyle(.secondary)
            }
            if settings.showMedia {
                Text(media).frame(width: 84, alignment: .trailing).foregroundStyle(.secondary)
            }
        }
        .task(id: "\(item.id)#\(generation)#\(settings.showPerms)\(settings.showOwner)\(settings.showMedia)") {
            guard !item.isParent, item.remotePath == nil else { info = nil; media = ""; return }
            if settings.showPerms || settings.showOwner { info = await RowInfoLoader.info(item.url) }
            if settings.showMedia && !item.isDirectory { media = await RowInfoLoader.media(item.url) }
        }
    }
}

struct TagsSheet: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var picked: Set<Int> = []
    @State private var custom = ""
    @State private var loaded = false
    @State private var common: Set<String> = []

    private var items: [FileItem] { model.tagItems }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Štítky Finderu (\(items.count) položek)").font(.headline)
            HStack(spacing: 10) {
                ForEach(1..<max(1, min(8, TagPalette.names.count)), id: \.self) { i in
                    Button {
                        if picked.contains(i) { picked.remove(i) } else { picked.insert(i) }
                    } label: {
                        VStack(spacing: 3) {
                            Circle().fill(Color(nsColor: TagPalette.colors[i])).frame(width: 22, height: 22)
                                .overlay(Circle().stroke(Color.primary, lineWidth: picked.contains(i) ? 2 : 0))
                            Text(TagPalette.names[i]).font(.system(size: 10))
                        }
                    }
                    .buttonStyle(.plain)
                }
            }
            TextField("Další štítky (oddělené čárkou)", text: $custom).textFieldStyle(.roundedBorder)
            Text("Změní se jen štítky, které mají všechny vybrané položky společné; ostatní zůstanou.")
                .font(.system(size: 11)).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Zrušit") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Použít") {
                    let colorNames = picked.map { TagPalette.names[$0] }
                    let extra = custom.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
                    dismiss()
                    Task { await model.applyTags(items.map(\.url), common: common, add: colorNames + extra) }
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 470)
        .task {
            guard !loaded else { return }
            loaded = true
            var sets: [Set<String>] = []
            for item in items { sets.append(Set(await RowInfoLoader.info(item.url).tags)) }
            common = sets.dropFirst().reduce(sets.first ?? []) { $0.intersection($1) }
            picked = Set(common.compactMap { TagPalette.index(of: $0) })
            custom = common.filter { TagPalette.index(of: $0) == nil }.sorted().joined(separator: ", ")
        }
    }
}
