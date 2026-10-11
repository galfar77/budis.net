using BudisCommander.Core;
using Xunit;

namespace BudisCommander.Tests;

public class SelectionTests
{
    [Theory]
    [InlineData("500", 500L)]
    [InlineData("10 KB", 10240L)]
    [InlineData("1,5 MB", 1572864L)]
    [InlineData("2gb", 2147483648L)]
    [InlineData("", null)]
    public void ParsesSizes(string text, long? expected)
    {
        Assert.True(SelectionCriteria.TryParseSize(text, out var v));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void RejectsNonsense()
    {
        Assert.False(SelectionCriteria.TryParseSize("hodně", out _));
        Assert.False(SelectionCriteria.TryParseDate("zítra", out _));
        Assert.True(SelectionCriteria.TryParseDate("31. 12. 2025", out var d)); Assert.Equal(new DateTime(2025, 12, 31), d);
        Assert.True(SelectionCriteria.TryParseDate("2025-03-01", out d)); Assert.Equal(new DateTime(2025, 3, 1), d);
    }

    [Fact]
    public void CombinesMaskKindSizeAndDate()
    {
        var c = new SelectionCriteria { Mask = "*.jpg;*.png", Kind = 1, MinSize = 100, MaxSize = 1000, ModifiedFrom = new DateTime(2025, 1, 1), ModifiedTo = new DateTime(2025, 1, 31) };
        Assert.True(c.Matches("a.JPG", false, 500, new DateTime(2025, 1, 31, 23, 0, 0)));
        Assert.False(c.Matches("a.txt", false, 500, new DateTime(2025, 1, 10)));
        Assert.False(c.Matches("a.jpg", false, 50, new DateTime(2025, 1, 10)));
        Assert.False(c.Matches("a.jpg", false, 5000, new DateTime(2025, 1, 10)));
        Assert.False(c.Matches("a.jpg", false, 500, new DateTime(2025, 2, 1)));
        Assert.False(c.Matches("a.jpg", false, 500, new DateTime(2024, 12, 31, 23, 59, 0)));
        Assert.False(c.Matches("a.jpg", false, 500, null));
        Assert.False(c.Matches("dir.jpg", true, 0, new DateTime(2025, 1, 10)));
    }

    [Fact]
    public async Task PaneMarksByCriteriaAndSameExtension()
    {
        using var t = new TempDir();
        t.File("a.jpg", new string('x', 10)); t.File("b.jpg", new string('x', 2000)); t.File("c.txt", new string('x', 2000)); t.Dir("d.jpg");
        var p = new PaneState(new AppSettings(), t.Path, true);
        Assert.True(await p.LoadLocalAsync(t.Path, "", true));
        var n = p.MarkWhere(new SelectionCriteria { Mask = "*.jpg", Kind = 1, MinSize = 1000 }, true);
        Assert.Equal(1, n);
        Assert.Equal(new[] { "b.jpg" }, p.Marked.Select(i => i.Name));
        p.UnmarkAll();
        p.SetCursorSilently(p.Items.ToList().FindIndex(i => i.Name == "a.jpg"));
        Assert.Equal(2, p.MarkSameExtension());
        Assert.Equal(new[] { "a.jpg", "b.jpg" }, p.Marked.Select(i => i.Name).OrderBy(x => x));
        Assert.Equal(1, p.MarkWhere(new SelectionCriteria { Mask = "a.*", Marked = true }, false));
        Assert.Single(p.Marked);
    }
}

public class ContextMenuTests
{
    private static FileEntry E(string name, bool dir = false, bool parent = false) => new() { Name = name, FullPath = "/x/" + name, IsDirectory = dir, IsParent = parent };

    [Fact]
    public void KindDependsOnWhatIsUnderThePointer()
    {
        Assert.Equal(ContextKind.Empty, ContextMenus.KindOf(0, false, null));
        Assert.Equal(ContextKind.Empty, ContextMenus.KindOf(0, false, E("..", true, true)));
        Assert.Equal(ContextKind.File, ContextMenus.KindOf(0, false, E("a.txt")));
        Assert.Equal(ContextKind.Folder, ContextMenus.KindOf(0, false, E("dir", true)));
        Assert.Equal(ContextKind.Archive, ContextMenus.KindOf(0, false, E("a.zip")));
        Assert.Equal(ContextKind.Multiple, ContextMenus.KindOf(3, true, E("a.txt")));
        Assert.Equal(ContextKind.File, ContextMenus.KindOf(3, false, E("a.txt")));    // pravý klik mimo výběr ho přebije
    }

    [Fact]
    public void FileMenuHasOpenCopyAndDeleteAndNoDoubleSeparators()
    {
        var m = ContextMenus.For(ContextKind.File, remote: false);
        Assert.Contains("open", m); Assert.Contains("view", m); Assert.Contains("copy", m); Assert.Contains("delete", m); Assert.Contains("checksum", m);
        Assert.NotEqual(ContextMenus.Separator, m[0]); Assert.NotEqual(ContextMenus.Separator, m[^1]);
        for (var i = 1; i < m.Count; i++) Assert.False(m[i] == ContextMenus.Separator && m[i - 1] == ContextMenus.Separator);
    }

    [Fact]
    public void RemoteMenuOmitsLocalOnlyActions()
    {
        foreach (var kind in Enum.GetValues<ContextKind>())
        {
            var m = ContextMenus.For(kind, remote: true);
            foreach (var local in new[] { "pack", "unpack", "checksum", "attributes", "copyFiles", "cutFiles", "pasteFiles", "terminal", "dirSizes", "batchRename" })
                Assert.DoesNotContain(local, m);
        }
        Assert.Contains("mkdir", ContextMenus.For(ContextKind.Empty, true));
    }

    [Fact]
    public void ArchiveMenuOffersUnpackAndEveryIdIsAKnownAction()
    {
        Assert.Contains("unpack", ContextMenus.For(ContextKind.Archive, false));
        var known = BudisCommander.Ui.Actions.All.Select(a => a.Id).ToHashSet();
        foreach (var kind in Enum.GetValues<ContextKind>())
            foreach (var remote in new[] { false, true })
                foreach (var id in ContextMenus.For(kind, remote).Where(i => i != ContextMenus.Separator))
                    Assert.Contains(id, known);
    }
}
