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
