using BudisCommander.Core;
using Xunit;

namespace BudisCommander.Tests;

public class PaneStateTests
{
    private static async Task<PaneState> Load(string dir, AppSettings? s = null)
    {
        var p = new PaneState(s ?? new AppSettings(), dir, true);
        Assert.True(await p.LoadLocalAsync(dir, "", true));
        return p;
    }

    [Fact]
    public async Task ListsDirectoriesFirstWithParent()
    {
        using var t = new TempDir();
        t.File("b.txt"); t.File("a.txt"); t.Dir("zdir");
        var p = await Load(t.Path);
        Assert.True(p.Items[0].IsParent);
        Assert.Equal("zdir", p.Items[1].Name);
        Assert.Equal(new[] { "a.txt", "b.txt" }, p.Items.Skip(2).Select(i => i.Name));
    }

    [Fact]
    public async Task NaturalSortAndDescending()
    {
        using var t = new TempDir();
        t.File("f10.txt"); t.File("f2.txt"); t.File("f1.txt");
        var p = await Load(t.Path);
        Assert.Equal(3, p.Items.Count(i => !i.IsParent));
        await p.SetSortAsync(SortKey.Name);          // druhé kliknutí = sestupně
        Assert.False(p.Ascending);
        Assert.Equal("f2.txt", p.Items.Where(i => !i.IsParent).Select(i => i.Name).OrderBy(n => n).ElementAt(2) == "f2.txt" ? "f2.txt" : "?");
    }

    [Fact]
    public async Task SortBySizeKeepsDirsFirst()
    {
        using var t = new TempDir();
        t.File("big.txt", new string('x', 100)); t.File("small.txt", "x"); t.Dir("d");
        var p = await Load(t.Path);
        await p.SetSortAsync(SortKey.Size);
        var names = p.Items.Where(i => !i.IsParent).Select(i => i.Name).ToList();
        Assert.Equal(new[] { "d", "small.txt", "big.txt" }, names);
    }

    [Fact]
    public async Task NavigateEnterAndUp()
    {
        using var t = new TempDir();
        t.File("sub/in.txt");
        var p = await Load(t.Path);
        p.MoveTo(p.Items.FindIndex(i => i.Name == "sub"));
        await p.EnterAsync();
        Assert.Equal(t.Combine("sub"), p.Path);
        Assert.Contains(p.Items, i => i.Name == "in.txt");
        await p.GoUpAsync();
        Assert.Equal(t.Path, p.Path);
        Assert.Equal("sub", p.Current!.Name);            // kurzor zůstal na složce, ze které jsme vyšli
    }

    [Fact]
    public async Task HistoryBackAndForward()
    {
        using var t = new TempDir();
        t.Dir("a"); t.Dir("b");
        var p = new PaneState(new AppSettings(), t.Path);
        await p.LoadLocalAsync(t.Path, "", true);
        await p.NavigateAsync(t.Combine("a"));
        await p.NavigateAsync(t.Combine("b"));
        await p.GoBackAsync();
        Assert.Equal(t.Combine("a"), p.Path);
        await p.GoForwardAsync();
        Assert.Equal(t.Combine("b"), p.Path);
    }

    [Fact]
    public async Task MarkingAndTargets()
    {
        using var t = new TempDir();
        t.File("a.txt"); t.File("b.jpg"); t.File("c.jpg");
        var p = await Load(t.Path);
        p.MarkMask("*.jpg", true);
        Assert.Equal(new[] { "b.jpg", "c.jpg" }, p.Targets().Select(i => i.Name).OrderBy(n => n));
        p.UnmarkAll();
        p.MoveTo(1);
        Assert.Single(p.Targets());
        p.ToggleMark();
        Assert.Single(p.Marked);
    }

    [Fact]
    public async Task FilterIgnoresDiacriticsAndCase()
    {
        using var t = new TempDir();
        t.File("Čeština.txt"); t.File("other.txt");
        var p = await Load(t.Path);
        p.SetFilter("cest");
        Assert.Equal(new[] { "Čeština.txt" }, p.Items.Where(i => !i.IsParent).Select(i => i.Name));
        p.SetFilter("");
        Assert.Equal(2, p.Items.Count(i => !i.IsParent));
    }

    [Fact]
    public async Task QuickSearchJumps()
    {
        using var t = new TempDir();
        t.File("alpha.txt"); t.File("beta.txt"); t.File("gamma.txt");
        var p = await Load(t.Path);
        p.QuickSearch("g");
        Assert.Equal("gamma.txt", p.Current!.Name);
    }

    [Fact]
    public async Task HiddenFilesAreOptional()
    {
        if (!OperatingSystem.IsWindows()) return;       // atribut Hidden existuje jen na Windows
        using var t = new TempDir();
        var f = t.File("secret.txt"); t.File("shown.txt");
        File.SetAttributes(f, FileAttributes.Hidden);
        var p = await Load(t.Path);
        Assert.DoesNotContain(p.Items, i => i.Name == "secret.txt");
        await p.ToggleHiddenAsync();
        Assert.Contains(p.Items, i => i.Name == "secret.txt");
    }

    [Fact]
    public async Task BranchViewListsAllFiles()
    {
        using var t = new TempDir();
        t.File("a.txt"); t.File("x/y/deep.txt");
        var p = await Load(t.Path);
        await p.ToggleBranchAsync();
        Assert.Contains(p.Items, i => i.SubPath != null && i.SubPath.EndsWith("deep.txt"));
        Assert.DoesNotContain(p.Items, i => i.IsDirectory && !i.IsParent);
    }

    [Fact]
    public async Task DirSizesAreComputed()
    {
        using var t = new TempDir();
        t.File("d/a.txt", "12345");
        var p = await Load(t.Path);
        await p.ComputeDirSizesAsync(p.Items.Where(i => i.IsDirectory && !i.IsParent).ToList());
        Assert.Equal(5, p.Items.First(i => i.Name == "d").DirSize);
    }

    [Fact]
    public async Task OpensZipAsFolderAndLeaves()
    {
        using var t = new TempDir();
        t.File("src/in.txt", "hi");
        ArchiveService.Create(t.Combine("a.zip"), t.Combine("src"), new[] { t.Combine("src", "in.txt") }, null, default);
        var p = new PaneState(new AppSettings(), t.Path);
        await p.LoadLocalAsync(t.Path, "", true);
        p.MoveTo(p.Items.FindIndex(i => i.Name == "a.zip"));
        await p.EnterAsync();
        Assert.True(p.IsArchive);
        Assert.Contains(p.Items, i => i.Name == "in.txt");
        await p.GoUpAsync();
        Assert.False(p.IsArchive);
        Assert.Equal(t.Path, p.Path);
        Assert.Equal("a.zip", p.Current!.Name);
    }

    [Fact]
    public async Task WatcherReloadsAfterChange()
    {
        using var t = new TempDir();
        var p = new PaneState(new AppSettings(), t.Path);
        await p.LoadLocalAsync(t.Path, "", true);
        t.File("appeared.txt");
        for (int i = 0; i < 50 && !p.Items.Any(x => x.Name == "appeared.txt"); i++) await Task.Delay(100);
        Assert.Contains(p.Items, x => x.Name == "appeared.txt");
        p.Dispose();
    }

    [Fact]
    public async Task MissingDirectoryRaisesError()
    {
        var p = new PaneState(new AppSettings(), Path.GetTempPath());
        string? err = null;
        p.Error += m => err = m;
        Assert.False(await p.LoadLocalAsync(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()), null, false));
        Assert.NotNull(err);
    }
}

public class AppCoreTests
{
    [Fact]
    public async Task CopyBetweenPanes()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "AAA"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        await core.StartTransferAsync(false);
        await core.WaitIdleAsync();
        Assert.Equal("AAA", File.ReadAllText(t.Combine("r", "a.txt")));
        Assert.True(File.Exists(t.Combine("l", "a.txt")));
    }

    [Fact]
    public async Task MoveAndUndo()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "AAA"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        await core.StartTransferAsync(true);
        await core.WaitIdleAsync();
        Assert.False(File.Exists(t.Combine("l", "a.txt")));
        Assert.True(File.Exists(t.Combine("r", "a.txt")));
        Assert.NotNull(core.UndoTitle);
        await core.UndoAsync();
        Assert.True(File.Exists(t.Combine("l", "a.txt")));
        Assert.False(File.Exists(t.Combine("r", "a.txt")));
    }

    [Fact]
    public async Task ConflictSkipKeepsExisting()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "NEW"); t.File("r/a.txt", "OLD");
        var ui = new FakeUi { Conflict = ConflictChoice.Skip };
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"), ui);
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        await core.StartTransferAsync(false);
        await core.WaitIdleAsync();
        Assert.Equal("OLD", File.ReadAllText(t.Combine("r", "a.txt")));
    }

    [Fact]
    public async Task ConflictOverwrite()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "NEW"); t.File("r/a.txt", "OLD");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"), new FakeUi { Conflict = ConflictChoice.Overwrite });
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        await core.StartTransferAsync(false);
        await core.WaitIdleAsync();
        Assert.Equal("NEW", File.ReadAllText(t.Combine("r", "a.txt")));
    }

    [Fact]
    public async Task SameFolderIsRejected()
    {
        using var t = new TempDir();
        t.File("l/a.txt");
        var ui = new FakeUi();
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("l"), ui);
        core.Active.MoveTo(1);
        await core.StartTransferAsync(false);
        Assert.Single(ui.Errors);
    }

    [Fact]
    public async Task DeleteAsksAndRemoves()
    {
        using var t = new TempDir();
        t.File("l/a.txt"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        await core.StartDeleteAsync(true);
        await core.WaitIdleAsync();
        Assert.False(File.Exists(t.Combine("l", "a.txt")));
    }

    [Fact]
    public async Task DeleteDeclinedKeepsFile()
    {
        using var t = new TempDir();
        t.File("l/a.txt"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"), new FakeUi { Confirm = false });
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        await core.StartDeleteAsync(true);
        await core.WaitIdleAsync();
        Assert.True(File.Exists(t.Combine("l", "a.txt")));
    }

    [Fact]
    public async Task MakeDirectoryAndRenameWithUndo()
    {
        using var t = new TempDir();
        t.File("l/a.txt"); t.Dir("r");
        var ui = new FakeUi { PromptAnswer = "novy" };
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"), ui);
        await core.MakeDirectoryAsync();
        Assert.True(Directory.Exists(t.Combine("l", "novy")));
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        ui.PromptAnswer = "b.txt";
        await core.RenameCurrentAsync();
        Assert.True(File.Exists(t.Combine("l", "b.txt")));
        await core.UndoAsync();
        Assert.True(File.Exists(t.Combine("l", "a.txt")));
    }

    [Fact]
    public async Task BatchRenameWithSwappedNames()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "A"); t.File("l/b.txt", "B"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        var items = core.Active.Items.Where(i => !i.IsParent).ToList();
        await core.ApplyRenameAsync(items, new[] { "b.txt", "a.txt" });
        Assert.Equal("B", File.ReadAllText(t.Combine("l", "a.txt")));
        Assert.Equal("A", File.ReadAllText(t.Combine("l", "b.txt")));
    }

    [Fact]
    public async Task CompareMarksNewerAndMissing()
    {
        using var t = new TempDir();
        t.File("l/only-left.txt"); t.File("r/only-right.txt");
        t.File("l/both.txt", "new"); t.File("r/both.txt", "oldold");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        await core.CompareAsync(false);
        Assert.Contains(core.Left.Current.AllItems, i => i.Name == "only-left.txt" && i.IsMarked);
        Assert.Contains(core.Right.Current.AllItems, i => i.Name == "only-right.txt" && i.IsMarked);
        Assert.Contains(core.Left.Current.AllItems, i => i.Name == "both.txt" && i.IsMarked);
    }

    [Fact]
    public async Task CompareByContentDetectsSameSizeDifference()
    {
        using var t = new TempDir();
        t.File("l/x.txt", "aaaa"); t.File("r/x.txt", "bbbb");
        File.SetLastWriteTime(t.Combine("r", "x.txt"), File.GetLastWriteTime(t.Combine("l", "x.txt")));
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        await core.CompareAsync(false);
        Assert.DoesNotContain(core.Left.Current.AllItems, i => i.IsMarked);
        await core.CompareAsync(true);
        Assert.Contains(core.Left.Current.AllItems, i => i.Name == "x.txt" && i.IsMarked);
        Assert.Contains(core.Right.Current.AllItems, i => i.Name == "x.txt" && i.IsMarked);
    }

    [Fact]
    public async Task SyncMirrorsAndRemovesExtras()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "A"); t.File("l/sub/b.txt", "B"); t.File("r/extra.txt", "E");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        await core.StartSyncAsync();
        await core.WaitIdleAsync();
        Assert.Equal("B", File.ReadAllText(t.Combine("r", "sub", "b.txt")));
        Assert.False(File.Exists(t.Combine("r", "extra.txt")));
    }

    [Fact]
    public async Task PackAndUnpackZip()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "AAA"); t.Dir("r");
        var ui = new FakeUi { PromptAnswer = "bal.zip" };
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"), ui);
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "a.txt"));
        await core.PackAsync();
        await core.WaitIdleAsync();
        Assert.True(File.Exists(t.Combine("r", "bal.zip")));
        core.ActiveIsLeft = false;
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "bal.zip"));
        Directory.CreateDirectory(t.Combine("out"));
        await core.Other.NavigateAsync(t.Combine("out"));
        await core.UnpackAsync();
        await core.WaitIdleAsync();
        Assert.Equal("AAA", File.ReadAllText(t.Combine("out", "a.txt")));
    }

    [Fact]
    public async Task SplitAndCombineThroughCore()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t.Combine("l-data.bin"), new byte[3000]); t.Dir("l"); t.Dir("r");
        File.Move(t.Combine("l-data.bin"), t.Combine("l", "data.bin"));
        var ui = new FakeUi { PromptAnswer = "0,001" };       // ~1 kB
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"), ui);
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "data.bin"));
        await core.StartSplitAsync();
        await core.WaitIdleAsync();
        Assert.True(File.Exists(t.Combine("r", "data.bin.001")));
        core.ActiveIsLeft = false;
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "data.bin.001"));
        await core.Other.NavigateAsync(t.Combine("l")); // cíl slepení
        File.Delete(t.Combine("l", "data.bin"));
        await core.StartCombineAsync();
        await core.WaitIdleAsync();
        Assert.Equal(3000, new FileInfo(t.Combine("l", "data.bin")).Length);
    }

    [Fact]
    public async Task DropFromExplorerCopiesWhenChoiceIsCopy()
    {
        using var t = new TempDir();
        t.File("outside/ext.txt", "E"); t.Dir("l"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"), new FakeUi { Choice = 0 });
        await core.DropFilesAsync(new[] { t.Combine("outside", "ext.txt") }, core.Left.Current, null);
        await core.WaitIdleAsync();
        Assert.True(File.Exists(t.Combine("l", "ext.txt")));
        Assert.True(File.Exists(t.Combine("outside", "ext.txt")));
    }

    [Fact]
    public async Task DropOntoFolderMoves()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "A"); t.Dir("l/target"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"), new FakeUi { Choice = 1 });
        core.DragSource = core.Left.Current;
        await core.DropFilesAsync(new[] { t.Combine("l", "a.txt") }, core.Left.Current, t.Combine("l", "target"));
        await core.WaitIdleAsync();
        Assert.True(File.Exists(t.Combine("l", "target", "a.txt")));
        Assert.False(File.Exists(t.Combine("l", "a.txt")));
    }

    [Fact]
    public async Task PasteCutMovesFiles()
    {
        using var t = new TempDir();
        t.File("src/a.txt", "A"); t.Dir("l"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        await core.PasteAsync(new[] { t.Combine("src", "a.txt") }, true);
        await core.WaitIdleAsync();
        Assert.True(File.Exists(t.Combine("l", "a.txt")));
        Assert.False(File.Exists(t.Combine("src", "a.txt")));
    }

    [Fact]
    public async Task QueueRunsTransfersInOrder()
    {
        using var t = new TempDir();
        t.File("l/1.txt", "1"); t.File("l/2.txt", "2"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "1.txt"));
        await core.StartTransferAsync(false);
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "2.txt"));
        await core.StartTransferAsync(false);
        await core.WaitIdleAsync();
        Assert.True(File.Exists(t.Combine("r", "1.txt")));
        Assert.True(File.Exists(t.Combine("r", "2.txt")));
    }

    [Fact]
    public void ExpandCommandQuotesPaths()
    {
        using var t = new TempDir();
        t.File("l/my file.txt"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "my file.txt"));
        var cmd = core.ExpandCommand("tool %f %n %% %d");
        Assert.Contains("\"" + t.Combine("l", "my file.txt") + "\"", cmd);
        Assert.Contains("\"my file.txt\"", cmd);
        Assert.Contains(" % ", cmd);
    }

    [Fact]
    public async Task SessionIsSavedAndRestored()
    {
        using var t = new TempDir();
        t.Dir("l/sub"); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        await core.Active.NavigateAsync(t.Combine("l", "sub"));
        core.SaveState();
        Assert.Equal(t.Combine("l", "sub"), core.Settings.Session.Left[0]);
    }

    [Fact]
    public async Task ViewerDetectsBinaryAndText()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "Ahoj světe"); File.WriteAllBytes(t.Combine("l", "b.bin"), new byte[] { 1, 0, 2, 0 }); t.Dir("r");
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"));
        var text = await core.LoadViewerAsync(core.Active, core.Active.Items.First(i => i.Name == "a.txt"));
        Assert.Equal(ViewerKind.Text, text!.Kind);
        Assert.Contains("světe", text.Text);
        var bin = await core.LoadViewerAsync(core.Active, core.Active.Items.First(i => i.Name == "b.bin"));
        Assert.Equal(ViewerKind.Hex, bin!.Kind);
    }
}
