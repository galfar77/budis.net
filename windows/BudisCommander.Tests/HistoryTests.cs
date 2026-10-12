using System.Text;
using BudisCommander.Core;
using Xunit;

namespace BudisCommander.Tests;

public class OperationLogTests
{
    [Fact]
    public void PersistsAndListsNewestFirst()
    {
        using var t = new TempDir();
        var file = t.Combine("hist/history.json");
        var log = new OperationLog(file);
        log.Add(OpKind.Copy, "první", new[] { new OpItem("/a", "/b") });
        log.Add(OpKind.Delete, "druhé", new[] { new OpItem("/c", "") }, permanent: true);
        Assert.Equal(new[] { "druhé", "první" }, log.Entries.Select(e => e.Summary));
        var again = new OperationLog(file);
        Assert.Equal(2, again.Entries.Count);
        Assert.True(again.Entries[0].Permanent);
        var third = again.Add(OpKind.Move, "třetí", new[] { new OpItem("/x", "/y") });
        Assert.Equal(3, third.Id);                                   // číslování pokračuje
    }

    [Fact]
    public void KeepsOnlyTheLatestEntries()
    {
        var log = new OperationLog();
        for (var i = 0; i < OperationLog.MaxEntries + 20; i++) log.Add(OpKind.Rename, "r" + i, new[] { new OpItem("/a" + i, "/b" + i) });
        Assert.Equal(OperationLog.MaxEntries, log.Entries.Count);
        Assert.Equal("r" + (OperationLog.MaxEntries + 19), log.Entries[0].Summary);
    }

    [Fact]
    public void RevertMoveAndRenameAndNewFolderAndCopy()
    {
        using var t = new TempDir();
        t.File("a/f.txt", "A"); t.Dir("b");
        // přesun a/f.txt -> b/f.txt
        File.Move(t.Combine("a/f.txt"), t.Combine("b/f.txt"));
        var move = new OpEntry { Kind = OpKind.Move, Items = { new OpItem(t.Combine("a/f.txt"), t.Combine("b/f.txt")) } };
        Assert.True(OperationLog.CanRevert(move));
        var (done, errors) = OperationLog.Revert(move);
        Assert.Equal(1, done); Assert.Empty(errors);
        Assert.True(File.Exists(t.Combine("a/f.txt"))); Assert.False(File.Exists(t.Combine("b/f.txt")));

        // původní místo obsazené -> chyba, nic se nepřepíše
        File.WriteAllText(t.Combine("b/f.txt"), "B");
        var (done2, errors2) = OperationLog.Revert(move);
        Assert.Equal(0, done2); Assert.Single(errors2);
        Assert.Equal("A", File.ReadAllText(t.Combine("a/f.txt")));

        // kopie a nová složka jdou pryč
        var copy = new OpEntry { Kind = OpKind.Copy, Items = { new OpItem(t.Combine("a/f.txt"), t.Combine("b/f.txt")) } };
        Assert.Equal(1, OperationLog.Revert(copy).Done);
        Assert.False(File.Exists(t.Combine("b/f.txt")));
        t.Dir("nova");
        var mk = new OpEntry { Kind = OpKind.NewFolder, Items = { new OpItem("", t.Combine("nova")) } };
        Assert.Equal(1, OperationLog.Revert(mk).Done);
        Assert.False(Directory.Exists(t.Combine("nova")));

        // přejmenování
        File.Move(t.Combine("a/f.txt"), t.Combine("a/g.txt"));
        var ren = new OpEntry { Kind = OpKind.Rename, Items = { new OpItem(t.Combine("a/f.txt"), t.Combine("a/g.txt")) } };
        Assert.Equal(1, OperationLog.Revert(ren).Done);
        Assert.True(File.Exists(t.Combine("a/f.txt")));
    }

    [Fact]
    public void DeleteRevertUsesRestoreCallbackAndPermanentIsNotRevertable()
    {
        var e = new OpEntry { Kind = OpKind.Delete, Items = { new OpItem("/x/doc.txt", "") } };
        Assert.True(OperationLog.CanRevert(e));
        var asked = new List<string>();
        Assert.Equal(1, OperationLog.Revert(e, p => { asked.Add(p); return true; }).Done);
        Assert.Equal(new[] { "/x/doc.txt" }, asked);
        Assert.Single(OperationLog.Revert(e, _ => false).Errors);
        Assert.False(OperationLog.CanRevert(new OpEntry { Kind = OpKind.Delete, Permanent = true, Items = { new OpItem("/x", "") } }));
        Assert.False(OperationLog.CanRevert(new OpEntry { Kind = OpKind.Move, Reverted = true, Items = { new OpItem("/a", "/b") } }));
    }

    [Fact]
    public async Task RepeatCopiesAgainAndOverwrites()
    {
        using var t = new TempDir();
        t.File("src/a.txt", "NOVE"); t.File("dst/a.txt", "STARE");
        var e = new OpEntry { Kind = OpKind.Copy, Items = { new OpItem(t.Combine("src/a.txt"), t.Combine("dst/a.txt")) } };
        Assert.True(OperationLog.CanRepeat(e));
        Assert.Equal(1, await OperationLog.RepeatAsync(e));
        Assert.Equal("NOVE", File.ReadAllText(t.Combine("dst/a.txt")));
        Assert.False(OperationLog.CanRepeat(new OpEntry { Kind = OpKind.Move, Items = { new OpItem("/a", "/b") } }));
    }

    [Fact]
    public async Task TransferAndDeleteAreRecordedInHistory()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "A"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        await core.StartTransferAsync(false);
        await core.WaitIdleAsync();
        var copy = Assert.Single(core.History.Entries);
        Assert.Equal(OpKind.Copy, copy.Kind);
        Assert.Equal(t.Combine("r/a.txt"), copy.Items[0].To);

        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        await core.StartDeleteAsync(true);
        await core.WaitIdleAsync();
        Assert.Equal(OpKind.Delete, core.History.Entries[0].Kind);
        Assert.True(core.History.Entries[0].Permanent);
        Assert.False(File.Exists(t.Combine("l/a.txt")));
    }
}

public class RecycleBinTests
{
    private static string MakeInfo(string dir, string id, string ext, string original, long size, bool v2 = true, bool asDir = false)
    {
        var info = Path.Combine(dir, "$I" + id + ext);
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(v2 ? 2L : 1L); w.Write(size); w.Write(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
            if (v2) { w.Write(original.Length + 1); w.Write(Encoding.Unicode.GetBytes(original + "\0")); }
            else { var buf = new byte[520]; Encoding.Unicode.GetBytes(original).CopyTo(buf, 0); w.Write(buf); }
            File.WriteAllBytes(info, ms.ToArray());
        }
        var rec = Path.Combine(dir, "$R" + id + ext);
        if (asDir) { Directory.CreateDirectory(rec); File.WriteAllText(Path.Combine(rec, "in.txt"), "x"); } else File.WriteAllText(rec, "obsah");
        return info;
    }

    [Fact]
    public void ParsesBothFormatVersions()
    {
        using var t = new TempDir();
        t.Dir("bin");
        var v2 = RecycleBin.ParseInfo(MakeInfo(t.Combine("bin"), "AAAAAA", ".txt", @"C:\Users\Já\Dokumenty\zpráva.txt", 5))!;
        Assert.Equal(@"C:\Users\Já\Dokumenty\zpráva.txt", v2.OriginalPath);
        Assert.Equal(5, v2.Size); Assert.False(v2.IsDirectory);
        Assert.Equal(2025, v2.Deleted.Year);
        var v1 = RecycleBin.ParseInfo(MakeInfo(t.Combine("bin"), "BBBBBB", ".doc", @"D:\stará\verze.doc", 7, v2: false))!;
        Assert.Equal(@"D:\stará\verze.doc", v1.OriginalPath);
        var dir = RecycleBin.ParseInfo(MakeInfo(t.Combine("bin"), "CCCCCC", "", @"C:\data\složka", 0, asDir: true))!;
        Assert.True(dir.IsDirectory);
    }

    [Fact]
    public void IgnoresBrokenOrOrphanedInfoFiles()
    {
        using var t = new TempDir();
        t.Dir("bin");
        File.WriteAllBytes(t.Combine("bin/$IBROKEN.txt"), new byte[] { 1, 2, 3 });
        var orphan = MakeInfo(t.Combine("bin"), "ORPHAN", ".txt", @"C:\x.txt", 1);
        File.Delete(t.Combine("bin/$RORPHAN.txt"));
        Assert.Null(RecycleBin.ParseInfo(t.Combine("bin/$IBROKEN.txt")));
        Assert.Null(RecycleBin.ParseInfo(orphan));
        Assert.Empty(RecycleBin.List(new[] { t.Combine("bin") }));
    }

    [Fact]
    public void RestoreMovesBackAndAvoidsOverwrite()
    {
        using var t = new TempDir();
        t.Dir("bin");
        var original = t.Combine("kam/doc.txt");
        MakeInfo(t.Combine("bin"), "DDDDDD", ".txt", original, 5);
        var item = Assert.Single(RecycleBin.List(new[] { t.Combine("bin") }));
        Directory.CreateDirectory(t.Combine("kam"));
        File.WriteAllText(original, "jiný soubor");                          // původní název je obsazený
        var target = RecycleBin.Restore(item);
        Assert.Equal(t.Combine("kam/doc (2).txt"), target);
        Assert.Equal("obsah", File.ReadAllText(target));
        Assert.Equal("jiný soubor", File.ReadAllText(original));
        Assert.Empty(RecycleBin.List(new[] { t.Combine("bin") }));
    }

    [Fact]
    public void RestoreByOriginalPathFindsTheItemAndRecreatesFolders()
    {
        using var t = new TempDir();
        t.Dir("bin");
        var original = t.Combine("neexistujici/slozka/f.txt");
        MakeInfo(t.Combine("bin"), "EEEEEE", ".txt", original, 5);
        Assert.False(RecycleBin.RestoreByOriginalPath(t.Combine("jine.txt"), new[] { t.Combine("bin") }));
        Assert.True(RecycleBin.RestoreByOriginalPath(original, new[] { t.Combine("bin") }));
        Assert.Equal("obsah", File.ReadAllText(original));
    }
}
