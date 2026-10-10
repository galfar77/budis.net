using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using BudisCommander.Core;

namespace BudisCommander.Ui;

internal static class WindowExt
{
    public static void CloseOnEscape(this Window w) => w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; w.Close(); } };
}

/// <summary>Prohlížeč (F3): text, hex u binárních souborů, obrázek.</summary>
public sealed class ViewerWindow : Window
{
    public ViewerWindow(ViewerData data)
    {
        Title = data.Title + " – Budis Commander";
        Width = 940; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right };
        if (data.OpenPath != null) top.Children.Add(UiKit.Button("Otevřít v aplikaci", () => PaneState.OpenWithShell(data.OpenPath)));
        top.Children.Add(UiKit.Button("Zavřít", Close, true));

        Control body;
        if (data.Kind == ViewerKind.Image && data.FilePath != null)
        {
            try
            {
                var bmp = new Bitmap(data.FilePath);
                body = new ScrollViewer { Content = new Image { Source = bmp, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            }
            catch (Exception e) { body = UiKit.Label("Obrázek nelze zobrazit: " + e.Message); }
        }
        else
        {
            var box = UiKit.Mono(data.Text ?? "");
            box.FontSize = 13;
            body = box;
        }
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top); dock.Children.Add(body);
        Content = dock;
        this.CloseOnEscape();
    }
}

/// <summary>Kontrolní součty MD5, SHA-1, SHA-256 s ověřením a uložením .sha256.</summary>
public sealed class ChecksumWindow : Window
{
    private readonly Dictionary<string, LocalFs.Hashes> _results = new();
    private readonly List<FileEntry> _files;
    private readonly TextBox _expected = new() { Watermark = "Očekávaný součet (vložte pro ověření)" };
    private readonly TextBlock _verdict = new() { FontWeight = FontWeight.SemiBold };
    private readonly CancellationTokenSource _cts = new();

    public ChecksumWindow(List<FileEntry> files)
    {
        _files = files;
        Title = "Kontrolní součty";
        Width = 760; SizeToContent = SizeToContent.Height; MaxHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = new StackPanel { Spacing = 12 };
        var cells = new Dictionary<string, StackPanel>();
        foreach (var f in files)
        {
            var box = new StackPanel { Spacing = 2 };
            box.Children.Add(UiKit.Label(f.Name, 13, true));
            box.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4 });
            cells[f.FullPath] = box;
            list.Children.Add(box);
        }
        _expected.TextChanged += (_, _) => UpdateVerdict();
        var copy = UiKit.Button("Kopírovat vše", async () =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in _files) if (_results.TryGetValue(f.FullPath, out var h)) sb.AppendLine($"{f.Name}\nMD5     {h.Md5}\nSHA-1   {h.Sha1}\nSHA-256 {h.Sha256}\n");
            if (Clipboard != null) await Clipboard.SetTextAsync(sb.ToString());
        });
        var save = UiKit.Button("Uložit .sha256 vedle souborů", () =>
        {
            foreach (var f in _files)
                if (_results.TryGetValue(f.FullPath, out var h)) File.WriteAllText(f.FullPath + ".sha256", $"{h.Sha256}  {f.Name}\n");
        });
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(10, UiKit.Label("Kontrolní součty", 15, true), UiKit.Scroll(list, 300),
                _expected, _verdict, UiKit.HStack(8, copy, save, new Border { Width = 40 }, UiKit.Button("Zavřít", Close, true))),
        };
        Closing += (_, _) => _cts.Cancel();
        this.CloseOnEscape();

        Opened += async (_, _) =>
        {
            foreach (var f in _files)
            {
                try
                {
                    var h = await Task.Run(() => LocalFs.ComputeHashes(f.FullPath, _cts.Token));
                    _results[f.FullPath] = h;
                    var box = cells[f.FullPath];
                    box.Children.RemoveAt(1);
                    box.Children.Add(Row("MD5", h.Md5)); box.Children.Add(Row("SHA-1", h.Sha1)); box.Children.Add(Row("SHA-256", h.Sha256));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    var box = cells[f.FullPath];
                    box.Children.RemoveAt(1);
                    box.Children.Add(UiKit.Label("Soubor nelze přečíst: " + e.Message));
                }
            }
            UpdateVerdict();
        };
    }

    private static Control Row(string label, string value)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*") };
        var l = new TextBlock { Text = label, Opacity = 0.7, FontFamily = new FontFamily("Consolas, monospace") };
        var v = new SelectableTextBlock { Text = value, FontFamily = new FontFamily("Consolas, monospace"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        g.Children.Add(l); g.Children.Add(v);
        return g;
    }

    private void UpdateVerdict()
    {
        var e = (_expected.Text ?? "").Trim().ToLowerInvariant();
        if (e.Length == 0) { _verdict.Text = ""; return; }
        bool match = _results.Values.Any(h => h.Md5 == e || h.Sha1 == e || h.Sha256 == e);
        _verdict.Text = match ? "✔ Shoduje se" : "✘ Neshoduje se";
        _verdict.Foreground = match ? Brushes.Green : Brushes.Red;
    }
}

/// <summary>Porovnání dvou souborů řádek po řádku.</summary>
public sealed class DiffWindow : Window
{
    private readonly string _a, _b;
    private readonly ContentControl _body = new();
    private readonly CheckBox _onlyChanges = new() { Content = "Jen rozdíly" };
    private List<DiffRow> _rows = new();
    private readonly ListBox _list = new();

    public DiffWindow(string a, string b)
    {
        _a = a; _b = b;
        Title = "Porovnání souborů";
        Width = 1000; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto,Auto"), Margin = new Thickness(8) };
        var la = UiKit.Label(Path.GetFileName(a), 12, true); var lb = UiKit.Label(Path.GetFileName(b), 12, true);
        Grid.SetColumn(lb, 1); Grid.SetColumn(_onlyChanges, 2);
        var close = UiKit.Button("Zavřít", Close, true); Grid.SetColumn(close, 3);
        top.Children.Add(la); top.Children.Add(lb); top.Children.Add(_onlyChanges); top.Children.Add(close);
        _onlyChanges.IsCheckedChanged += (_, _) => Fill();
        _list.ItemTemplate = new FuncDataTemplate<DiffRow>((r, _) => Cell(r), true);
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MinHeightProperty, 0.0) } });
        _body.Content = new ProgressBar { IsIndeterminate = true, Width = 200, Margin = new Thickness(40) };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top); dock.Children.Add(_body);
        Content = dock;
        this.CloseOnEscape();
        Opened += async (_, _) =>
        {
            var result = await Task.Run(() => DiffEngine.Compute(a, b));
            switch (result)
            {
                case DiffResult.Binary bin: _body.Content = UiKit.Label(bin.Identical ? "Binární soubory jsou shodné." : "Binární soubory se liší."); break;
                case DiffResult.TooLarge big:
                    _body.Content = UiKit.Label(big.Identical ? "Soubory jsou příliš velké pro porovnání po řádcích, ale jsou shodné."
                        : "Soubory jsou příliš velké pro porovnání po řádcích (nad 3 MB nebo 8000 řádků) a liší se."); break;
                case DiffResult.Rows rows:
                    _rows = rows.Lines;
                    var summary = UiKit.Label(rows.Changes == 0 ? "Soubory jsou shodné." : $"Rozdílných řádků: {rows.Changes}", 12);
                    summary.Margin = new Thickness(8, 2);
                    var panel = new DockPanel();
                    DockPanel.SetDock(summary, Dock.Top);
                    panel.Children.Add(summary); panel.Children.Add(_list);
                    _body.Content = panel;
                    Fill();
                    break;
            }
        };
    }

    private void Fill() => _list.ItemsSource = _onlyChanges.IsChecked == true ? _rows.Where(r => r.Kind != DiffKind.Same).ToList() : _rows;

    private static Control Cell(DiffRow r)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Control Half(int? no, string? text, bool tint, Color color)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*") };
            var n = new TextBlock { Text = no?.ToString() ?? "", Opacity = 0.5, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0), FontFamily = new FontFamily("Consolas, monospace"), FontSize = 12 };
            var t = new TextBlock { Text = text ?? "", FontFamily = new FontFamily("Consolas, monospace"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, 1);
            row.Children.Add(n); row.Children.Add(t);
            return new Border { Child = row, Background = tint ? new SolidColorBrush(color) : Brushes.Transparent };
        }
        var left = Half(r.LeftNo, r.Left, r.Kind == DiffKind.Removed, Color.FromArgb(70, 230, 70, 70));
        var right = Half(r.RightNo, r.Right, r.Kind == DiffKind.Added, Color.FromArgb(70, 70, 200, 90));
        Grid.SetColumn(right, 1);
        g.Children.Add(left); g.Children.Add(right);
        return g;
    }
}

/// <summary>Atributy a časy souborů.</summary>
public sealed class AttributesWindow : Window
{
    public AttributesWindow(AppCore core, List<FileEntry> items)
    {
        Title = $"Atributy a časy ({items.Count} položek)";
        Width = 440; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var first = items[0];
        var setDate = new CheckBox { Content = "Nastavit datum a čas změny" };
        var date = new DatePicker { SelectedDate = first.Modified ?? DateTime.Now };
        var time = new TimePicker { SelectedTime = (first.Modified ?? DateTime.Now).TimeOfDay, ClockIdentifier = "24HourClock" };
        var now = UiKit.Button("Teď", () => { date.SelectedDate = DateTime.Now; time.SelectedTime = DateTime.Now.TimeOfDay; setDate.IsChecked = true; });
        var ro = new CheckBox { Content = "Jen pro čtení", IsThreeState = true, IsChecked = Tri(items, FileAttributes.ReadOnly) };
        var hid = new CheckBox { Content = "Skrytý", IsThreeState = true, IsChecked = Tri(items, FileAttributes.Hidden) };
        var sys = new CheckBox { Content = "Systémový", IsThreeState = true, IsChecked = Tri(items, FileAttributes.System) };
        var arc = new CheckBox { Content = "Archivovat", IsThreeState = true, IsChecked = Tri(items, FileAttributes.Archive) };
        var rec = new CheckBox { Content = "Včetně obsahu složek" };
        var apply = UiKit.Button("Použít", async () =>
        {
            DateTime? when = null;
            if (setDate.IsChecked == true && date.SelectedDate.HasValue)
                when = date.SelectedDate.Value.Date + (time.SelectedTime ?? TimeSpan.Zero);
            var change = new LocalFs.AttributeChange
            {
                Modified = when, ReadOnly = ro.IsChecked, Hidden = hid.IsChecked, System = sys.IsChecked, Archive = arc.IsChecked, Recursive = rec.IsChecked == true,
            };
            var paths = items.Select(i => i.FullPath).ToList();
            Close();
            await core.ApplyAttributesAsync(change, paths);
        }, true);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label($"Atributy a časy ({items.Count} položek)", 15, true), setDate,
                UiKit.HStack(8, date, time, now), ro, hid, sys, arc, rec,
                UiKit.Label("Zaškrtnutí beze změny (neurčitý stav) atribut nechá, jak je.", dim: true),
                UiKit.ButtonRow(UiKit.Button("Zrušit", Close), apply)),
        };
        this.CloseOnEscape();
    }

    /// <summary>Společný stav atributu: true / false, nebo null, když se položky liší.</summary>
    private static bool? Tri(List<FileEntry> items, FileAttributes flag)
    {
        bool any = items.Any(i => (i.Attributes & flag) != 0), all = items.All(i => (i.Attributes & flag) != 0);
        return all ? true : any ? null : false;
    }
}

/// <summary>Hromadné přejmenování s živým náhledem.</summary>
public sealed class BatchRenameWindow : Window
{
    public List<string>? Result { get; private set; }

    public BatchRenameWindow(AppCore core, List<FileEntry> items)
    {
        Title = $"Hromadné přejmenování ({items.Count} položek)";
        Width = 640; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var mask = new TextBox { Text = "[N].[E]" };
        var search = new TextBox(); var replace = new TextBox();
        var regex = new CheckBox { Content = "Regulární výraz (v náhradě lze použít $1, $2…)" };
        var start = new NumericUpDown { Value = 1, Minimum = 0, Maximum = 99999, Increment = 1, FormatString = "0" };
        var digits = new NumericUpDown { Value = 1, Minimum = 1, Maximum = 6, Increment = 1, FormatString = "0" };
        var caseBox = new ComboBox { ItemsSource = new[] { "Beze změny", "malá", "VELKÁ" }, SelectedIndex = 0 };
        var preview = new StackPanel { Spacing = 2 };
        var warning = new TextBlock { Foreground = Brushes.Red, Text = "", TextWrapping = TextWrapping.Wrap };
        var apply = UiKit.Button("Přejmenovat", () => { Result = Names(); Close(); }, true);
        var oldNames = items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = core.Active.AllItems.Where(i => !i.IsParent && !oldNames.Contains(i.Name)).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> Names() => BatchRename.NewNames(items.Select(i => (i.Name, i.IsDirectory)).ToList(), new RenameOptions
        {
            Mask = mask.Text ?? "", Search = search.Text ?? "", Replace = replace.Text ?? "", Regex = regex.IsChecked == true,
            Start = (int)(start.Value ?? 1), Digits = (int)(digits.Value ?? 1), CaseMode = caseBox.SelectedIndex,
        });

        void Refresh()
        {
            var names = Names();
            var counts = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var bad = new HashSet<int>();
            for (int i = 0; i < names.Count; i++)
                if (names[i].Length == 0 || counts[names[i]] > 1 || others.Contains(names[i])) bad.Add(i);
            preview.Children.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
                var a = new TextBlock { Text = items[i].Name, TextTrimming = TextTrimming.CharacterEllipsis };
                var arrow = new TextBlock { Text = "→", Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center };
                var b = new TextBlock { Text = names[i], TextTrimming = TextTrimming.CharacterEllipsis };
                if (bad.Contains(i)) b.Foreground = Brushes.Red;
                if (!bad.Contains(i) && names[i] == items[i].Name) b.Opacity = 0.5;
                Grid.SetColumn(arrow, 1); Grid.SetColumn(b, 2);
                g.Children.Add(a); g.Children.Add(arrow); g.Children.Add(b);
                preview.Children.Add(g);
            }
            warning.Text = bad.Count > 0 ? "Červeně označené názvy jsou prázdné, duplicitní nebo už existují." : "";
            apply.IsEnabled = bad.Count == 0 && !names.SequenceEqual(items.Select(i => i.Name));
        }
        mask.TextChanged += (_, _) => Refresh(); search.TextChanged += (_, _) => Refresh(); replace.TextChanged += (_, _) => Refresh();
        regex.IsCheckedChanged += (_, _) => Refresh(); start.ValueChanged += (_, _) => Refresh(); digits.ValueChanged += (_, _) => Refresh();
        caseBox.SelectionChanged += (_, _) => Refresh();

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"), RowSpacing = 6 };
        void Row(int r, string label, Control c) { var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(l, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1); form.Children.Add(l); form.Children.Add(c); }
        Row(0, "Maska", mask); Row(1, "Hledat", search); Row(2, "Nahradit", replace); Row(3, "", regex);
        Row(4, "Čítač od", start); Row(5, "Číslic", digits); Row(6, "Písmena", caseBox);

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label($"Hromadné přejmenování ({items.Count} položek)", 15, true), form,
                UiKit.Label("Maska: [N] = název bez přípony, [E] = přípona, [C] = čítač.", dim: true),
                UiKit.Frame(UiKit.Scroll(preview, 170)), warning,
                UiKit.ButtonRow(UiKit.Button("Zrušit", Close), apply)),
        };
        this.CloseOnEscape();
        Opened += (_, _) => { Refresh(); mask.Focus(); };
    }
}
