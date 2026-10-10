using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BudisCommander.Core;
using BudisCommander.Ui;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(BudisCommander.Tests.TestAppBuilder))]

namespace BudisCommander.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class UiTests
{
    private static MainWindow Open(TempDir t, out AppSettings settings, FakeUi? ui = null)
    {
        settings = new AppSettings();
        settings.Session = new SessionState { Left = new() { t.Combine("l") }, Right = new() { t.Combine("r") }, ActiveLeft = true };
        var w = new MainWindow(settings, t.Combine("settings.json"), ui ?? new FakeUi());
        w.Show();
        return w;
    }

    private static async Task Settle(int ms = 400)
    {
        for (int i = 0; i < ms / 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
    }

    private static void Press(Window w, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        var raw = (RawInputModifiers)(int)mods;
        w.KeyPress(key, raw, PhysicalKey.None, null);
        w.KeyRelease(key, raw, PhysicalKey.None, null);
    }

    private static void Shot(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("BUDIS_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var frame = w.CaptureRenderedFrame();
        frame?.Save(System.IO.Path.Combine(dir, name + ".png"));
    }

    [AvaloniaFact]
    public async Task MainWindowShowsBothPanels()
    {
        using var t = new TempDir();
        t.File("l/alpha.txt", "A"); t.File("l/beta.jpg", "B"); t.Dir("l/Složka"); t.File("r/gamma.zip", "G");
        var w = Open(t, out _);
        await Settle(800);
        Assert.Contains(w.Core.Left.Current.Items, i => i.Name == "alpha.txt");
        Assert.Contains(w.Core.Right.Current.Items, i => i.Name == "gamma.zip");
        Shot(w, "main");
        w.Close();
    }

    [AvaloniaFact]
    public async Task KeyboardNavigationAndMarking()
    {
        using var t = new TempDir();
        t.File("l/a.txt"); t.File("l/b.txt"); t.File("l/c.txt"); t.Dir("r");
        var w = Open(t, out _);
        await Settle(800);
        var pane = w.Core.Active;
        Assert.Equal(0, pane.Cursor);
        Press(w, Key.Down); Press(w, Key.Down);
        Assert.Equal(2, pane.Cursor);
        Press(w, Key.Space);                  // označí a posune
        Assert.Single(pane.Marked);
        Assert.Equal(3, pane.Cursor);
        Press(w, Key.Home);
        Assert.Equal(0, pane.Cursor);
        Press(w, Key.Tab);
        Assert.False(w.Core.ActiveIsLeft);
        Press(w, Key.Tab);
        Assert.True(w.Core.ActiveIsLeft);
        w.Close();
    }

    [AvaloniaFact]
    public async Task EnterAndBackspaceNavigate()
    {
        using var t = new TempDir();
        t.File("l/sub/in.txt"); t.Dir("r");
        var w = Open(t, out _);
        await Settle(800);
        var pane = w.Core.Active;
        pane.MoveTo(pane.Items.FindIndex(i => i.Name == "sub"));
        Press(w, Key.Enter);
        await Settle(600);
        Assert.Equal(t.Combine("l", "sub"), pane.Path);
        Press(w, Key.Back);
        await Settle(600);
        Assert.Equal(t.Combine("l"), pane.Path);
        w.Close();
    }

    [AvaloniaFact]
    public async Task FunctionKeysDoFileOperations()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "AAA"); t.File("l/b.txt", "BBB"); t.File("l/c.txt", "CCC"); t.Dir("r");
        var ui = new FakeUi { PromptAnswer = "nova" };
        var w = Open(t, out _, ui);
        await Settle(800);
        var pane = w.Core.Active;
        pane.MoveTo(pane.Items.FindIndex(i => i.Name == "a.txt"));

        Press(w, Key.F5);                                           // kopírovat
        for (int i = 0; i < 100 && !File.Exists(t.Combine("r", "a.txt")); i++) await Settle(100);
        Assert.True(File.Exists(t.Combine("r", "a.txt")));

        pane.MoveTo(pane.Items.FindIndex(i => i.Name == "b.txt"));
        Press(w, Key.F6);                                           // přesunout
        for (int i = 0; i < 100 && !File.Exists(t.Combine("r", "b.txt")); i++) await Settle(100);
        Assert.True(File.Exists(t.Combine("r", "b.txt")));
        Assert.False(File.Exists(t.Combine("l", "b.txt")));

        Press(w, Key.Z, KeyModifiers.Control);                      // vrátit přesun
        await Settle(600);
        Assert.True(File.Exists(t.Combine("l", "b.txt")));

        Press(w, Key.F7);                                           // nová složka
        await Settle(600);
        Assert.True(Directory.Exists(t.Combine("l", "nova")));

        pane.MoveTo(pane.Items.FindIndex(i => i.Name == "c.txt"));
        ui.PromptAnswer = "cc.txt";
        Press(w, Key.F2);                                           // přejmenovat
        await Settle(600);
        Assert.True(File.Exists(t.Combine("l", "cc.txt")));

        pane.MoveTo(pane.Items.FindIndex(i => i.Name == "cc.txt"));
        Press(w, Key.Delete, KeyModifiers.Shift);                   // smazat natrvalo
        for (int i = 0; i < 100 && File.Exists(t.Combine("l", "cc.txt")); i++) await Settle(100);
        Assert.False(File.Exists(t.Combine("l", "cc.txt")));
        w.Close();
    }

    [AvaloniaFact]
    public async Task MaskMarkingAndFilterKeys()
    {
        using var t = new TempDir();
        t.File("l/a.txt"); t.File("l/b.jpg"); t.File("l/c.jpg"); t.Dir("r");
        var ui = new FakeUi { PromptAnswer = "*.jpg" };
        var w = Open(t, out _, ui);
        await Settle(800);
        var pane = w.Core.Active;
        Press(w, Key.Add);
        await Settle(300);
        Assert.Equal(2, pane.Marked.Count());
        Press(w, Key.Escape);
        Assert.Empty(pane.Marked);
        Press(w, Key.A, KeyModifiers.Control);
        Assert.Equal(3, pane.Marked.Count());
        w.Close();
    }

    [AvaloniaFact]
    public async Task TabsAndPanelSwitching()
    {
        using var t = new TempDir();
        t.Dir("l/x"); t.Dir("r");
        var w = Open(t, out _);
        await Settle(800);
        Press(w, Key.T, KeyModifiers.Control);
        await Settle(200);
        Assert.Equal(2, w.Core.Left.Tabs.Count);
        Press(w, Key.W, KeyModifiers.Control);
        await Settle(200);
        Assert.Single(w.Core.Left.Tabs);
        w.Close();
    }

    [AvaloniaFact]
    public async Task ToolWindowsOpenWithoutCrash()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "one\ntwo\n"); t.File("r/b.txt", "one\nTWO\nthree\n");
        var w = Open(t, out var settings);
        await Settle(800);
        var windows = new List<Window>
        {
            new ViewerWindow(new ViewerData("a.txt", ViewerKind.Text, "ahoj\nsvět", null, null)),
            new ViewerWindow(new ViewerData("b (hex)", ViewerKind.Hex, LocalFs.HexDump(new byte[] { 1, 2, 3 }), null, null)),
            new SearchWindow(w.Core), new DuplicatesWindow(w.Core),
            new BatchRenameWindow(w.Core, w.Core.Left.Current.Items.Where(i => !i.IsParent).ToList()),
            new ChecksumWindow(w.Core.Left.Current.Items.Where(i => !i.IsParent).ToList()),
            new AttributesWindow(w.Core, w.Core.Left.Current.Items.Where(i => !i.IsParent).ToList()),
            new DiffWindow(t.Combine("l", "a.txt"), t.Combine("r", "b.txt")),
            new ConnectWindow(w.Core), new FavoritesWindow(w.Core), new NetworkWindow(w.Core),
            new SettingsWindow(settings), new UserMenuWindow(settings),
        };
        int n = 0;
        foreach (var win in windows)
        {
            win.Show();
            await Settle(300);
            Shot(win, $"tool{n++:00}-" + win.GetType().Name);
            win.Close();
        }
        w.Close();
    }

    [AvaloniaFact]
    public async Task ThemeAndColumnsApply()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "A"); t.Dir("l/dir"); t.Dir("r");
        var w = Open(t, out var settings);
        await Settle(800);
        settings.Theme = "dark"; settings.ShowExtColumn = true; settings.ShowAttrColumn = true; settings.FontSize = 15;
        w.ApplySettings();
        await Settle(500);
        Shot(w, "main-dark");
        w.Close();
    }

    [AvaloniaFact]
    public async Task QuickViewAndCommandLineToggle()
    {
        using var t = new TempDir();
        t.File("l/a.txt", "Obsah souboru\nDruhý řádek"); t.Dir("r");
        var w = Open(t, out _);
        await Settle(800);
        var pane = w.Core.Active;
        pane.MoveTo(pane.Items.FindIndex(i => i.Name == "a.txt"));
        Press(w, Key.Q, KeyModifiers.Control);
        await Settle(800);
        Shot(w, "quickview");
        Press(w, Key.J, KeyModifiers.Control);
        await Settle(300);
        Shot(w, "commandline");
        w.Close();
    }
}

public class GestureTests
{
    [Fact]
    public void AllDefaultGesturesParse()
    {
        foreach (var a in Actions.All.Where(a => a.DefaultGesture != null))
            Assert.True(Actions.GestureFor(a, new AppSettings()) != null, $"Zkratka „{a.DefaultGesture}“ akce {a.Id} se nepodařilo přečíst.");
    }

    [Fact]
    public void DefaultGesturesAreUnique()
    {
        var used = Actions.All.Where(a => a.DefaultGesture != null).GroupBy(a => a.DefaultGesture!.ToLowerInvariant()).Where(g => g.Count() > 1).ToList();
        Assert.Empty(used);
    }

    [Fact]
    public void CzechTextWithoutBomIsDecoded()
    {
        var bytes = Encodings.Ansi().GetBytes("Příliš žluťoučký kůň");
        // ANSI bajty nejsou platné UTF-8, takže se použije záložní kódování
        var view = AppCore.FromBytes("a.txt", bytes, null);
        Assert.Equal(ViewerKind.Text, view.Kind);
    }
}
