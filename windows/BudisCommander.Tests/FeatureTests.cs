using BudisCommander.Core;
using BudisCommander.Remote;
using Xunit;

namespace BudisCommander.Tests;

public class TabSetTests
{
    [Fact]
    public async Task SaveLoadAndDeleteTabSet()
    {
        using var t = new TempDir();
        var a = t.Dir("a"); var b = t.Dir("b"); var c = t.Dir("c");
        var core = Helpers.NewCore(a, b);
        core.Left.NewTab();                       // druhá záložka vlevo (stejná složka)
        await core.Left.Current.NavigateAsync(c);
        core.SaveTabSet("Projekt X");
        Assert.Single(core.Settings.TabSets);
        Assert.Equal(new[] { a, c }, core.Settings.TabSets[0].Left);
        Assert.Equal(1, core.Settings.TabSets[0].LeftSelected);

        // přepsání stejného jména nevytvoří duplicitu
        core.SaveTabSet("projekt x");
        Assert.Single(core.Settings.TabSets);

        // jiné rozložení a návrat
        core.Left.Replace(new[] { b }, 0);
        await core.Left.Current.LoadLocalAsync(b, "", true);
        Assert.Single(core.Left.Tabs);
        await core.LoadTabSetAsync(core.Settings.TabSets[0]);
        Assert.Equal(2, core.Left.Tabs.Count);
        Assert.Equal(c, core.Left.Current.Path);
        Assert.Equal(new[] { a, c }, core.Left.Tabs.Select(x => x.Path));

        core.DeleteTabSet(core.Settings.TabSets[0].Name);
        Assert.Empty(core.Settings.TabSets);
    }

    [Fact]
    public async Task MissingFoldersAreSkipped()
    {
        using var t = new TempDir();
        var a = t.Dir("a"); var b = t.Dir("b");
        var core = Helpers.NewCore(a, b);
        var set = new TabSet { Name = "x", Left = new() { t.Combine("neexistuje"), b }, LeftSelected = 1, Right = new() { t.Combine("taky-ne") } };
        await core.LoadTabSetAsync(set);
        Assert.Equal(new[] { b }, core.Left.Tabs.Select(x => x.Path));
        Assert.Single(core.Right.Tabs);                      // náhradní domovská složka
    }
}

public class ServerSyncTests
{
    public static IEnumerable<object[]> Servers()
    {
        yield return new object[] { "BUDIS_TEST_FTP", RemoteProtocol.Ftp };
        yield return new object[] { "BUDIS_TEST_SFTP", RemoteProtocol.Sftp };
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task MirrorLocalToServerAndBack(string variable, RemoteProtocol protocol)
    {
        var v = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(v)) return;
        var p = v.Split(':');
        var server = new SavedServer { Protocol = protocol, Host = p[0], Port = int.Parse(p[1]), User = p[2], Insecure = true };
        using var t = new TempDir();
        t.File("src/a.txt", "AAA"); t.File("src/sub/b.txt", "BBB"); t.Dir("dst");
        var ui = new FakeUi();
        var core = Helpers.NewCore(t.Combine("src"), t.Combine("dst"), ui);
        Assert.True(await core.ConnectAsync(server, p[3]));          // levý panel (aktivní) je server
        var session = core.Active.Connection!;
        var baseDir = core.Active.RemotePath;
        var folder = RemotePath.Child(baseDir, "sync-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await session.MkdirAsync(folder, CancellationToken.None);
            await session.UploadAsync(t.File("junk.txt", "PREBYVA"), RemotePath.Child(folder, "junk.txt"), null, CancellationToken.None);
            await core.Active.LoadRemoteAsync(session, folder, "");
            await core.Other.NavigateAsync(t.Combine("src"));

            // nahrání: aktivní je lokální (pravý), cíl je server
            core.ActiveIsLeft = false;
            await core.StartSyncAsync();
            await core.WaitIdleAsync();
            var list = await session.ListAsync(folder, CancellationToken.None);
            Assert.Contains(list, e => e.Name == "a.txt" && e.Size == 3);
            Assert.Contains(list, e => e.Name == "sub" && e.IsDirectory);
            Assert.DoesNotContain(list, e => e.Name == "junk.txt");     // přebývající soubor na serveru zmizel
            Assert.Contains(await session.ListAsync(RemotePath.Child(folder, "sub"), CancellationToken.None), e => e.Name == "b.txt");

            // druhé zrcadlení nemá co dělat
            await core.StartSyncAsync();
            await core.WaitIdleAsync();
            Assert.Contains("shodné", core.Notice ?? "");

            // stažení: aktivní je server, cíl je prázdná místní složka
            core.ActiveIsLeft = true;
            await core.Other.NavigateAsync(t.Combine("dst"));
            await core.StartSyncAsync();
            await core.WaitIdleAsync();
            Assert.Equal("AAA", File.ReadAllText(t.Combine("dst", "a.txt")));
            Assert.Equal("BBB", File.ReadAllText(t.Combine("dst", "sub", "b.txt")));
        }
        finally
        {
            try { await session.DeleteAsync(folder, true, CancellationToken.None); } catch { }
        }
    }

    [Fact]
    public async Task PlanSkipsSameSizeUnlessSourceIsNewer()
    {
        using var t = new TempDir();
        var f1 = t.File("a/same.txt", "123"); var f2 = t.File("b/same.txt", "123");
        File.SetLastWriteTimeUtc(f1, DateTime.UtcNow.AddHours(-5));      // zdroj starší, ale stejná velikost: nekopírovat
        File.SetLastWriteTimeUtc(f2, DateTime.UtcNow);
        var g1 = t.File("a/new.txt", "123"); var g2 = t.File("b/new.txt", "123");
        File.SetLastWriteTimeUtc(g1, DateTime.UtcNow);                   // zdroj novější: kopírovat
        File.SetLastWriteTimeUtc(g2, DateTime.UtcNow.AddHours(-5));
        var plan = await TreeMirror.PlanAsync(TreeMirror.LocalLister, t.Combine("a"), Path.Combine,
                                              TreeMirror.LocalLister, t.Combine("b"), Path.Combine, 120, CancellationToken.None);
        Assert.Single(plan.Copies);
        Assert.EndsWith("new.txt", plan.Copies[0].From);
        Assert.Empty(plan.Deletes);
    }
}

public class RcloneTests
{
    /// <summary>Běží jen když je v BUDIS_TEST_RCLONE název úložiště (a rclone je nainstalovaný).</summary>
    [Fact]
    public async Task ListUploadDownloadRenameDelete()
    {
        var remote = Environment.GetEnvironmentVariable("BUDIS_TEST_RCLONE");
        var exe = Rclone.FindExe(null);
        if (string.IsNullOrEmpty(remote) || exe == null) return;
        using var t = new TempDir();
        using var s = new RcloneSession(exe, remote);
        await s.ConnectAsync(CancellationToken.None);
        Assert.Contains(remote, await Rclone.ListRemotesAsync(exe));

        var root = "/it-" + Guid.NewGuid().ToString("N")[..8];
        await s.MkdirAsync(root, CancellationToken.None);
        try
        {
            t.File("up/a.txt", "Obsah A"); t.File("up/sub/b.txt", "Obsah B");
            var fractions = new List<double>();
            await s.UploadAsync(t.Combine("up"), RemotePath.Child(root, "up"), new Progress<(string Text, double Fraction)>(p => fractions.Add(p.Fraction)), CancellationToken.None);
            var list = await s.ListAsync(RemotePath.Child(root, "up"), CancellationToken.None);
            Assert.Contains(list, e => e.Name == "a.txt" && !e.IsDirectory && e.Size == 7 && e.Modified != null);
            Assert.Contains(list, e => e.Name == "sub" && e.IsDirectory);

            await s.DownloadAsync(RemotePath.Child(root, "up"), true, 0, t.Combine("down"), null, CancellationToken.None);
            Assert.Equal("Obsah A", File.ReadAllText(t.Combine("down", "a.txt")));
            Assert.Equal("Obsah B", File.ReadAllText(t.Combine("down", "sub", "b.txt")));

            var head = await s.ReadHeadAsync(RemotePath.Child(root, "up/a.txt"), 4, CancellationToken.None);
            Assert.Equal("Obsa", System.Text.Encoding.UTF8.GetString(head));

            await s.RenameAsync(RemotePath.Child(root, "up/a.txt"), RemotePath.Child(root, "up/c.txt"), CancellationToken.None);
            list = await s.ListAsync(RemotePath.Child(root, "up"), CancellationToken.None);
            Assert.Contains(list, e => e.Name == "c.txt");
            Assert.DoesNotContain(list, e => e.Name == "a.txt");

            await s.DeleteAsync(RemotePath.Child(root, "up"), true, CancellationToken.None);
            Assert.Empty(await s.ListAsync(root, CancellationToken.None));
        }
        finally
        {
            try { await s.DeleteAsync(root, true, CancellationToken.None); } catch { }
        }
    }

    [Fact]
    public async Task ErrorOnMissingRemote()
    {
        var exe = Rclone.FindExe(null);
        if (exe == null || Environment.GetEnvironmentVariable("BUDIS_TEST_RCLONE") == null) return;
        using var s = new RcloneSession(exe, "neexistujici-uloziste");
        await Assert.ThrowsAnyAsync<Exception>(() => s.ConnectAsync(CancellationToken.None));
    }
}

public class UpdaterTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public byte[] Zip = Array.Empty<byte>();
        public string Commit = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/releases/tags/"))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    body = $"Samostatná aplikace.\n\nSestavení z commitu: `{Commit}`",
                    assets = new[] { new { name = Updater.AssetNameForThisMachine, size = Zip.Length, browser_download_url = "https://example.test/asset.zip" } },
                });
                return Task.FromResult(new HttpResponseMessage { Content = new StringContent(json) });
            }
            return Task.FromResult(new HttpResponseMessage { Content = new ByteArrayContent(Zip) });
        }
    }

    private static byte[] MakeZip(string exeText)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var e = z.CreateEntry("BudisCommander.exe");
            using var w = new StreamWriter(e.Open()); w.Write(exeText);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task CheckDetectsNewAndSameCommit()
    {
        var h = new FakeHandler { Commit = "abcdef1234567890", Zip = MakeZip("NOVA") };
        using var http = new HttpClient(h);
        Assert.Null(await Updater.CheckAsync(http, "abcdef1234567890", Updater.AssetNameForThisMachine));
        Assert.Null(await Updater.CheckAsync(http, "abcdef1", Updater.AssetNameForThisMachine));     // zkrácený commit
        var info = await Updater.CheckAsync(http, "1111111222222", Updater.AssetNameForThisMachine);
        Assert.NotNull(info);
        Assert.Equal("abcdef1234567890", info!.Commit);
    }

    [Fact]
    public async Task UpdateReplacesExeAndKeepsOld()
    {
        using var t = new TempDir();
        var exe = t.File("BudisCommander.exe", "STARA");
        var h = new FakeHandler { Commit = "abcdef1234567890", Zip = MakeZip("NOVA") };
        using var http = new HttpClient(h);
        var core = Helpers.NewCore(t.Dir("l"), t.Dir("r"));
        Assert.True(await core.UpdateAsync(http, "1111111222222", exe, t.Combine("work")));
        Assert.Equal("NOVA", File.ReadAllText(exe));
        Assert.Equal("STARA", File.ReadAllText(exe + ".old"));
        Updater.CleanupOld(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task UpToDateDoesNothing()
    {
        using var t = new TempDir();
        var exe = t.File("BudisCommander.exe", "STARA");
        var h = new FakeHandler { Commit = "abcdef1234567890", Zip = MakeZip("NOVA") };
        using var http = new HttpClient(h);
        var core = Helpers.NewCore(t.Dir("l"), t.Dir("r"));
        Assert.False(await core.UpdateAsync(http, "abcdef1234567890", exe, t.Combine("work")));
        Assert.Equal("STARA", File.ReadAllText(exe));
        Assert.Contains("nejnovější", core.Notice ?? "");
    }
}

public class SortByTypeTests
{
    [Fact]
    public async Task SortsByExtensionThenName()
    {
        using var t = new TempDir();
        var core = Helpers.NewCore(t.Dir("sorted-here"), t.Dir("other"));
        foreach (var n in new[] { "b.txt", "a.zip", "c.txt", "d.avi" }) t.File("sorted-here/" + n);
        t.Dir("sorted-here/slozka");
        await core.Active.SetSortAsync(SortKey.Ext);
        var names = core.Active.Items.Where(i => !i.IsParent).Select(i => i.Name).ToList();
        Assert.Equal(new[] { "slozka", "d.avi", "b.txt", "c.txt", "a.zip" }, names);
    }
}

public class FolderViewAndTypeFilterTests
{
    [Fact]
    public async Task SortIsRememberedPerFolder()
    {
        using var t = new TempDir();
        var a = t.Dir("a"); var b = t.Dir("b");
        t.File("a/x.txt"); t.File("a/y.avi"); t.File("b/p.txt");
        var core = Helpers.NewCore(a, b);
        var pane = core.Active;
        await pane.SetSortAsync(SortKey.Ext);                 // složka a: podle typu
        Assert.Equal(SortKey.Ext, pane.SortKey);
        await pane.NavigateAsync(b);                          // složka b nemá nic uloženo: výchozí
        Assert.Equal(SortKey.Name, pane.SortKey);
        await pane.SetSortAsync(SortKey.Size);
        await pane.SetSortAsync(SortKey.Size);                // sestupně
        await pane.NavigateAsync(a);
        Assert.Equal(SortKey.Ext, pane.SortKey);              // složka a si své řazení pamatuje
        Assert.True(pane.Ascending);
        await pane.NavigateAsync(b);
        Assert.Equal(SortKey.Size, pane.SortKey);
        Assert.False(pane.Ascending);
        // návrat na výchozí řazení záznam odstraní
        await pane.SetSortAsync(SortKey.Name);
        Assert.DoesNotContain(core.Settings.FolderViews, kv => kv.Key.EndsWith("b"));
    }

    [Fact]
    public async Task FolderViewsSurviveSaveAndLoad()
    {
        using var t = new TempDir();
        var s = new AppSettings();
        s.FolderViews["c:\\foto"] = new FolderView { Sort = SortKey.Date, Ascending = false };
        var path = t.Combine("s.json");
        s.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Equal(SortKey.Date, loaded.FolderViews["c:\\foto"].Sort);
        Assert.False(loaded.FolderViews["c:\\foto"].Ascending);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TypeFilterKeepsFoldersAndMatchingFiles()
    {
        using var t = new TempDir();
        var a = t.Dir("a");
        t.File("a/foto.JPG"); t.File("a/text.txt"); t.File("a/song.mp3"); t.File("a/film.mkv"); t.File("a/balik.zip"); t.Dir("a/slozka");
        var core = Helpers.NewCore(a, t.Dir("other"));
        var pane = core.Active;
        pane.SetTypeFilter("images");
        Assert.Equal(new[] { "slozka", "foto.JPG" }, pane.Items.Where(i => !i.IsParent).Select(i => i.Name));
        pane.SetTypeFilter("audio");
        Assert.Equal(new[] { "slozka", "song.mp3" }, pane.Items.Where(i => !i.IsParent).Select(i => i.Name));
        pane.SetTypeFilter("docs");
        Assert.Contains(pane.Items, i => i.Name == "text.txt");
        pane.SetTypeFilter("archives");
        Assert.Contains(pane.Items, i => i.Name == "balik.zip");
        pane.SetTypeFilter("video");
        Assert.Contains(pane.Items, i => i.Name == "film.mkv");
        Assert.Contains("položek", pane.Summary);
        pane.SetTypeFilter(null);
        Assert.Equal(5, pane.Items.Count(i => !i.IsDirectory && !i.IsParent));
        // spolupráce s textovým filtrem
        pane.SetTypeFilter("images");
        pane.SetFilter("zzz");
        Assert.Empty(pane.Items.Where(i => !i.IsParent));
    }
}

[Collection("Language")]
public class InfoTextsTests
{
    [Fact]
    public void EveryInfoLineHasEnglish()
    {
        try
        {
            Tr.SetLanguage("en");
            var czech = InfoTexts.News.Concat(InfoTexts.Features).Where(l => Tr.T(l) == l).ToList();
            Assert.Empty(czech);
        }
        finally { Tr.SetLanguage("cs"); }
    }
}
