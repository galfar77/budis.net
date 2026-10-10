using BudisCommander.Core;
using Xunit;

namespace BudisCommander.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData("foto.JPG", "*.jpg", true)]
    [InlineData("foto.png", "*.jpg", false)]
    [InlineData("abc", "a?c", true)]
    [InlineData("abbc", "a?c", false)]
    [InlineData("report.docx", "*.txt;*.docx", true)]
    [InlineData("x", "", true)]
    public void Mask(string name, string mask, bool expected) => Assert.Equal(expected, Formatting.MatchesMask(name, mask));

    [Fact]
    public void FoldRemovesDiacritics() => Assert.Equal("cesky text", Formatting.Fold("Český Text"));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    public void SizeSmall(long b, string expected) => Assert.Equal(expected, Formatting.Size(b).Replace('\u00a0', ' '));

    [Fact]
    public void SizeLarge() => Assert.Contains("MB", Formatting.Size(5 * 1024 * 1024));
}

public class BatchRenameTests
{
    [Fact]
    public void MaskWithCounter()
    {
        var o = new RenameOptions { Mask = "foto-[C].[E]", Start = 1, Digits = 3 };
        var r = BatchRename.NewNames(new[] { ("a.jpg", false), ("b.jpg", false) }, o);
        Assert.Equal(new[] { "foto-001.jpg", "foto-002.jpg" }, r);
    }

    [Fact]
    public void SearchReplaceAndCase()
    {
        var o = new RenameOptions { Search = "Old", Replace = "New", CaseMode = 1 };
        Assert.Equal("new.txt", BatchRename.NewNames(new[] { ("Old.TXT", false) }, o)[0]);
    }

    [Fact]
    public void RegexWithGroups()
    {
        var o = new RenameOptions { Regex = true, Search = @"(\d+)-(\w+)", Replace = "$2_$1" };
        Assert.Equal("abc_12.txt", BatchRename.NewNames(new[] { ("12-abc.txt", false) }, o)[0]);
    }

    [Fact]
    public void DirectoryKeepsNameWithDot()
    {
        var o = new RenameOptions { Mask = "[N].[E]" };
        Assert.Equal("v1.2", BatchRename.NewNames(new[] { ("v1.2", true) }, o)[0]);
    }
}

public class DiffTests
{
    [Fact]
    public void FindsChanges()
    {
        using var t = new TempDir();
        var a = t.File("a.txt", "1\n2\n3\n4\n");
        var b = t.File("b.txt", "1\n2x\n3\n4\n5\n");
        var r = Assert.IsType<DiffResult.Rows>(DiffEngine.Compute(a, b));
        Assert.Equal(3, r.Changes);   // 2 smazáno, 2x přidáno, 5 přidáno
        Assert.Contains(r.Lines, l => l.Kind == DiffKind.Added && l.Right == "5");
    }

    [Fact]
    public void IdenticalFiles()
    {
        using var t = new TempDir();
        var a = t.File("a.txt", "hello\nworld"); var b = t.File("b.txt", "hello\nworld");
        Assert.Equal(0, Assert.IsType<DiffResult.Rows>(DiffEngine.Compute(a, b)).Changes);
    }

    [Fact]
    public void BinaryFilesCompareWhole()
    {
        using var t = new TempDir();
        var a = t.Combine("a.bin"); var b = t.Combine("b.bin");
        File.WriteAllBytes(a, new byte[] { 0, 1, 2 }); File.WriteAllBytes(b, new byte[] { 0, 1, 3 });
        Assert.False(Assert.IsType<DiffResult.Binary>(DiffEngine.Compute(a, b)).Identical);
    }

    [Fact]
    public void MyersEmptyAndTotallyDifferent()
    {
        Assert.Empty(DiffEngine.Myers(new List<string>(), new List<string>())!);
        var ops = DiffEngine.Myers(new List<string> { "a", "b" }, new List<string> { "c" })!;
        Assert.Equal(2, ops.Count(o => o == -1)); Assert.Single(ops, o => o == 1);
    }
}

public class FsTests
{
    [Fact]
    public void SplitAndCombineRoundTrip()
    {
        using var t = new TempDir();
        var f = t.Combine("data.bin");
        var data = new byte[250_000]; new Random(1).NextBytes(data);
        File.WriteAllBytes(f, data);
        int parts = LocalFs.Split(f, 100_000, t.Path, null, CancellationToken.None);
        Assert.Equal(3, parts);
        var joined = t.Combine("joined.bin");
        LocalFs.Combine(t.Combine("data.bin.001"), joined, null, CancellationToken.None);
        Assert.True(LocalFs.SameContent(f, joined));
    }

    [Fact]
    public void SplitExactMultipleHasNoEmptyPart()
    {
        using var t = new TempDir();
        var f = t.Combine("x.bin"); File.WriteAllBytes(f, new byte[200]);
        Assert.Equal(2, LocalFs.Split(f, 100, t.Path, null, CancellationToken.None));
        Assert.False(File.Exists(t.Combine("x.bin.003")));
    }

    [Fact]
    public void HashesAreCorrect()
    {
        using var t = new TempDir();
        var h = LocalFs.ComputeHashes(t.File("a.txt", "abc"));
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", h.Md5);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", h.Sha1);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h.Sha256);
    }

    [Fact]
    public void HexDumpFormat()
    {
        var d = LocalFs.HexDump(System.Text.Encoding.ASCII.GetBytes("Hello"));
        Assert.StartsWith("00000000  48 65 6c 6c 6f", d);
        Assert.Contains("|Hello|", d);
    }

    [Fact]
    public void TotalSizeIsRecursive()
    {
        using var t = new TempDir();
        t.File("a/1.txt", "12345"); t.File("a/b/2.txt", "123");
        Assert.Equal(8, LocalFs.TotalSize(t.Combine("a")));
    }

    [Fact]
    public async Task CopyAndMoveDirectory()
    {
        using var t = new TempDir();
        t.File("src/a.txt", "A"); t.File("src/sub/b.txt", "BB");
        await FileOps.CopyAsync(t.Combine("src"), t.Combine("copy"), false, null, default);
        Assert.Equal("BB", File.ReadAllText(t.Combine("copy", "sub", "b.txt")));
        await FileOps.MoveAsync(t.Combine("copy"), t.Combine("moved"), false, null, default);
        Assert.False(Directory.Exists(t.Combine("copy")));
        Assert.True(File.Exists(t.Combine("moved", "a.txt")));
    }

    [Fact]
    public async Task CopyOverwriteFlag()
    {
        using var t = new TempDir();
        var s = t.File("s.txt", "new"); var d = t.File("d.txt", "old");
        await FileOps.CopyAsync(s, d, false, null, default);
        Assert.Equal("old", File.ReadAllText(d));
        await FileOps.CopyAsync(s, d, true, null, default);
        Assert.Equal("new", File.ReadAllText(d));
    }

    [Fact]
    public void AttributesApply()
    {
        using var t = new TempDir();
        var f = t.File("a.txt");
        var when = new DateTime(2020, 1, 2, 3, 4, 5);
        var errors = LocalFs.ApplyAttributes(new LocalFs.AttributeChange { Modified = when }, new[] { f });
        Assert.Empty(errors);
        Assert.Equal(when, File.GetLastWriteTime(f));
    }
}

public class SyncAndDuplicateTests
{
    [Fact]
    public void SyncPlanCopiesNewAndDeletesExtras()
    {
        using var t = new TempDir();
        t.File("src/new.txt", "n"); t.File("src/same.txt", "s"); t.File("src/sub/x.txt", "x");
        t.File("dst/same.txt", "s"); t.File("dst/extra.txt", "e");
        File.SetLastWriteTime(t.Combine("dst", "same.txt"), File.GetLastWriteTime(t.Combine("src", "same.txt")));
        var plan = SyncPlan.Make(t.Combine("src"), t.Combine("dst"));
        Assert.Contains(plan.Copies, c => c.To.EndsWith("new.txt"));
        Assert.DoesNotContain(plan.Copies, c => c.To.EndsWith("same.txt"));
        Assert.Contains(plan.Deletes, d => d.EndsWith("extra.txt"));
        Assert.Contains(plan.Mkdirs, d => d.EndsWith("sub"));
    }

    [Fact]
    public void FindsDuplicates()
    {
        using var t = new TempDir();
        t.File("a/one.txt", "same content here"); t.File("b/two.txt", "same content here");
        t.File("c/three.txt", "different content!!");
        var groups = DuplicateFinder.Scan(new[] { t.Path }, 1, true, null, default);
        var g = Assert.Single(groups);
        Assert.Equal(2, g.Files.Count);
    }

    [Fact]
    public void SameSizeDifferentContentIsNotDuplicate()
    {
        using var t = new TempDir();
        t.File("a.txt", "aaaa"); t.File("b.txt", "bbbb");
        Assert.Empty(DuplicateFinder.Scan(new[] { t.Path }, 1, true, null, default));
    }
}

public class ArchiveTests
{
    [Fact]
    public void ZipRoundTrip()
    {
        using var t = new TempDir();
        t.File("src/a.txt", "AAA"); t.File("src/sub/b.txt", "BBB");
        var zip = t.Combine("out.zip");
        ArchiveService.Create(zip, t.Combine("src"), new[] { t.Combine("src", "a.txt"), t.Combine("src", "sub") }, null, default);
        ArchiveService.Extract(zip, t.Combine("x"), null, default);
        Assert.Equal("AAA", File.ReadAllText(t.Combine("x", "a.txt")));
        Assert.Equal("BBB", File.ReadAllText(t.Combine("x", "sub", "b.txt")));
    }

    [Fact]
    public void TarGzRoundTrip()
    {
        using var t = new TempDir();
        t.File("src/a.txt", "AAA");
        var tgz = t.Combine("out.tar.gz");
        ArchiveService.Create(tgz, t.Combine("src"), new[] { t.Combine("src", "a.txt") }, null, default);
        ArchiveService.Extract(tgz, t.Combine("x"), null, default);
        Assert.Equal("AAA", File.ReadAllText(t.Combine("x", "a.txt")));
    }

    [Fact]
    public void AddToZipReplacesAndAdds()
    {
        using var t = new TempDir();
        t.File("src/a.txt", "1");
        var zip = t.Combine("z.zip");
        ArchiveService.Create(zip, t.Combine("src"), new[] { t.Combine("src", "a.txt") }, null, default);
        File.WriteAllText(t.Combine("src", "a.txt"), "2"); t.File("src/b.txt", "3");
        ArchiveService.AddToZip(zip, t.Combine("src"), new[] { t.Combine("src", "a.txt"), t.Combine("src", "b.txt") }, default);
        ArchiveService.Extract(zip, t.Combine("x"), null, default);
        Assert.Equal("2", File.ReadAllText(t.Combine("x", "a.txt")));
        Assert.Equal("3", File.ReadAllText(t.Combine("x", "b.txt")));
    }

    [Fact]
    public void ExtractRefusesPathTraversal()
    {
        using var t = new TempDir();
        var zip = t.Combine("evil.zip");
        using (var fs = File.Create(zip))
        using (var za = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = za.CreateEntry("../escaped.txt");
            using var w = new StreamWriter(e.Open()); w.Write("bad");
        }
        ArchiveService.Extract(zip, t.Combine("x"), null, default);
        Assert.False(File.Exists(t.Combine("escaped.txt")));
    }

    [Theory]
    [InlineData("a.zip", true)] [InlineData("a.TAR.GZ", true)] [InlineData("a.7z", true)] [InlineData("a.txt", false)]
    public void IsArchive(string name, bool expected) => Assert.Equal(expected, ArchiveService.IsArchive(name));
}

public class SettingsTests
{
    [Fact]
    public void RoundTrip()
    {
        using var t = new TempDir();
        var path = t.Combine("s.json");
        var s = new AppSettings { FontSize = 15, Theme = "dark" };
        s.Favorites.Add(@"C:\Work");
        s.Servers.Add(new SavedServer { Host = "h", User = "u", Protocol = RemoteProtocol.Sftp });
        s.Save(path);
        var l = AppSettings.Load(path);
        Assert.Equal(15, l.FontSize); Assert.Equal("dark", l.Theme);
        Assert.Equal(@"C:\Work", l.Favorites[0]);
        Assert.Equal(RemoteProtocol.Sftp, l.Servers[0].Protocol);
        Assert.Equal(22, l.Servers[0].EffectivePort);
    }

    [Fact]
    public void BrokenFileFallsBackToDefaults()
    {
        using var t = new TempDir();
        var path = t.File("s.json", "{ not json");
        Assert.Equal(13, AppSettings.Load(path).FontSize);
    }

    [Fact]
    public void RecentsAreDeduplicatedAndCapped()
    {
        var s = new AppSettings();
        for (int i = 0; i < 40; i++) s.AddRecent("p" + i);
        s.AddRecent("P5");
        Assert.Equal(25, s.Recents.Count);
        Assert.Equal("P5", s.Recents[0]);
        Assert.Single(s.Recents, r => r.Equals("p5", StringComparison.OrdinalIgnoreCase));
    }
}
