using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using BudisCommander.Core;

namespace BudisCommander.Ui;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppCore _core;
    private readonly IUserInterface _ui;
    private readonly PanelControl _leftPanel;
    private readonly PanelControl _rightPanel;
    private readonly Grid _panelsGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
    private readonly GridSplitter _splitter = new() { Background = Brushes.Transparent, Width = 4, ResizeDirection = GridResizeDirection.Columns };
    private readonly TextBlock _notice = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly ProgressBar _progress = new() { Width = 220, Height = 14, Maximum = 1, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel = new() { Content = "Zrušit", IsVisible = false, Padding = new Thickness(10, 1) };
    private readonly Button _cancelAll = new() { Content = "Zrušit vše", IsVisible = false, Padding = new Thickness(10, 1) };
    private readonly TextBlock _queueText = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly Border _commandHost = new() { IsVisible = false };
    private readonly TextBox _commandOutput = UiKit.Mono("");
    private readonly TextBox _commandInput = new() { Watermark = "příkaz (Enter spustí, Esc zavře)" };
    private readonly Button _commandStop = new() { Content = "Zastavit", IsVisible = false };
    private readonly StackPanel _userBar = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 2) };
    private readonly Border _userBarHost = new();
    private readonly Menu _menu = new();
    private bool _quickView;
    private QuickViewControl? _quickViewControl;
    private readonly Dictionary<string, Func<Task>> _handlers = new();

    public MainWindow() : this(AppSettings.Load()) { }

    public AppCore Core => _core;

    public MainWindow(AppSettings settings, string? settingsPath = null, IUserInterface? ui = null)
    {
        _settings = settings;
        Title = "Budis Commander";
        Width = settings.WindowWidth; Height = settings.WindowHeight;
        MinWidth = 800; MinHeight = 480;
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BudisCommander/Assets/AppIcon.png"))); } catch { }

        _ui = ui ?? new WindowUi(() => this);
        _core = new AppCore(settings, _ui, settingsPath);
        ApplyTheme();

        _leftPanel = new PanelControl(_core, _core.Left, true);
        _rightPanel = new PanelControl(_core, _core.Right, false);
        _leftPanel.Activated += () => SetActive(true);
        _rightPanel.Activated += () => SetActive(false);

        RegisterHandlers();
        BuildMenu();
        Content = BuildLayout();
        _core.StatusChanged += () => Dispatcher.UIThread.Post(UpdateStatus);
        _core.SessionChanged += () => Dispatcher.UIThread.Post(() => { });

        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInputTunnel, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            await _core.LoadAllAsync();
            SetActive(_core.ActiveIsLeft);
            RebuildUserBar();
            if (Updater.SelfExePath() is { } self) Updater.CleanupOld(self);
            _ = _core.AutoCheckUpdateAsync();
        };
        Closing += (_, _) => OnClosing();
        Activated += (_, _) => FocusActiveList();
    }

    // --- sestavení okna -----------------------------------------------------------------------

    private Control BuildLayout()
    {
        Grid.SetColumn(_leftPanel, 0); Grid.SetColumn(_splitter, 1); Grid.SetColumn(_rightPanel, 2);
        _panelsGrid.Children.Add(_leftPanel); _panelsGrid.Children.Add(_splitter); _panelsGrid.Children.Add(_rightPanel);

        var status = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
            MinHeight = 24,
            Child = UiKit.HStack(6, _progress, _notice, _queueText, _cancel, _cancelAll),
        };
        _cancel.Click += (_, _) => _core.CancelJob();
        _cancelAll.Click += (_, _) => _core.CancelJob(true);

        _commandInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await RunTypedCommandAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; SetCommandVisible(false); }
        };
        _commandStop.Click += (_, _) => _core.Runner.Cancel();
        _commandOutput.Height = 130;
        var cmdGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 2) };
        Grid.SetColumn(_commandInput, 0); Grid.SetColumn(_commandStop, 1);
        inputRow.Children.Add(_commandInput); inputRow.Children.Add(_commandStop);
        Grid.SetRow(_commandOutput, 0); Grid.SetRow(inputRow, 1);
        cmdGrid.Children.Add(_commandOutput); cmdGrid.Children.Add(inputRow);
        _commandHost.Child = cmdGrid;

        _userBarHost.Child = new ScrollViewer { Content = _userBar, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var functionBar = BuildFunctionBar();

        var dock = new DockPanel();
        DockPanel.SetDock(_menu, Dock.Top);
        DockPanel.SetDock(functionBar, Dock.Bottom);
        DockPanel.SetDock(_userBarHost, Dock.Bottom);
        DockPanel.SetDock(_commandHost, Dock.Bottom);
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(_menu); dock.Children.Add(functionBar); dock.Children.Add(_userBarHost);
        dock.Children.Add(_commandHost); dock.Children.Add(status); dock.Children.Add(_panelsGrid);
        return dock;
    }

    private Control BuildFunctionBar()
    {
        var grid = new Grid { Margin = new Thickness(4) };
        var items = new (string Key, string Title, string Action)[]
        {
            ("F2", "Přejmenovat", "rename"), ("F3", "Zobrazit", "view"), ("F4", "Editovat", "edit"), ("F5", "Kopírovat", "copy"),
            ("F6", "Přesunout", "move"), ("F7", "Nová složka", "mkdir"), ("F8", "Smazat", "delete"),
            ("Alt+F7", "Hledat", "search"), ("Ctrl+N", "Server", "connect"),
        };
        for (int i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var (key, title, action) = items[i];
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = key, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x7C, 0xD3)) });
            content.Children.Add(new TextBlock { Text = title });
            var b = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(1), Focusable = false };
            var captured = action;
            b.Click += async (_, _) => { await Run(captured); };
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
        }
        return grid;
    }

    private void RebuildUserBar()
    {
        _userBar.Children.Clear();
        foreach (var cmd in _settings.UserCommands)
        {
            var c = cmd;
            var b = new Button { Content = c.Name, Padding = new Thickness(10, 2), Focusable = false };
            ToolTip.SetTip(b, c.Command);
            b.Click += async (_, _) => await RunUserCommandAsync(c);
            _userBar.Children.Add(b);
        }
        _userBarHost.IsVisible = _settings.ShowButtonBar && _settings.UserCommands.Count > 0;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        foreach (var group in new[] { Actions.File, Actions.Mark, Actions.Tools, Actions.View, Actions.Servers })
        {
            var top = new MenuItem { Header = group };
            foreach (var a in Actions.All.Where(a => a.Menu == group))
            {
                var item = new MenuItem { Header = a.Title };
                var gesture = Actions.GestureFor(a, _settings);
                if (gesture != null) item.InputGesture = gesture;
                var id = a.Id;
                item.Click += async (_, _) => await Run(id);
                top.Items.Add(item);
            }
            _menu.Items.Add(top);
        }
    }

    // --- téma, stav, aktivní panel --------------------------------------------------------------------

    public void ApplyTheme()
    {
        var app = Application.Current;
        if (app == null) return;
        app.RequestedThemeVariant = _settings.Theme switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        var dark = _settings.Theme == "dark" || (_settings.Theme == "system" && app.ActualThemeVariant == ThemeVariant.Dark);
        FileEntry.NormalBrush = dark ? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)) : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        FileEntry.DirBrush = dark ? new SolidColorBrush(Color.FromRgb(0x7F, 0xB5, 0xFF)) : new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0xB5));
        FileEntry.MarkBrush = dark ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) : new SolidColorBrush(Color.FromRgb(0xC6, 0x1A, 0x1A));
        FileEntry.SplitExtension = _settings.ShowExtColumn;
    }

    public void ApplySettings()
    {
        ApplyTheme();
        _leftPanel.ApplySettings(); _rightPanel.ApplySettings();
        foreach (var t in _core.Left.Tabs.Concat(_core.Right.Tabs)) t.RaiseStateChangedForUi();
        BuildMenu();
        RebuildUserBar();
    }

    private void SetActive(bool left)
    {
        _core.ActiveIsLeft = left;
        _leftPanel.SetActive(left);
        _rightPanel.SetActive(!left);
        if (_quickView) UpdateQuickViewHost();
        _core.SaveState();
    }

    private PanelControl ActivePanel => _core.ActiveIsLeft ? _leftPanel : _rightPanel;
    private void FocusActiveList() => Dispatcher.UIThread.Post(() => { if (!IsTextFocused()) ActivePanel.FocusList(); }, DispatcherPriority.Input);

    private bool IsTextFocused() => FocusManager?.GetFocusedElement() is TextBox;

    private void UpdateStatus()
    {
        _progress.IsVisible = _core.Progress.HasValue;
        if (_core.Progress.HasValue) _progress.Value = _core.Progress.Value;
        _notice.Text = _core.Notice ?? _core.ProgressText;
        _queueText.Text = _core.QueueCount > 0 ? $"ve frontě: {_core.QueueCount}" : "";
        _cancel.IsVisible = _core.CanCancel && _core.Progress.HasValue;
        _cancelAll.IsVisible = _cancel.IsVisible && _core.QueueCount > 0;
    }

    private void OnClosing()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { _settings.WindowWidth = Width; _settings.WindowHeight = Height; }
        _core.SaveState();
        _core.Dispose();
    }

    // --- rychlý náhled ----------------------------------------------------------------------------------

    private void ToggleQuickView()
    {
        _quickView = !_quickView;
        UpdateQuickViewHost();
    }

    private void UpdateQuickViewHost()
    {
        _panelsGrid.Children.Remove(_leftPanel); _panelsGrid.Children.Remove(_rightPanel);
        if (_quickViewControl != null) _panelsGrid.Children.Remove(_quickViewControl);
        _quickViewControl = null;
        Control left = _leftPanel, right = _rightPanel;
        if (_quickView)
        {
            _quickViewControl = new QuickViewControl(_core, _core.ActiveIsLeft ? _core.Left : _core.Right);
            if (_core.ActiveIsLeft) right = _quickViewControl; else left = _quickViewControl;
        }
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
        _panelsGrid.Children.Add(left); _panelsGrid.Children.Add(right);
    }

    // --- příkazový řádek ---------------------------------------------------------------------------------

    private void SetCommandVisible(bool visible)
    {
        _commandHost.IsVisible = visible;
        if (visible) _commandInput.Focus(); else FocusActiveList();
    }

    private void AppendOutput(string text) => Dispatcher.UIThread.Post(() =>
    {
        var t = (_commandOutput.Text ?? "") + text;
        if (t.Length > 200_000) t = t[^200_000..];
        _commandOutput.Text = t;
        _commandOutput.CaretIndex = t.Length;
    });

    private Task RunTypedCommandAsync()
    {
        var cmd = (_commandInput.Text ?? "").Trim();
        if (cmd.Length == 0) return Task.CompletedTask;
        if (_core.Active.IsRemote) { _commandOutput.Text = "Příkazy fungují jen v lokální složce.\n"; return Task.CompletedTask; }
        _commandInput.Text = "";
        StartCommand(cmd);
        return Task.CompletedTask;
    }

    private void StartCommand(string cmd)
    {
        var pane = _core.Active;
        _commandOutput.Text = "> " + cmd + "\n";
        _commandStop.IsVisible = true;
        _core.Runner.Run(cmd, pane.Path, AppendOutput, code => Dispatcher.UIThread.Post(() =>
        {
            _commandStop.IsVisible = false;
            if (code != 0) AppendOutput($"\n[kód {code}]\n");
            _ = pane.ReloadAsync(true);
        }));
    }

    private async Task RunUserCommandAsync(UserCommand cmd)
    {
        var text = await _core.RunUserCommandPrepareAsync(cmd);
        if (text == null) return;
        SetCommandVisible(true);
        StartCommand(text);
    }
}
