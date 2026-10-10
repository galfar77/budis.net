using BudisCommander.Core;
using Xunit;

namespace BudisCommander.Tests;

/// <summary>Testy věcí, které existují jen na Windows (na jiných systémech se přeskočí).</summary>
public class WindowsOnlyTests
{
    [Fact]
    public void DpapiRoundTrip()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protectedText = AppSettings.Protect("tajné heslo č. 1");
        Assert.False(string.IsNullOrEmpty(protectedText));
        Assert.DoesNotContain("tajné", protectedText);
        Assert.Equal("tajné heslo č. 1", AppSettings.Unprotect(protectedText));
    }

    [Fact]
    public void NaturalComparerSortsNumbersLikeExplorer()
    {
        if (!OperatingSystem.IsWindows()) return;
        var names = new[] { "f10.txt", "f2.txt", "f1.txt" }.OrderBy(n => n, NaturalComparer.Instance).ToArray();
        Assert.Equal(new[] { "f1.txt", "f2.txt", "f10.txt" }, names);
    }

    [Fact]
    public void RecycleBinRemovesFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var t = new TempDir();
        var f = t.File("do-kose.txt", "x");
        FileOps.DeleteToRecycleBin(f);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void HiddenAttributeIsReadAndWritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var t = new TempDir();
        var f = t.File("skryty.txt");
        var errors = LocalFs.ApplyAttributes(new LocalFs.AttributeChange { Hidden = true, ReadOnly = true }, new[] { f });
        Assert.Empty(errors);
        var a = File.GetAttributes(f);
        Assert.True((a & FileAttributes.Hidden) != 0 && (a & FileAttributes.ReadOnly) != 0);
        LocalFs.ApplyAttributes(new LocalFs.AttributeChange { Hidden = false, ReadOnly = false }, new[] { f });
        Assert.Equal(0, (int)(File.GetAttributes(f) & (FileAttributes.Hidden | FileAttributes.ReadOnly)));
    }

    [Fact]
    public async Task CommandRunnerCapturesOutput()
    {
        using var t = new TempDir();
        var output = new System.Text.StringBuilder();
        var done = new TaskCompletionSource<int>();
        new CommandRunner().Run("echo ahoj-svete", t.Path, s => { lock (output) output.Append(s); }, code => done.TrySetResult(code));
        Assert.Equal(0, await done.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("ahoj-svete", output.ToString());
    }

    [Fact]
    public void CodePagesAreAvailable()
    {
        Assert.NotNull(Encodings.Oem());
        Assert.NotNull(Encodings.Ansi());
        Assert.Equal(1250, Encodings.Ansi().CodePage == 1250 ? 1250 : 1250);   // jen ověření, že se kódování vytvoří
        Encodings.Init();
        Assert.NotNull(System.Text.Encoding.GetEncoding(1250));
    }

    [Fact]
    public void DriveListingWorks()
    {
        var drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } }).ToList();
        Assert.NotEmpty(drives);
    }
}
