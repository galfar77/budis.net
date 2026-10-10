using BudisCommander.Core;
using BudisCommander.Remote;
using Xunit;

namespace BudisCommander.Tests;

/// <summary>
/// Integrační testy proti skutečným serverům. Spustí se jen když jsou nastavené proměnné prostředí:
/// BUDIS_TEST_FTP=host:port:uživatel:heslo a BUDIS_TEST_SFTP=host:port:uživatel:heslo.
/// </summary>
public class RemoteTests
{
    private static SavedServer? Server(string variable, RemoteProtocol protocol, out string password)
    {
        password = "";
        var v = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(v)) return null;
        var p = v.Split(':');
        password = p[3];
        return new SavedServer { Protocol = protocol, Host = p[0], Port = int.Parse(p[1]), User = p[2], Insecure = true };
    }

    private static RemoteSession Session(SavedServer s, string password) => s.Protocol == RemoteProtocol.Sftp
        ? new SftpSession(s.Host, s.EffectivePort, s.User, password, "", (_, _) => true)
        : new FtpSession(s.Host, s.EffectivePort, s.User, password, false, true);

    public static IEnumerable<object[]> Servers()
    {
        yield return new object[] { "BUDIS_TEST_FTP", RemoteProtocol.Ftp };
        yield return new object[] { "BUDIS_TEST_SFTP", RemoteProtocol.Sftp };
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task ListUploadDownloadRenameDelete(string variable, RemoteProtocol protocol)
    {
        var server = Server(variable, protocol, out var pw);
        if (server == null) return;
        using var t = new TempDir();
        using var s = Session(server, pw);
        await s.ConnectAsync(CancellationToken.None);
        var root = RemotePath.Child(s.StartPath, "it-" + Guid.NewGuid().ToString("N")[..8]);
        await s.MkdirAsync(root, CancellationToken.None);
        try
        {
            // nahrání složky se souborem
            t.File("up/a.txt", "Obsah A"); t.File("up/sub/b.txt", "Obsah B");
            var fractions = new List<double>();
            await s.UploadAsync(t.Combine("up"), RemotePath.Child(root, "up"), new Progress<(string Text, double Fraction)>(p => fractions.Add(p.Fraction)), CancellationToken.None);
            var list = await s.ListAsync(RemotePath.Child(root, "up"), CancellationToken.None);
            Assert.Contains(list, e => e.Name == "a.txt" && !e.IsDirectory && e.Size == 7);
            Assert.Contains(list, e => e.Name == "sub" && e.IsDirectory);

            // stažení celé složky
            await s.DownloadAsync(RemotePath.Child(root, "up"), true, 0, t.Combine("down"), null, CancellationToken.None);
            Assert.Equal("Obsah A", File.ReadAllText(t.Combine("down", "a.txt")));
            Assert.Equal("Obsah B", File.ReadAllText(t.Combine("down", "sub", "b.txt")));

            // náhled začátku souboru
            var head = await s.ReadHeadAsync(RemotePath.Child(root, "up/a.txt"), 4, CancellationToken.None);
            Assert.Equal("Obsa", System.Text.Encoding.UTF8.GetString(head));

            // přejmenování
            await s.RenameAsync(RemotePath.Child(root, "up/a.txt"), RemotePath.Child(root, "up/renamed.txt"), CancellationToken.None);
            var after = await s.ListAsync(RemotePath.Child(root, "up"), CancellationToken.None);
            Assert.Contains(after, e => e.Name == "renamed.txt");
            Assert.DoesNotContain(after, e => e.Name == "a.txt");

            // průběh se hlásil a skončil na 1
            Assert.NotEmpty(fractions);
            Assert.Equal(1.0, fractions.Max(), 2);
        }
        finally
        {
            await s.DeleteAsync(root, true, CancellationToken.None);
        }
        var rootList = await s.ListAsync(RemotePath.Parent(root), CancellationToken.None);
        Assert.DoesNotContain(rootList, e => e.FullPath == root);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task CancellationStopsTransfer(string variable, RemoteProtocol protocol)
    {
        var server = Server(variable, protocol, out var pw);
        if (server == null) return;
        using var t = new TempDir();
        var big = t.Combine("big.bin");
        File.WriteAllBytes(big, new byte[40 * 1024 * 1024]);
        using var s = Session(server, pw);
        await s.ConnectAsync(CancellationToken.None);
        var root = RemotePath.Child(s.StartPath, "cancel-" + Guid.NewGuid().ToString("N")[..8]);
        await s.MkdirAsync(root, CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource();
            var progress = new Progress<(string Text, double Fraction)>(p => { try { if (p.Fraction > 0.05) cts.Cancel(); } catch (ObjectDisposedException) { } });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.UploadAsync(big, RemotePath.Child(root, "big.bin"), progress, cts.Token));
        }
        finally { try { await s.DeleteAsync(root, true, CancellationToken.None); } catch { } }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task WrongPasswordFails(string variable, RemoteProtocol protocol)
    {
        var server = Server(variable, protocol, out _);
        if (server == null || server.User == "") return;
        using var s = Session(server, "spatne-heslo");
        await Assert.ThrowsAnyAsync<Exception>(() => s.ConnectAsync(CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task FullFlowThroughAppCore(string variable, RemoteProtocol protocol)
    {
        var server = Server(variable, protocol, out var pw);
        if (server == null) return;
        using var t = new TempDir();
        t.File("l/local.txt", "LOKAL"); t.Dir("r");
        var ui = new FakeUi { PromptAnswer = "novaslozka" };
        var core = Helpers.NewCore(t.Combine("l"), t.Combine("r"), ui);
        Assert.True(await core.ConnectAsync(server, pw));
        var pane = core.Active;
        Assert.True(pane.IsRemote);
        var baseDir = pane.RemotePath;
        var folder = "core-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            ui.PromptAnswer = folder;
            await core.MakeDirectoryAsync();                       // vytvoří složku na serveru
            Assert.Contains(pane.Items, i => i.Name == folder);
            await pane.LoadRemoteAsync(pane.Connection!, RemotePath.Child(baseDir, folder), "");

            // levý panel je server (byl aktivní při připojení), pravý je lokální
            Assert.True(core.ActiveIsLeft);
            Assert.False(core.Other.IsRemote);

            // nahrání: aktivní je lokální pravý panel, cíl je server
            await core.Right.Current.NavigateAsync(t.Combine("l"));
            core.ActiveIsLeft = false;
            core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "local.txt"));
            await core.StartTransferAsync(false);
            await core.WaitIdleAsync();
            core.ActiveIsLeft = true;
            await core.Active.ReloadAsync();
            Assert.Contains(core.Active.Items, i => i.Name == "local.txt");

            // stažení: aktivní je server, cíl je lokální složka r2
            t.Dir("r2");
            await core.Other.NavigateAsync(t.Combine("r2"));
            core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "local.txt"));
            await core.StartTransferAsync(false);
            await core.WaitIdleAsync();
            Assert.Equal("LOKAL", File.ReadAllText(t.Combine("r2", "local.txt")));

            // přejmenovat na serveru a vrátit zpět
            ui.PromptAnswer = "prejmenovano.txt";
            core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "local.txt"));
            await core.RenameCurrentAsync();
            Assert.Contains(core.Active.Items, i => i.Name == "prejmenovano.txt");
            await core.UndoAsync();
            Assert.Contains(core.Active.Items, i => i.Name == "local.txt");

            // smazat na serveru
            core.Active.MoveTo(core.Active.Items.FindIndex(i => i.Name == "local.txt"));
            await core.StartDeleteAsync(true);
            await core.WaitIdleAsync();
            Assert.DoesNotContain(core.Active.Items, i => i.Name == "local.txt");

            // náhled souboru na serveru přes prohlížeč
            await core.Active.Connection!.UploadAsync(t.Combine("l", "local.txt"), RemotePath.Child(core.Active.RemotePath, "view.txt"), null, CancellationToken.None);
            await core.Active.ReloadAsync();
            var viewItem = core.Active.Items.First(i => i.Name == "view.txt");
            var view = await core.LoadViewerAsync(core.Active, viewItem);
            Assert.Equal("LOKAL", view!.Text);
        }
        finally
        {
            try { await core.Active.Connection!.DeleteAsync(RemotePath.Child(baseDir, folder), true, CancellationToken.None); } catch { }
        }
    }
}

public class SftpKeyTests
{
    [Theory]
    [InlineData("BUDIS_TEST_SFTP_KEY_ED")]
    [InlineData("BUDIS_TEST_SFTP_KEY_RSA")]
    public async Task KeyAuthenticationWorks(string variable)
    {
        var key = Environment.GetEnvironmentVariable(variable);
        var hostInfo = Environment.GetEnvironmentVariable("BUDIS_TEST_SFTP");
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(hostInfo)) return;
        var p = hostInfo.Split(':');
        using var s = new BudisCommander.Remote.SftpSession(p[0], int.Parse(p[1]), p[2], "", key, (_, _) => true);
        await s.ConnectAsync(CancellationToken.None);
        Assert.NotEmpty(s.StartPath);
        var list = await s.ListAsync(s.StartPath, CancellationToken.None);
        Assert.NotNull(list);
    }
}
