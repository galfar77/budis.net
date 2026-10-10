using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BudisCommander.Core;

namespace BudisCommander.Ui;

/// <summary>Jeden panel: záložky, cesta, seznam souborů a souhrn.</summary>
public sealed class PanelControl : UserControl
{
    private readonly AppCore _core;
    private readonly TabGroup _group;
    private readonly bool _isLeft;

    private readonly Border _frame = new() { BorderThickness = new Thickness(2) };
    private readonly ComboBox _drives = new() { MinWidth = 120 };
    private readonly TextBox _path = new() { MinHeight = 28 };
    private readonly Button _disconnect = new() { Content = "⏏", IsVisible = false };
    private readonly Button _parent = new() { Content = "↰" };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Border _filterBar = new() { IsVisible = false };
    private readonly TextBox _filterBox = new() { Watermark = "Filtr názvů (Esc zruší)" };
    private readonly Grid _header = new();
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _summary = new() { Margin = new Thickness(8, 2) };
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, Height = 3, IsVisible = false };

    private PaneState? _pane;
    private bool _updating;
    private Point? _dragStart;
    private bool _driveUpdating;

    public event Action? Activated;
    public PaneState Pane => _group.Current;
    public ListBox List => _list;

    public PanelControl(AppCore core, TabGroup group, bool isLeft)
    {
        _core = core; _group = group; _isLeft = isLeft;
        BuildLayout();
        _group.Changed += () => { RebuildTabs(); Bind(_group.Current); };
        Bind(_group.Current);
        RebuildTabs();
        ApplySettings();
    }

    // --- sestavení rozhraní -----------------------------------------------------------

    private void BuildLayout()
    {
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(4, 4, 4, 2) };
        Grid.SetColumn(_drives, 0); Grid.SetColumn(_path, 1); Grid.SetColumn(_parent, 2); Grid.SetColumn(_disconnect, 3);
        _path.Margin = new Thickness(4, 0);
        top.Children.Add(_drives); top.Children.Add(_path); top.Children.Add(_parent); top.Children.Add(_disconnect);
        ToolTip.SetTip(_disconnect, "Odpojit od serveru");
        ToolTip.SetTip(_parent, "O úroveň výš");

        var tabScroll = new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(4, 0) };
        _filterBar.Child = new Grid { Margin = new Thickness(4, 2), Children = { _filterBox } };
        _filterBar.Background = new SolidColorBrush(Color.FromArgb(50, 255, 200, 0));

        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(4, 0)),
                new Setter(ListBoxItem.MinHeightProperty, 0.0),
                new Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(0)),
            }
        });
        _list.Focusable = false;
        DragDrop.SetAllowDrop(_list, true);

        var dock = new DockPanel();
        var summaryBar = new Border { Child = _summary, Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)) };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(tabScroll, Dock.Top); DockPanel.SetDock(_filterBar, Dock.Top);
        DockPanel.SetDock(_loading, Dock.Top); DockPanel.SetDock(_header, Dock.Top); DockPanel.SetDock(summaryBar, Dock.Bottom);
        dock.Children.Add(top); dock.Children.Add(tabScroll); dock.Children.Add(_filterBar); dock.Children.Add(_loading);
        dock.Children.Add(_header); dock.Children.Add(summaryBar); dock.Children.Add(_list);
        _frame.Child = dock;
        Content = _frame;

        // události
        _parent.Click += async (_, _) => { Activated?.Invoke(); await Pane.GoUpAsync(); };
        _disconnect.Click += async (_, _) => { Activated?.Invoke(); await Pane.DisconnectAsync(); };
        _path.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await GoToTypedPathAsync(); FocusList(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _path.Text = Pane.Title; FocusList(); }
        };
        _filterBox.TextChanged += (_, _) => { if (!_updating) Pane.SetFilter(_filterBox.Text ?? ""); };
        _filterBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; HideFilter(); }
            else if (e.Key is Key.Enter or Key.Down) { e.Handled = true; FocusList(); }
        };
        _drives.SelectionChanged += async (_, _) =>
        {
            if (_driveUpdating || _drives.SelectedItem is not string s) return;
            var root = s.Split(' ')[0];
            if (!string.IsNullOrEmpty(root) && !string.Equals(root, Path.GetPathRoot(Pane.Path), StringComparison.OrdinalIgnoreCase))
            {
                Activated?.Invoke();
                await Pane.NavigateAsync(root);
            }
        };
        _drives.DropDownOpened += (_, _) => RefreshDrives();

        _list.SelectionChanged += (_, e) =>
        {
            if (_updating || _pane == null) return;
            if (_list.SelectedIndex >= 0) { _pane.SetCursorSilently(_list.SelectedIndex); Activated?.Invoke(); _pane.RaiseSummaryOnly(); UpdateSummary(); }
        };
        _list.DoubleTapped += async (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>() != null) { Activated?.Invoke(); await Enter(); }
        };
        PointerPressed += (_, _) => Activated?.Invoke();
        _list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void FocusList() => _list.Focus();

    // --- vazba na stav panelu ------------------------------------------------------------

    private void Bind(PaneState pane)
    {
        if (_pane != null)
        {
            _pane.ListChanged -= OnListChanged; _pane.CursorChanged -= OnCursorChanged; _pane.StateChanged -= OnStateChanged;
        }
        _pane = pane;
        pane.ListChanged += OnListChanged; pane.CursorChanged += OnCursorChanged; pane.StateChanged += OnStateChanged;
        OnListChanged(); OnCursorChanged(); OnStateChanged();
    }

    private void OnListChanged()
    {
        if (_pane == null) return;
        _updating = true;
        _list.ItemsSource = _pane.Items;
        _list.SelectedIndex = _pane.Items.Count > 0 ? _pane.Cursor : -1;
        _updating = false;
        ScrollToCursor();
    }

    private void OnCursorChanged()
    {
        if (_pane == null) return;
        _updating = true;
        if (_list.SelectedIndex != _pane.Cursor && _pane.Items.Count > 0) _list.SelectedIndex = _pane.Cursor;
        _updating = false;
        ScrollToCursor();
    }

    private void ScrollToCursor()
    {
        if (_pane == null || _pane.Items.Count == 0) return;
        var index = _pane.Cursor;
        Dispatcher.UIThread.Post(() =>
        {
            if (index >= 0 && index < _pane.Items.Count) _list.ScrollIntoView(index);
        }, DispatcherPriority.Background);
    }

    private void OnStateChanged()
    {
        if (_pane == null) return;
        _path.Text = _pane.Title;
        _disconnect.IsVisible = _pane.IsRemote;
        _loading.IsVisible = _pane.IsLoading;
        _parent.IsEnabled = !_pane.IsRemote || _pane.RemotePath != "/";
        if (_pane.Filter.Length > 0 && !_filterBar.IsVisible) _filterBar.IsVisible = true;
        if (_pane.Filter.Length == 0 && _filterBox.Text?.Length > 0) { _updating = true; _filterBox.Text = ""; _updating = false; }
        UpdateSummary();
        UpdateDriveSelection();
        RebuildTabsTitles();
    }

    private void UpdateSummary() => _summary.Text = _pane?.Summary ?? "";

    // --- jednotky ---------------------------------------------------------------------------

    private void RefreshDrives()
    {
        _driveUpdating = true;
        var items = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var label = string.IsNullOrEmpty(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel;
                items.Add($"{d.Name} ({label})");
            }
            catch { }
        }
        var selected = _drives.SelectedItem as string;
        _drives.ItemsSource = items;
        _drives.SelectedItem = selected != null && items.Contains(selected) ? selected : null;
        _driveUpdating = false;
        UpdateDriveSelection();
    }

    private void UpdateDriveSelection()
    {
        if (_pane == null) return;
        if (_drives.ItemsSource == null) RefreshDrives();
        _driveUpdating = true;
        var root = Path.GetPathRoot(_pane.Path) ?? "";
        var match = (_drives.ItemsSource as List<string>)?.FirstOrDefault(s => s.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        _drives.SelectedItem = _pane.IsRemote ? null : match;
        _driveUpdating = false;
    }

    private async Task GoToTypedPathAsync()
    {
        var text = (_path.Text ?? "").Trim().Trim('"');
        if (text.Length == 0) return;
        Activated?.Invoke();
        text = Environment.ExpandEnvironmentVariables(text);
        if (Directory.Exists(text)) await Pane.NavigateAsync(Path.GetFullPath(text));
        else if (File.Exists(text)) await Pane.NavigateAsync(Path.GetDirectoryName(Path.GetFullPath(text))!);
        else await _core.Ui.ShowErrorAsync($"Složka „{text}“ neexistuje.");
        _path.Text = Pane.Title;
    }

    // --- záložky ------------------------------------------------------------------------------

    private void RebuildTabs()
    {
        _tabs.Children.Clear();
        for (int i = 0; i < _group.Tabs.Count; i++)
        {
            int index = i;
            var tab = _group.Tabs[i];
            var title = new TextBlock { Text = tab.TabTitle, MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(title);
            if (_group.Tabs.Count > 1)
            {
                var close = new TextBlock { Text = "✕", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
                close.PointerPressed += (_, e) => { _group.Close(index); e.Handled = true; };
                row.Children.Add(close);
            }
            var border = new Border
            {
                Padding = new Thickness(8, 3),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(i == _group.Selected ? (byte)90 : (byte)30, 100, 140, 255)),
                Child = row,
            };
            border.PointerPressed += (_, e) => { Activated?.Invoke(); _group.Select(index); };
            _tabs.Children.Add(border);
        }
        var plus = new Button { Content = "+", Padding = new Thickness(8, 1) };
        ToolTip.SetTip(plus, "Nová záložka (Ctrl+T)");
        plus.Click += (_, _) => { Activated?.Invoke(); _group.NewTab(); };
        _tabs.Children.Add(plus);
    }

    private void RebuildTabsTitles()
    {
        for (int i = 0; i < _group.Tabs.Count && i < _tabs.Children.Count; i++)
        {
            if (_tabs.Children[i] is Border { Child: StackPanel { Children: { Count: > 0 } kids } } && kids[0] is TextBlock tb)
            {
                var title = _group.Tabs[i].TabTitle;
                if (tb.Text != title) tb.Text = title;
            }
        }
    }

    // --- sloupce a vzhled ----------------------------------------------------------------------

    private string Columns()
    {
        var s = _core.Settings;
        return "26,*," + (s.ShowExtColumn ? "60," : "") + (s.ShowAttrColumn ? "48," : "") + "92,140";
    }

    public void ApplySettings()
    {
        var s = _core.Settings;
        FontSize = s.FontSize;
        _list.ItemTemplate = new FuncDataTemplate<FileEntry>((_, _) => BuildRow(), true);
        BuildHeader();
        _list.InvalidateMeasure();
        // seznam se znovu vykreslí kvůli nové šabloně
        var items = _pane?.Items;
        _list.ItemsSource = null;
        _list.ItemsSource = items;
        if (_pane != null) { _updating = true; _list.SelectedIndex = _pane.Items.Count > 0 ? _pane.Cursor : -1; _updating = false; }
    }

    private Control BuildRow()
    {
        var s = _core.Settings;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns()), Background = Brushes.Transparent, MinHeight = s.FontSize + 8 };
        int c = 0;
        void Add(string prop, bool right = false, bool trim = false)
        {
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1) };
            tb.Bind(TextBlock.TextProperty, new Binding(prop));
            tb.Bind(TextBlock.ForegroundProperty, new Binding(nameof(FileEntry.Foreground)));
            if (right) { tb.HorizontalAlignment = HorizontalAlignment.Right; tb.Margin = new Thickness(0, 1, 6, 1); }
            if (trim) tb.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(tb, c++);
            g.Children.Add(tb);
        }
        Add(nameof(FileEntry.Icon));
        Add(nameof(FileEntry.DisplayName), trim: true);
        if (s.ShowExtColumn) Add(nameof(FileEntry.Ext));
        if (s.ShowAttrColumn) Add(nameof(FileEntry.AttrText));
        Add(nameof(FileEntry.SizeText), right: true);
        Add(nameof(FileEntry.DateText), right: true);
        return g;
    }

    private void BuildHeader()
    {
        var s = _core.Settings;
        _header.Children.Clear();
        _header.ColumnDefinitions = new ColumnDefinitions(Columns());
        _header.Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));
        _header.Margin = new Thickness(4, 0, 4, 0);
        int c = 1;
        TextBlock H(string text, SortKey? key, bool right = false)
        {
            var arrow = key != null && _pane != null && _pane.SortKey == key ? (_pane.Ascending ? " ▲" : " ▼") : "";
            var tb = new TextBlock { Text = text + arrow, FontWeight = FontWeight.SemiBold, FontSize = Math.Max(10, s.FontSize - 1), Margin = new Thickness(0, 3, right ? 6 : 0, 3), Cursor = new Cursor(StandardCursorType.Hand) };
            if (right) tb.HorizontalAlignment = HorizontalAlignment.Right;
            if (key != null) tb.PointerPressed += async (_, _) => { Activated?.Invoke(); await Pane.SetSortAsync(key.Value); BuildHeader(); };
            Grid.SetColumn(tb, c++);
            _header.Children.Add(tb);
            return tb;
        }
        H("Název", SortKey.Name);
        if (s.ShowExtColumn) H("Přípona", SortKey.Ext);
        if (s.ShowAttrColumn) H("Atr", null);
        H("Velikost", SortKey.Size, true);
        H("Změněno", SortKey.Date, true);
    }

    // --- aktivní panel, filtr ----------------------------------------------------------------------

    public void SetActive(bool active)
    {
        _frame.BorderBrush = active ? new SolidColorBrush(Color.FromRgb(0x2B, 0x7C, 0xD3)) : Brushes.Transparent;
    }

    public void ShowFilter()
    {
        _filterBar.IsVisible = true;
        _filterBox.Focus();
        _filterBox.SelectAll();
    }

    public void HideFilter()
    {
        _updating = true; _filterBox.Text = ""; _updating = false;
        _filterBar.IsVisible = false;
        Pane.SetFilter("");
        FocusList();
    }

    public void FocusPathBox() { _path.Focus(); _path.SelectAll(); }

    public Task Enter() => Pane.EnterAsync(_core.OpenRemoteFileAsync);

    // --- myš: tažení a pouštění ------------------------------------------------------------------------

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Activated?.Invoke();
        if (e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() != null)
            _dragStart = e.GetPosition(_list);
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart == null || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(_list);
        if (Math.Abs(p.X - _dragStart.Value.X) < 8 && Math.Abs(p.Y - _dragStart.Value.Y) < 8) return;
        _dragStart = null;
        if (_pane == null || _pane.IsRemote) return;
        var under = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as FileEntry;
        var entries = under != null && under.IsMarked ? _pane.Targets() : under != null && !under.IsParent ? new List<FileEntry> { under } : new List<FileEntry>();
        if (entries.Count == 0) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var data = new DataObject();
        var storage = new List<IStorageItem>();
        foreach (var en in entries)
        {
            IStorageItem? item = en.IsDirectory
                ? await top.StorageProvider.TryGetFolderFromPathAsync(en.FullPath)
                : await top.StorageProvider.TryGetFileFromPathAsync(en.FullPath);
            if (item != null) storage.Add(item);
        }
        if (storage.Count == 0) return;
        data.Set(DataFormats.Files, storage);
        _core.DragSource = _pane;
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files == null) return;
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths.Count == 0) return;
        Activated?.Invoke();
        string? folder = null;
        if (!Pane.IsRemote && !Pane.IsArchive && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is FileEntry { IsDirectory: true } target)
            folder = target.IsParent ? Path.GetDirectoryName(Pane.Path.TrimEnd('\\', '/')) ?? target.FullPath : target.FullPath;
        e.Handled = true;
        await _core.DropFilesAsync(paths, Pane, folder);
    }
}
