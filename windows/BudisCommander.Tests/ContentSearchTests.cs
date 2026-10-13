using BudisCommander.Core;
using BudisCommander.Remote;
using Xunit;

namespace BudisCommander.Tests;

public class ContentSearchTests
{
    [Fact]
    public void FindsEntriesInsideZipByNameAndContent()
    {
        using var t = new TempDir();
        t.File("src/readme.txt", "Ahoj světe, tajné slovo je pomeranč");
        t.File("src/data.bin", "x"); File.WriteAllBytes(t.Combine("src/blob.dat"), new byte[] { 0, 1, 2, 3, 0, 5 });
        t.File("src/sub/note.txt", "nic zajímavého");
        var zip = t.Combine("balik.zip");
        ArchiveService.Create(zip, t.Combine("src"), new[] { t.Combine("src/readme.txt"), t.Combine("src/data.bin"), t.Combine("src/blob.dat"), t.Combine("src/sub") }, null, CancellationToken.None);

        var byName = ArchiveSearch.Search(zip, "*.txt", "", 100, CancellationToken.None);
        Assert.Contains("readme.txt", byName); Assert.Contains("sub/note.txt", byName); Assert.DoesNotContain("data.bin", byName);

        var byText = ArchiveSearch.Search(zip, "*", "POMERANČ", 100, CancellationToken.None);
        Assert.Equal(new[] { "readme.txt" }, byText);
        Assert.Empty(ArchiveSearch.Search(zip, "*", "banán", 100, CancellationToken.None));
    }

    [Fact]
    public void BrokenOrMissingArchiveGivesNoResults()
    {
        using var t = new TempDir();
        var bad = t.File("rozbity.zip", "tohle není zip");
        Assert.Empty(ArchiveSearch.Search(bad, "*", "", 10, CancellationToken.None));
        Assert.Empty(ArchiveSearch.Search(t.Combine("neexistuje.zip"), "*", "", 10, CancellationToken.None));
    }

    private sealed class LocalBackedRemote : RemoteSession
    {
        private readonly string _root; public int Reads;
        public LocalBackedRemote(string root) { _root = root; }
        private string L(string p) => Path.Combine(_root, p.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        public override string DisplayName => "test";
        public override string StartPath => "/";
        public override Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task<List<FileEntry>> ListAsync(string path, CancellationToken ct)
        {
            var d = new DirectoryInfo(L(path));
            var list = d.EnumerateFileSystemInfos().Select(i => new FileEntry
            {
                Name = i.Name, FullPath = RemotePath.Child(path, i.Name), IsDirectory = i is DirectoryInfo,
                Size = i is FileInfo f ? f.Length : 0, Modified = i.LastWriteTime,
            }).ToList();
            return Task.FromResult(list);
        }
        public override Task<byte[]> ReadHeadAsync(string path, int maxBytes, CancellationToken ct)
        {
            Reads++;
            var data = File.ReadAllBytes(L(path));
            return Task.FromResult(data.Length > maxBytes ? data[..maxBytes] : data);
        }
        public int Downloads;
        protected override Task DownloadFileAsync(string remote, string local, long size, Action<double>? progress, CancellationToken ct)
        {
            Downloads++;
            File.Copy(L(remote), local, true);
            return Task.CompletedTask;
        }
        protected override Task UploadFileAsync(string local, string remote, Action<double>? progress, CancellationToken ct) => Task.CompletedTask;
        protected override Task DeleteFileAsync(string path, CancellationToken ct) => Task.CompletedTask;
        protected override Task DeleteEmptyDirectoryAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public override Task MkdirAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public override Task RenameAsync(string from, string to, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
    }

    [Fact]
    public async Task RemoteSearchByNameAndByContent()
    {
        using var t = new TempDir();
        t.File("a.txt", "klíčové slovo tady"); t.File("b.txt", "nic"); t.File("sub/c.txt", "jiné KLÍČOVÉ slovo"); t.File("sub/.skryty.txt", "klíčové");
        File.WriteAllBytes(t.Combine("bin.dat"), new byte[] { 0, 0, 1 });
        var remote = new LocalBackedRemote(t.Path);

        var names = await RemoteSearch.SearchAsync(remote, "/", "*.txt", "", false, null, CancellationToken.None);
        Assert.Equal(new[] { "/a.txt", "/b.txt", "/sub/c.txt" }, names.OrderBy(x => x));
        Assert.Equal(0, remote.Reads);

        var hits = await RemoteSearch.SearchAsync(remote, "/", "*", "klíčové", false, null, CancellationToken.None);
        Assert.Equal(new[] { "/a.txt", "/sub/c.txt" }, hits.OrderBy(x => x));
        Assert.True(remote.Reads >= 3);

        var withHidden = await RemoteSearch.SearchAsync(remote, "/", "*.txt", "klíčové", true, null, CancellationToken.None);
        Assert.Contains("/sub/.skryty.txt", withHidden);
    }

    [Fact]
    public async Task RemoteSearchLooksInsideArchivesWhenAsked()
    {
        using var t = new TempDir();
        t.File("src/readme.txt", "tajné slovo pomeranč"); t.File("src/other.txt", "nic");
        t.Dir("srv");
        ArchiveService.Create(t.Combine("srv/balik.zip"), t.Combine("src"), new[] { t.Combine("src/readme.txt"), t.Combine("src/other.txt") }, null, CancellationToken.None);
        var remote = new LocalBackedRemote(t.Combine("srv"));

        var without = await RemoteSearch.SearchAsync(remote, "/", "*.txt", "", false, null, CancellationToken.None);
        Assert.Empty(without); Assert.Equal(0, remote.Downloads);

        var byName = await RemoteSearch.SearchAsync(remote, "/", "*.txt", "", false, null, CancellationToken.None, inArchives: true);
        Assert.Equal(new[] { "/balik.zip" + ArchiveSearch.Separator + "other.txt", "/balik.zip" + ArchiveSearch.Separator + "readme.txt" }, byName.OrderBy(x => x));
        Assert.Equal(1, remote.Downloads);

        var byText = await RemoteSearch.SearchAsync(remote, "/", "*", "POMERANČ", false, null, CancellationToken.None, inArchives: true);
        Assert.Equal(new[] { "/balik.zip" + ArchiveSearch.Separator + "readme.txt" }, byText);
    }

    [Fact]
    public async Task RemoteContentSearchStopsReadingAfterLimit()
    {
        using var t = new TempDir();
        for (var i = 0; i < RemoteSearch.MaxFilesRead + 20; i++) t.File($"f{i:D4}.txt", "nic");
        var remote = new LocalBackedRemote(t.Path);
        RemoteSearch.Progress? last = null;
        await RemoteSearch.SearchAsync(remote, "/", "*", "slovo", false, (_, p) => last = p, CancellationToken.None);
        Assert.Equal(RemoteSearch.MaxFilesRead, remote.Reads);
        Assert.True(last!.ReadLimitReached);
    }
}

public class ArchiveEditTests
{
    [Fact]
    public void ReplacesAndAddsEntriesInZipKeepingOthers()
    {
        using var t = new TempDir();
        t.File("src/a.txt", "STARE"); t.File("src/sub/b.txt", "B");
        var zip = t.Combine("balik.zip");
        ArchiveService.Create(zip, t.Combine("src"), new[] { t.Combine("src/a.txt"), t.Combine("src/sub") }, null, CancellationToken.None);
        Assert.True(ArchiveService.CanUpdate(zip));
        Assert.False(ArchiveService.CanUpdate(t.Combine("x.7z")));

        var edited = t.File("edit/a.txt", "NOVE ěščř");
        ArchiveService.ReplaceInZip(zip, "a.txt", edited);
        var fresh = t.File("edit/c.txt", "NOVY SOUBOR");
        ArchiveService.ReplaceInZip(zip, "sub/c.txt", fresh);

        ArchiveService.Extract(zip, t.Combine("out"), null, CancellationToken.None);
        Assert.Equal("NOVE ěščř", File.ReadAllText(t.Combine("out/a.txt")));
        Assert.Equal("B", File.ReadAllText(t.Combine("out/sub/b.txt")));
        Assert.Equal("NOVY SOUBOR", File.ReadAllText(t.Combine("out/sub/c.txt")));
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Equal(1, z.Entries.Count(e => e.FullName == "a.txt"));              // nevznikl duplikát
    }

    [Fact]
    public void EntryNameUsesForwardSlashesRelativeToExtractRoot()
    {
        using var t = new TempDir();
        Assert.Equal("sub/dir/f.txt", ArchiveService.EntryNameFor(t.Combine("root"), Path.Combine(t.Combine("root"), "sub", "dir", "f.txt")));
    }
}
