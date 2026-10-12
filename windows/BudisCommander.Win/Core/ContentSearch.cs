using System.Text;
using BudisCommander.Remote;
using SharpCompress.Archives;

namespace BudisCommander.Core;

/// <summary>Hledání uvnitř archivů: podle názvu položky a (volitelně) podle textu v obsahu.</summary>
public static class ArchiveSearch
{
    public const long MaxEntryBytes = 5 * 1024 * 1024;
    public const string Separator = " ▸ ";

    /// <summary>Vrací cesty položek uvnitř archivu, které odpovídají masce a případně obsahují text.</summary>
    public static List<string> Search(string archivePath, string mask, string text, int limit, CancellationToken ct)
    {
        var found = new List<string>();
        try
        {
            using var archive = ArchiveFactory.Open(archivePath);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory || entry.Key == null) continue;
                var key = entry.Key.Replace('\\', '/');
                var name = key[(key.LastIndexOf('/') + 1)..];
                if (!Formatting.MatchesMask(name, mask)) continue;
                if (text.Length > 0 && !EntryContains(entry, text)) continue;
                found.Add(key);
                if (found.Count >= limit) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* poškozený nebo zašifrovaný archiv se přeskočí */ }
        return found;
    }

    private static bool EntryContains(IArchiveEntry entry, string text)
    {
        if (entry.Size <= 0 || entry.Size > MaxEntryBytes) return false;
        try
        {
            using var s = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var data = ms.ToArray();
            return !LocalFs.LooksBinary(data) && Encoding.UTF8.GetString(data).Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>Hledání na serveru nebo v cloudu: podle názvu a volitelně podle textu (čte se jen začátek souborů).</summary>
public static class RemoteSearch
{
    public const int MaxFoldersListed = 3000, MaxResults = 5000, MaxFilesRead = 300, MaxArchivesDownloaded = 15;
    public const long MaxArchiveBytes = 50L * 1024 * 1024;
    public const int HeadBytes = 1024 * 1024;

    public sealed record Progress(int Found, int Folders, int FilesRead, bool ReadLimitReached, int Archives = 0);

    public static async Task<List<string>> SearchAsync(RemoteSession conn, string root, string mask, string text, bool hidden,
                                                       Action<IReadOnlyList<string>, Progress>? report, CancellationToken ct, bool inArchives = false)
    {
        var queue = new Queue<string>(); queue.Enqueue(root);
        var found = new List<string>();
        int listed = 0, read = 0, archives = 0; var limit = false;
        while (queue.Count > 0 && listed < MaxFoldersListed && found.Count < MaxResults)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue(); listed++;
            List<FileEntry> items;
            try { items = await conn.ListAsync(dir, ct); } catch (OperationCanceledException) { throw; } catch { continue; }
            foreach (var i in items)
            {
                if (!hidden && i.Name.StartsWith('.')) continue;
                if (i.IsDirectory) { queue.Enqueue(i.FullPath); if (text.Length == 0 && Formatting.MatchesMask(i.Name, mask)) found.Add(i.FullPath); continue; }
                if (inArchives && ArchiveService.IsArchive(i.Name) && i.Size <= MaxArchiveBytes && archives < MaxArchivesDownloaded)
                {
                    archives++;
                    found.AddRange(await SearchRemoteArchiveAsync(conn, i, mask, text, ct));
                }
                if (!Formatting.MatchesMask(i.Name, mask)) continue;
                if (text.Length == 0) { found.Add(i.FullPath); continue; }
                if (i.Size > 50L * 1024 * 1024) continue;
                if (read >= MaxFilesRead) { limit = true; continue; }
                read++;
                try
                {
                    var data = await conn.ReadHeadAsync(i.FullPath, HeadBytes, ct);
                    if (!LocalFs.LooksBinary(data) && Encoding.UTF8.GetString(data).Contains(text, StringComparison.OrdinalIgnoreCase)) found.Add(i.FullPath);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            report?.Invoke(found, new Progress(found.Count, listed, read, limit, archives));
        }
        report?.Invoke(found, new Progress(found.Count, listed, read, limit, archives));
        return found;
    }

    /// <summary>Stáhne archiv ze serveru do dočasné složky a hledá v něm; výsledky mají tvar „cesta na serveru ▸ položka“.</summary>
    private static async Task<List<string>> SearchRemoteArchiveAsync(RemoteSession conn, FileEntry archive, string mask, string text, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "budis-rsearch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, archive.Name);
            await conn.DownloadAsync(archive.FullPath, false, archive.Size, local, null, ct);
            return await Task.Run(() => ArchiveSearch.Search(local, mask, text, 200, ct).Select(e => archive.FullPath + ArchiveSearch.Separator + e).ToList(), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new List<string>(); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
