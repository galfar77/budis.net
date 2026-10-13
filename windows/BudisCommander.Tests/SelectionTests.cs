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
            foreach (var local in new[] { "pack", "unpack", "checksum", "attributes", "copyFiles", "cutFiles", "pasteFiles", "terminal", "dirSizes", "batchRename", "openWith", "runAsAdmin" })
                Assert.DoesNotContain(local, m);
        }
        Assert.Contains("mkdir", ContextMenus.For(ContextKind.Empty, true));
    }

    [Fact]
    public void ExecutableFilesGetRunAsAdminOnlyOnWindows()
    {
        Assert.True(ContextMenus.IsRunnable("setup.EXE")); Assert.True(ContextMenus.IsRunnable("a.bat")); Assert.False(ContextMenus.IsRunnable("a.txt"));
        var withExe = ContextMenus.For(ContextKind.File, false, executable: true);
        var plain = ContextMenus.For(ContextKind.File, false, executable: false);
        Assert.Equal(OperatingSystem.IsWindows(), withExe.Contains("runAsAdmin"));
        Assert.DoesNotContain("runAsAdmin", plain);
        Assert.Equal(OperatingSystem.IsWindows(), plain.Contains("openWith"));
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

public class ColumnLayoutTests
{
    [Fact]
    public void DefinitionFollowsOrderAndVisibility()
    {
        var l = new ColumnLayout();
        Assert.Equal("26,*,92,140", l.Definition(false, false));
        Assert.Equal("26,*,60,48,92,140", l.Definition(true, true));
        Assert.True(l.IsDefault);
        Assert.True(l.Move("date", -1, false, false));                    // Změněno před Velikost
        Assert.Equal(new[] { "name", "date", "size" }, l.Visible(false, false));
        Assert.Equal("26,*,140,92", l.Definition(false, false));
        Assert.False(l.IsDefault);
        Assert.False(l.Move("name", -1, false, false));                   // dál doleva to nejde
        Assert.False(l.Move("size", 1, false, false));
    }

    [Fact]
    public void MoveSkipsHiddenColumnsAndResizeIsClamped()
    {
        var l = new ColumnLayout();
        Assert.True(l.Move("size", -1, false, false));                    // ext a attr se nezobrazují, Velikost se prohodí s Názvem
        Assert.Equal(new[] { "size", "name", "date" }, l.Visible(false, false));
        l.Resize("size", 5); Assert.Equal(ColumnLayout.MinWidth, l.WidthOf("size"));
        l.Resize("date", 9999); Assert.Equal(ColumnLayout.MaxWidth, l.WidthOf("date"));
        l.Resize("name", 100); Assert.False(l.Widths.ContainsKey("name"));
        Assert.Contains("date", l.Normalized());
        var odd = new ColumnLayout { Order = new() { "date", "zzz", "date" } };
        Assert.Equal(5, odd.Normalized().Count); Assert.Equal("date", odd.Normalized()[0]);
    }

    [Fact]
    public async Task LayoutIsRememberedPerFolderAndSurvivesSerialization()
    {
        using var t = new TempDir();
        t.File("a/f.txt"); t.File("b/g.txt");
        var settings = new AppSettings();
        var p = new PaneState(settings, t.Combine("a"));
        Assert.True(await p.LoadLocalAsync(t.Combine("a"), "", true));
        var l = new ColumnLayout(); l.Move("date", -1, false, false); l.Resize("size", 120);
        p.SetColumns(l);
        Assert.True(p.HasCustomColumns);
        Assert.Single(settings.FolderViews);

        var json = System.Text.Json.JsonSerializer.Serialize(settings.FolderViews);
        var back = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, FolderView>>(json)!;
        Assert.Equal(120, back.Values.Single().Columns!.WidthOf("size"));

        Assert.True(await p.LoadLocalAsync(t.Combine("b"), "", true));
        Assert.False(p.HasCustomColumns);                                   // jiná složka má výchozí rozložení
        Assert.True(await p.LoadLocalAsync(t.Combine("a"), "", true));
        Assert.True(p.HasCustomColumns); Assert.Equal(120, p.Columns.WidthOf("size"));

        p.SetDefaultColumns(l);                                             // pro všechny složky
        Assert.False(p.HasCustomColumns);
        Assert.Equal(120, p.Columns.WidthOf("size"));
        p.ResetColumns();
        Assert.Equal(120, p.Columns.WidthOf("size"));                       // výchozí z nastavení platí dál
        settings.DefaultColumns = null;
        Assert.True(p.Columns.IsDefault);
    }
}

public class ServerBookTests
{
    private static SavedServer S(string host, string name = "", string group = "", string user = "u") =>
        new() { Protocol = RemoteProtocol.Sftp, Host = host, User = user, Name = name, Group = group };

    [Fact]
    public void TreePutsUngroupedFirstThenGroupsAlphabetically()
    {
        var list = new List<SavedServer> { S("z.cz", group: "Práce"), S("a.cz"), S("m.cz", "Moje", "Doma"), S("b.cz", group: "práce") };
        var tree = ServerBook.Tree(list);
        Assert.Equal(new[] { "", "Doma", "Práce" }, tree.Select(g => g.Group).ToArray());
        Assert.Equal(2, tree[2].Servers.Count);                                  // „Práce“ a „práce“ jsou jedna skupina
        Assert.Equal(new[] { "Doma", "Práce" }, ServerBook.Groups(list).Select(g => g).ToArray()[..2]);
        Assert.StartsWith("Moje", ServerBook.DisplayName(S("m.cz", "Moje")));
        Assert.Equal(S("x.cz").Title, ServerBook.DisplayName(S("x.cz")));
    }

    [Fact]
    public void RememberReplacesInPlaceKeepsNameAndGroupAndTrimsOnlyUnmanaged()
    {
        var list = new List<SavedServer> { S("a.cz", "Moje A", "Práce"), S("b.cz") };
        var again = S("a.cz");                                                   // stejný server po novém připojení
        ServerBook.Remember(list, again);
        Assert.Equal(2, list.Count);
        Assert.Equal("a.cz", list[0].Host); Assert.Equal("Moje A", list[0].Name); Assert.Equal("Práce", list[0].Group);

        for (var i = 0; i < 30; i++) ServerBook.Remember(list, S($"h{i}.cz"));
        Assert.Contains(list, s => s.Host == "a.cz" && s.Name == "Moje A");      // spravovaný server zůstal
        Assert.Equal(ServerBook.MaxUnmanaged, list.Count(s => !ServerBook.IsManaged(s)));
        Assert.Equal("h29.cz", list[0].Host);                                     // nejnovější nahoře
    }

    [Fact]
    public void DuplicateGetsCopyNameAndOldSettingsStillLoad()
    {
        var dup = ServerBook.Duplicate(S("a.cz", "Moje", "Doma"));
        Assert.Equal("Moje (kopie)", dup.Name); Assert.Equal("Doma", dup.Group);
        Assert.Equal("a.cz (kopie)", ServerBook.Duplicate(S("a.cz")).Name);
        var old = System.Text.Json.JsonSerializer.Deserialize<SavedServer>("{\"Host\":\"x.cz\",\"User\":\"u\",\"Port\":22}")!;
        Assert.Equal("", old.Name); Assert.Equal("", old.Group); Assert.False(ServerBook.IsManaged(old));
    }
}
