using BudisCommander.Core.Terminal;
using Xunit;

namespace BudisCommander.Tests;

public class TerminalBufferTests
{
    [Fact]
    public void PlainTextAndNewlines()
    {
        var t = new TerminalBuffer(20, 5);
        t.Write("ahoj\r\nsvět");
        Assert.Equal("ahoj", t.GetLineText(0));
        Assert.Equal("svět", t.GetLineText(1));
        Assert.Equal((4, 1), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void WrapsAtEndOfLineAndScrollsIntoHistory()
    {
        var t = new TerminalBuffer(5, 3);
        t.Write("1234567890");
        Assert.Equal("12345", t.GetLineText(0)); Assert.Equal("67890", t.GetLineText(1));
        t.Write("\r\nA\r\nB\r\nC");
        Assert.True(t.ScrollbackCount >= 2);
        Assert.Equal("C", t.GetLineText(2));
        Assert.Equal("1234567890".Substring(0, 5), new string(t.AbsLine(0).Select(c => c.Ch).ToArray()).Trim());
    }

    [Fact]
    public void CursorMovementAndErase()
    {
        var t = new TerminalBuffer(10, 5);
        t.Write("AAAAAAAAAA\x1b[1;3H\x1b[K");               // kurzor na řádek 1, sloupec 3, smazat do konce řádku
        Assert.Equal("AA", t.GetLineText(0));
        t.Write("\x1b[3;1Hxyz\x1b[2J");
        Assert.Equal("", t.GetScreenText());
        t.Write("\x1b[H\x1b[2Cx\x1b[1Dy");
        Assert.Equal("  y", t.GetLineText(0));
    }

    [Fact]
    public void ColorsAndAttributes()
    {
        var t = new TerminalBuffer(10, 2);
        t.Write("\x1b[1;31mA\x1b[0mB\x1b[38;5;200mC\x1b[38;2;1;2;3mD\x1b[48;5;21mE");
        Assert.Equal(1, t.Get(0, 0).Fg); Assert.Equal(1, t.Get(0, 0).Attr & 1);
        Assert.Equal(-1, t.Get(1, 0).Fg); Assert.Equal(0, t.Get(1, 0).Attr);
        Assert.Equal(200, t.Get(2, 0).Fg);
        Assert.Equal(0x1000000 | (1 << 16) | (2 << 8) | 3, t.Get(3, 0).Fg);
        Assert.Equal(21, t.Get(4, 0).Bg);
    }

    [Fact]
    public void AlternateScreenRestoresMainScreen()
    {
        var t = new TerminalBuffer(10, 4);
        t.Write("hlavní");
        t.Write("\x1b[?1049h\x1b[Halt");
        Assert.True(t.InAltScreen); Assert.Equal("alt", t.GetLineText(0));
        t.Write("\x1b[?1049l");
        Assert.False(t.InAltScreen); Assert.Equal("hlavní", t.GetLineText(0));
    }

    [Fact]
    public void ScrollRegionAndInsertDeleteLines()
    {
        var t = new TerminalBuffer(6, 5);
        t.Write("1\r\n2\r\n3\r\n4\r\n5");
        t.Write("\x1b[2;4r");                                // oblast řádků 2–4, kurzor domů
        t.Write("\x1b[4;1H\n");                              // posun uvnitř oblasti
        Assert.Equal("1", t.GetLineText(0)); Assert.Equal("3", t.GetLineText(1)); Assert.Equal("4", t.GetLineText(2)); Assert.Equal("", t.GetLineText(3)); Assert.Equal("5", t.GetLineText(4));
        t.Write("\x1b[r\x1b[2;1H\x1b[M");                    // smazat řádek 2
        Assert.Equal("4", t.GetLineText(1));
    }

    [Fact]
    public void InsertAndDeleteCharacters()
    {
        var t = new TerminalBuffer(8, 2);
        t.Write("abcdef\x1b[1;3H\x1b[2@XY");
        Assert.Equal("abXYcdef", t.GetLineText(0));
        t.Write("\x1b[1;3H\x1b[2P");
        Assert.Equal("abcdef", t.GetLineText(0));
    }

    [Fact]
    public void ModesTitleAndReplies()
    {
        var t = new TerminalBuffer(10, 3);
        var replies = new List<string>(); t.Reply += replies.Add;
        t.Write("\x1b[?25l\x1b[?1h\x1b[?2004h\x1b]0;Název okna\a");
        Assert.False(t.CursorVisible); Assert.True(t.AppCursorKeys); Assert.True(t.BracketedPaste); Assert.Equal("Název okna", t.Title);
        t.Write("\x1b[2;3H\x1b[6n");
        Assert.Equal("\x1b[2;3R", replies.Single());
    }

    [Fact]
    public void SequencesSplitAcrossWritesAreHandled()
    {
        var t = new TerminalBuffer(10, 2);
        t.Write("a\x1b["); t.Write("31"); t.Write("mb");
        Assert.Equal(1, t.Get(1, 0).Fg);
    }

    [Fact]
    public void ResizeKeepsContentAndCursor()
    {
        var t = new TerminalBuffer(10, 4);
        t.Write("a\r\nb\r\nc");
        t.Resize(20, 2);
        Assert.Equal(20, t.Cols); Assert.Equal(2, t.Rows);
        Assert.Equal("c", t.GetLineText(t.CursorY));
        Assert.True(t.ScrollbackCount >= 1);
    }

    [Theory]
    [InlineData("Up", false, false, false, "\x1b[A")]
    [InlineData("Up", true, false, false, "\x1b[1;5A")]
    [InlineData("Delete", false, false, false, "\x1b[3~")]
    [InlineData("F5", false, false, false, "\x1b[15~")]
    [InlineData("Backspace", false, false, false, "\x7f")]
    [InlineData("Tab", false, false, true, "\x1b[Z")]
    public void KeySequences(string key, bool ctrl, bool alt, bool shift, string expected)
    {
        var t = new TerminalBuffer();
        Assert.Equal(expected, t.KeySequence(key, ctrl, alt, shift));
    }

    [Fact]
    public void ApplicationCursorKeysUseSs3()
    {
        var t = new TerminalBuffer();
        t.Write("\x1b[?1h");
        Assert.Equal("\x1bOA", t.KeySequence("Up", false, false, false));
    }
}

[Collection("Env")]
public class PtyTests
{
    private static async Task<bool> WaitFor(TerminalBuffer t, Func<string, bool> ok, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { lock (t) { if (ok(t.GetScreenText())) return true; } await Task.Delay(50); }
        return false;
    }

    [Fact]
    public async Task ShellRunsCommandInPseudoTerminalAndEchoesOutput()
    {
        if (!OperatingSystem.IsWindows() && !File.Exists("/usr/bin/script") && !File.Exists("/bin/script")) return;
        var t = new TerminalBuffer(100, 30);
        using var pty = new PtySession();
        var raw = new System.Text.StringBuilder();
        pty.Output += s => { lock (t) { raw.Append(s); t.Write(s); } };
        t.Reply += pty.Write;                                    // ConPTY čeká na odpověď na dotaz na polohu kurzoru
        pty.Start(Path.GetTempPath(), 100, 30);
        await Task.Delay(500);
        pty.Write(OperatingSystem.IsWindows() ? "echo ahoj-pty-123\r" : "echo ahoj-pty-$((100+23))\r");
        Assert.True(await WaitFor(t, s => s.Contains("ahoj-pty-123") && s.Split('\n').Any(l => l.Trim() == "ahoj-pty-123")),
                    "Výstup příkazu se v terminálu neobjevil:\n" + t.GetScreenText()
                    + "\n--- chyba: " + pty.LastError + "\n--- surový výstup: " + raw.ToString().Replace("\x1b", "<ESC>").Replace("\r", "<CR>").Replace("\n", "<LF>\n"));
    }

    private static string Esc(string s) => s.Replace("\x1b", "<ESC>").Replace("\r", "<CR>").Replace("\n", "<LF>");

    /// <summary>Zkouší několik způsobů spuštění ConPTY a hlásí, který z nich vrátil výstup programu (diagnostika).</summary>
    [Fact]
    public async Task ConPtyDiagnosticMatrix()
    {
        if (!OperatingSystem.IsWindows()) return;
        var report = new System.Text.StringBuilder("OS: " + Environment.OSVersion + "\n");
        var anyOk = false;
        foreach (var (name, exe, args, poke) in new[]
        {
            ("cmd /c echo", (string?)null, "/c echo marker-A-1", ""),
            ("cmd /c echo + focus", null, "/c echo marker-A-1", "\x1b[I"),
            ("cmd interactive + CR", null, null, "\r"),
            ("powershell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile -Command \"Write-Output marker-A-1\"", ""),
        })
        {
            using var pty = new PtySession(exe);
            var raw = new System.Text.StringBuilder();
            pty.Output += s => { lock (raw) raw.Append(s); };
            var t = new TerminalBuffer(100, 30);
            t.Reply += pty.Write;
            pty.Output += s => { lock (t) t.Write(s); };
            pty.Start(Path.GetTempPath(), 100, 30, args);
            if (poke.Length > 0) { await Task.Delay(500); pty.Write(poke); }
            if (args == null) { await Task.Delay(500); pty.Write("echo marker-A-1\r"); }
            var ok = await WaitFor(t, s => s.Contains("marker-A-1"), 6000);
            anyOk |= ok;
            string rawText; lock (raw) rawText = raw.ToString();
            report.Append($"[{name}] ok={ok} running={pty.Running} err={pty.LastError} raw={Esc(rawText)}\n");
        }
        Assert.True(anyOk, report.ToString());
    }

    [Fact]
    public async Task ExitedEventIsRaisedWhenShellEnds()
    {
        if (!OperatingSystem.IsWindows() && !File.Exists("/usr/bin/script") && !File.Exists("/bin/script")) return;
        using var pty = new PtySession();
        var done = new TaskCompletionSource<int>();
        pty.Exited += c => done.TrySetResult(c);
        var t = new TerminalBuffer(80, 24);
        t.Reply += pty.Write;
        pty.Output += s => { lock (t) t.Write(s); };
        pty.Start(Path.GetTempPath(), 80, 24);
        await Task.Delay(500);
        pty.Write("exit\r");
        Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(8000)));
    }
}
