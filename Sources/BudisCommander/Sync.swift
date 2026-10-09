import Foundation

/// Plán zrcadlení: co zkopírovat, jaké složky vytvořit a co v cíli smazat.
struct SyncPlan {
    var mkdirs: [URL] = []
    var copies: [(from: URL, to: URL)] = []
    var deletes: [URL] = []
    var bytes: Int64 = 0

    var isEmpty: Bool { mkdirs.isEmpty && copies.isEmpty && deletes.isEmpty }

    static func make(src: URL, dst: URL) -> SyncPlan {
        var plan = SyncPlan()
        walk(src, dst, &plan)
        return plan
    }

    private struct Entry {
        let url: URL
        let isDir: Bool
        let size: Int64
        let date: Date?
    }

    private static func entries(_ dir: URL) -> [String: Entry] {
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .fileSizeKey, .contentModificationDateKey]
        guard let urls = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: Array(keys),
                                                                      options: []) else { return [:] }
        var out: [String: Entry] = [:]
        for u in urls {
            let v = try? u.resourceValues(forKeys: keys)
            out[u.lastPathComponent] = Entry(url: u, isDir: v?.isDirectory ?? false,
                                            size: Int64(v?.fileSize ?? 0), date: v?.contentModificationDate)
        }
        return out
    }

    private static func walk(_ src: URL, _ dst: URL, _ plan: inout SyncPlan) {
        let s = entries(src), d = entries(dst)
        for (name, a) in s where name != ".DS_Store" {
            let target = dst.appendingPathComponent(name)
            if a.isDir {
                if let b = d[name], b.isDir {
                    walk(a.url, target, &plan)
                } else {
                    if d[name] != nil { plan.deletes.append(target) }
                    plan.mkdirs.append(target)
                    walk(a.url, target, &plan)
                }
            } else {
                if let b = d[name], !b.isDir {
                    let sameTime = abs((a.date ?? .distantPast).timeIntervalSince(b.date ?? .distantPast)) <= 2
                    if a.size == b.size && sameTime { continue }
                } else if d[name] != nil {
                    plan.deletes.append(target)
                }
                plan.copies.append((a.url, target))
                plan.bytes += a.size
            }
        }
        for (name, b) in d where s[name] == nil && name != ".DS_Store" {
            plan.deletes.append(b.url)
        }
    }
}
