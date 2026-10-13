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
    private readonly TextBlock _summary = new() { Margin = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _typeBox = new()
    {
        ItemsSource = new[] { "Všechny soubory", "Jen obrázky", "Jen dokumenty", "Jen hudba", "Jen video", "Jen archivy" },
        SelectedIndex = 0, MinHeight = 22, Padding = new Thickness(6, 0), Focusable = false, FontSize = 11,
    };
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, Height = 3, IsVisible = false };

    private PaneState? _pane;
    private bool _updating;
    private Point? _dragStart;
    private bool _driveUpdating;

    public event Action? Activated;
    /// <summary>Provede akci podle identifikátoru (nastavuje hlavní okno); používá ji místní nabídka.</summary>
    public Func<string, Task>? RunAction { get; set; }
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
        var summaryGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_typeBox, 1);
        summaryGrid.Children.Add(_summary); summaryGrid.Children.Add(_typeBox);
        var summaryBar = new Border { Child = summaryGrid, Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)) };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(tabScroll, Dock.Top); DockPanel.SetDock(_filterBar, Dock.Top);
        DockPanel.SetDock(_loading, Dock.Top); DockPanel.SetDock(_header, Dock.Top); DockPanel.SetDock(summaryBar, Dock.Bottom);
        dock.Children.Add(top); dock.Children.Add(tabScroll); dock.Children.Add(_filterBar); dock.Children.Add(_loading);
        dock.Children.Add(_header); dock.Children.Add(summaryBar); dock.Children.Add(_list);
        _frame.Child = dock;
        Content = _frame;

        // události
        _typeBox.SelectionChanged += (_, _) =>
        {
            if (_updating || _pane == null) return;
            int i = _typeBox.SelectedIndex;
            _pane.SetTypeFilter(i <= 0 ? null : PaneState.TypeFilters[i - 1].Id);
            Activated?.Invoke();
            FocusList();
        };
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
        _list.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);
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
        if (LayoutKey() != _layoutKey) { _layoutKey = LayoutKey(); ApplySettings(); }
    }

    private void UpdateSummary()
    {
        _summary.Text = _pane?.Summary ?? "";
        int index = _pane?.TypeFilter == null ? 0 : Math.Max(0, Array.FindIndex(PaneState.TypeFilters, t => t.Id == _pane.TypeFilter) + 1);
        if (_typeBox.SelectedIndex != index) { _updating = true; _typeBox.SelectedIndex = index; _updating = false; }
    }

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
            var title = new TextBlock { Text = tab.TabTitle, MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }.Raw();
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
            border.PointerPressed += (_, e) =>
            {
                Activated?.Invoke(); _group.Select(index);
                if (e.GetCurrentPoint(border).Properties.IsRightButtonPressed) { e.Handled = true; ShowTabMenu(index, border); }
            };
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

    private ColumnLayout Layout => _pane?.Columns ?? new ColumnLayout();
    private string _layoutKey = "";

    private string Columns()
    {
        var s = _core.Settings;
        return Layout.Definition(s.ShowExtColumn, s.ShowAttrColumn);
    }

    private string LayoutKey()
    {
        var s = _core.Settings;
        return Columns() + "|" + string.Join(",", Layout.Visible(s.ShowExtColumn, s.ShowAttrColumn));
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
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1) }.Raw();
            tb.Bind(TextBlock.TextProperty, new Binding(prop));
            tb.Bind(TextBlock.ForegroundProperty, new Binding(nameof(FileEntry.Foreground)));
            if (right) { tb.HorizontalAlignment = HorizontalAlignment.Right; tb.Margin = new Thickness(0, 1, 6, 1); }
            if (trim) tb.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(tb, c++);
            g.Children.Add(tb);
        }
        if (s.ShowThumbs && Thumbnails.Available) AddIconWithThumbnail(g, c++);
        else Add(nameof(FileEntry.Icon));
        foreach (var col in Layout.Visible(s.ShowExtColumn, s.ShowAttrColumn))
        {
            switch (col)
            {
                case "name": Add(nameof(FileEntry.DisplayName), trim: true); break;
                case "ext": Add(nameof(FileEntry.Ext)); break;
                case "attr": Add(nameof(FileEntry.AttrText)); break;
                case "size": Add(nameof(FileEntry.SizeText), right: true); break;
                case "date": Add(nameof(FileEntry.DateText), right: true); break;
            }
        }
        return g;
    }

    /// <summary>Buňka s ikonou, kterou po načtení miniatury (na pozadí) nahradí obrázek.</summary>
    private static void AddIconWithThumbnail(Grid row, int column)
    {
        var icon = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1) }.Raw();
        icon.Bind(TextBlock.TextProperty, new Binding(nameof(FileEntry.Icon)));
        icon.Bind(TextBlock.ForegroundProperty, new Binding(nameof(FileEntry.Foreground)));
        var image = new Image { Width = 20, Height = 20, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        var cell = new Grid();
        cell.Children.Add(icon); cell.Children.Add(image);
        Grid.SetColumn(cell, column);
        row.Children.Add(cell);
        cell.DataContextChanged += async (_, _) =>
        {
            image.IsVisible = false; image.Source = null; icon.IsVisible = true;
            if (cell.DataContext is not FileEntry { IsParent: false, IsDirectory: false, IsRemote: false } e) return;
            var path = e.FullPath;
            var bmp = await Thumbnails.GetAsync(path, e.Size, e.Modified);
            if (bmp != null && cell.DataContext is FileEntry cur && cur.FullPath == path)
            {
                image.Source = bmp; image.IsVisible = true; icon.IsVisible = false;
            }
        };
    }

    private void BuildHeader()
    {
        var s = _core.Settings;
        _header.Children.Clear();
        _header.ColumnDefinitions = new ColumnDefinitions(Columns());
        _header.Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));
        _header.Margin = new Thickness(4, 0, 4, 0);
        int c = 1;
        foreach (var col in Layout.Visible(s.ShowExtColumn, s.ShowAttrColumn))
        {
            var (title, key, right) = col switch
            {
                "name" => ("Název", (SortKey?)SortKey.Name, false),
                "ext" => ("Přípona", SortKey.Ext, false),
                "attr" => ("Atr", null, false),
                "size" => ("Velikost", SortKey.Size, true),
                _ => ("Změněno", SortKey.Date, true),
            };
            var arrow = key != null && _pane != null && _pane.SortKey == key ? (_pane.Ascending ? " ▲" : " ▼") : "";
            var tb = new TextBlock { Text = Tr.T(title) + arrow, FontWeight = FontWeight.SemiBold, FontSize = Math.Max(10, s.FontSize - 1), Margin = new Thickness(0, 3, right ? 6 : 0, 3), Cursor = new Cursor(StandardCursorType.Hand) };
            if (right) tb.HorizontalAlignment = HorizontalAlignment.Right;
            if (key != null) tb.PointerPressed += async (_, e) =>
            {
                if (!e.GetCurrentPoint(tb).Properties.IsLeftButtonPressed) return;
                Activated?.Invoke(); await Pane.SetSortAsync(key.Value); BuildHeader();
            };
            var captured = col;
            tb.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Right) ShowHeaderMenu(captured, tb); };
            Grid.SetColumn(tb, c);
            _header.Children.Add(tb);
            if (col != "name") _header.Children.Add(MakeResizeHandle(col, c, right));
            c++;
        }
    }

    /// <summary>Úchyt na hraně sloupce: tažením se mění šířka a po pustění se uloží pro tuto složku.</summary>
    private Control MakeResizeHandle(string col, int gridColumn, bool onLeftEdge)
    {
        var handle = new Border
        {
            Width = 6, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast),
            HorizontalAlignment = onLeftEdge ? HorizontalAlignment.Left : HorizontalAlignment.Right,
        };
        Grid.SetColumn(handle, gridColumn);
        double startX = 0, startWidth = 0; bool dragging = false;
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            Activated?.Invoke();
            dragging = true; startX = e.GetPosition(_header).X; startWidth = Layout.WidthOf(col);
            e.Pointer.Capture(handle); e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var dx = e.GetPosition(_header).X - startX;
            var width = Math.Clamp(startWidth + (onLeftEdge ? -dx : dx), ColumnLayout.MinWidth, ColumnLayout.MaxWidth);
            var cols = _header.ColumnDefinitions;
            if (gridColumn < cols.Count) cols[gridColumn].Width = new GridLength(width);
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false; e.Pointer.Capture(null);
            var dx = e.GetPosition(_header).X - startX;
            var layout = Layout.Clone();
            layout.Resize(col, startWidth + (onLeftEdge ? -dx : dx));
            Pane.SetColumns(layout);
        };
        return handle;
    }

    private void ShowHeaderMenu(string col, Control anchor)
    {
        var s = _core.Settings;
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = Tr.T(header), IsEnabled = enabled };
            mi.Click += (_, _) => { Activated?.Invoke(); action(); };
            return mi;
        }
        var visible = Layout.Visible(s.ShowExtColumn, s.ShowAttrColumn);
        var idx = visible.IndexOf(col);
        void Move(int delta) { var l = Layout.Clone(); if (l.Move(col, delta, s.ShowExtColumn, s.ShowAttrColumn)) Pane.SetColumns(l); }
        menu.Items.Add(Item("Posunout sloupec doleva", () => Move(-1), idx > 0));
        menu.Items.Add(Item("Posunout sloupec doprava", () => Move(1), idx >= 0 && idx < visible.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Použít toto rozložení pro všechny složky", () => Pane.SetDefaultColumns(Layout.Clone())));
        menu.Items.Add(Item("Výchozí rozložení této složky", () => Pane.ResetColumns(), Pane.HasCustomColumns));
        menu.Open(anchor);
    }

    // --- aktivní panel, filtr ----------------------------------------------------------------------

    public void SetActive(bool active)
    {
        _frame.BorderBrush = active ? FileEntry.AccentBrush : Brushes.Transparent;
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
        if (e.GetCurrentPoint(_list).Properties.IsRightButtonPressed)
        {
            // nabídka se ukáže až po pustění tlačítka; když se mezitím myš posune, je z toho tažení pravým tlačítkem
            e.Handled = true;
            _rightStart = e.GetPosition(_list);
            _rightUnder = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as FileEntry;
            return;
        }
        if (e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() != null)
            _dragStart = e.GetPosition(_list);
    }

    private void ShowTabMenu(int index, Control anchor)
    {
        var menu = new ContextMenu();
        var add = new MenuItem { Header = Tr.T("Nová záložka") };
        add.Click += (_, _) => _group.NewTab();
        var close = new MenuItem { Header = Tr.T("Zavřít záložku"), IsEnabled = _group.Tabs.Count > 1 };
        close.Click += (_, _) => _group.Close(index);
        var others = new MenuItem { Header = Tr.T("Zavřít ostatní záložky"), IsEnabled = _group.Tabs.Count > 1 };
        others.Click += (_, _) => { for (var i = _group.Tabs.Count - 1; i >= 0; i--) if (i != index) _group.Close(i); };
        menu.Items.Add(add); menu.Items.Add(new Separator()); menu.Items.Add(close); menu.Items.Add(others);
        menu.Open(anchor);
    }

    /// <summary>Místní nabídka (pravé tlačítko): nad položkou funkce pro soubor, složku nebo označené, jinde pro celý panel.</summary>
    private void ShowContextMenu(FileEntry? under)
    {
        if (_pane == null || RunAction == null) return;
        var underIsMarked = under != null && under.IsMarked;
        if (under != null && !under.IsParent && !underIsMarked)
        {
            var idx = _pane.Items.IndexOf(under);
            if (idx >= 0) { _pane.SetCursorSilently(idx); _list.SelectedIndex = idx; }
        }
        var kind = ContextMenus.KindOf(_pane.Marked.Count(), underIsMarked, under);
        var settings = _core.Settings;
        var menu = new ContextMenu();
        foreach (var id in ContextMenus.For(kind, _pane.IsRemote, under != null && ContextMenus.IsRunnable(under.Name), _pane.Connection is BudisCommander.Remote.RcloneSession))
        {
            if (id == ContextMenus.Separator) { menu.Items.Add(new Separator()); continue; }
            var def = Actions.All.FirstOrDefault(a => a.Id == id);
            if (def == null) continue;
            var item = new MenuItem { Header = Tr.T(def.Title) };
            if (Actions.GestureFor(def, settings) is { } g) item.InputGesture = g;
            var captured = id;
            item.Click += async (_, _) => { Activated?.Invoke(); await RunAction(captured); };
            menu.Items.Add(item);
        }
        menu.Open(_list);
    }

    private Point? _rightStart;
    private FileEntry? _rightUnder;
    /// <summary>Právě probíhá tažení pravým tlačítkem (po pustění se ukáže nabídka Kopírovat sem / Přesunout sem).</summary>
    private static bool s_rightDrag;

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStart = null;
        if (e.InitialPressMouseButton == MouseButton.Right && _rightStart != null)
        {
            var under = _rightUnder;
            _rightStart = null; _rightUnder = null;
            ShowContextMenu(under);
            e.Handled = true;
        }
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(_list).Properties;
        var right = props.IsRightButtonPressed && _rightStart != null;
        if (!right && (_dragStart == null || !props.IsLeftButtonPressed)) return;
        var p = e.GetPosition(_list);
        var origin = right ? _rightStart!.Value : _dragStart!.Value;
        if (Math.Abs(p.X - origin.X) < 8 && Math.Abs(p.Y - origin.Y) < 8) return;
        _dragStart = null;
        var under = right ? _rightUnder : (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as FileEntry;
        if (right) { _rightStart = null; _rightUnder = null; }
        if (_pane == null || _pane.IsRemote) return;
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
        s_rightDrag = right;
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move); }
        finally { s_rightDrag = false; }
    }

    /// <summary>Nabídka po pustění souborů tažených pravým tlačítkem: 0 kopírovat, 1 přesunout, null zrušit.</summary>
    private Task<int?> AskDropActionAsync()
    {
        var tcs = new TaskCompletionSource<int?>();
        var menu = new ContextMenu();
        MenuItem Item(string header, int result)
        {
            var mi = new MenuItem { Header = Tr.T(header) };
            mi.Click += (_, _) => tcs.TrySetResult(result);
            return mi;
        }
        menu.Items.Add(Item("Kopírovat sem", 0));
        menu.Items.Add(Item("Přesunout sem", 1));
        menu.Items.Add(new Separator());
        var cancel = new MenuItem { Header = Tr.T("Zrušit") };
        cancel.Click += (_, _) => tcs.TrySetResult(null);
        menu.Items.Add(cancel);
        menu.Closed += (_, _) => Dispatcher.UIThread.Post(() => tcs.TrySetResult(null), DispatcherPriority.Background);
        menu.Open(this);
        return tcs.Task;
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
        bool? move = null;
        if (s_rightDrag)
        {
            var choice = await AskDropActionAsync();
            if (choice == null) return;
            move = choice == 1;
        }
        await _core.DropFilesAsync(paths, Pane, folder, move);
    }
}
