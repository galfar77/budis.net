using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BudisCommander.Core;

namespace BudisCommander.Ui;

/// <summary>Panel rychlého náhledu: místo neaktivního panelu ukazuje soubor pod kurzorem aktivního.</summary>
public sealed class QuickViewControl : UserControl
{
    private readonly AppCore _core;
    private readonly TabGroup _group;
    private PaneState? _pane;
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 4) };
    private readonly ContentControl _body = new();
    private int _version;

    public QuickViewControl(AppCore core, TabGroup group)
    {
        _core = core; _group = group;
        var dock = new DockPanel();
        var bar = new Border { Child = _title, Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) };
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar); dock.Children.Add(_body);
        Content = new Border { BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Child = dock };
        group.Changed += Rebind;
        Rebind();
        AttachedToVisualTree += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => { if (_pane != null) { _pane.CursorChanged -= Refresh; _pane.ListChanged -= Refresh; } group.Changed -= Rebind; };
    }

    private void Rebind()
    {
        if (_pane != null) { _pane.CursorChanged -= Refresh; _pane.ListChanged -= Refresh; }
        _pane = _group.Current;
        _pane.CursorChanged += Refresh; _pane.ListChanged += Refresh;
        Refresh();
    }

    private async void Refresh()
    {
        var pane = _pane;
        var item = pane?.Current;
        int version = ++_version;
        _title.Text = item?.Name ?? "Rychlý náhled";
        if (pane == null || item == null || item.IsParent) { _body.Content = Message("Nic k zobrazení."); return; }
        if (pane.IsRemote) { _body.Content = Message("Náhled souboru na serveru: stiskněte F3."); return; }
        var content = await Task.Run(() => Load(item));
        if (version != _version) return;
        _body.Content = content;
    }

    private static Control Message(string text) => new TextBlock { Text = text, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };

    private static Control Load(FileEntry item)
    {
        try
        {
            if (item.IsDirectory)
            {
                int count = Directory.EnumerateFileSystemEntries(item.FullPath).Count();
                return Dispatch(() => Message($"Složka\n{count} položek, {Formatting.Size(LocalFs.TotalSize(item.FullPath))}"));
            }
            var ext = Path.GetExtension(item.Name).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico" && item.Size < 60 * 1024 * 1024)
            {
                var bytes = File.ReadAllBytes(item.FullPath);
                return Dispatch(() =>
                {
                    using var ms = new MemoryStream(bytes);
                    return new Image { Source = new Bitmap(ms), Stretch = Stretch.Uniform, Margin = new Thickness(8) };
                });
            }
            using var fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[200 * 1024];
            int n = fs.Read(buf, 0, buf.Length);
            var data = buf.AsSpan(0, n).ToArray();
            if (n == 0) return Dispatch(() => Message("Prázdný soubor."));
            var view = AppCore.FromBytes(item.Name, data, null);
            return Dispatch(() => new ScrollViewer { Content = UiKit.Mono(view.Text ?? "") });
        }
        catch (Exception e) { return Dispatch(() => Message(e.Message)); }
    }

    // ovládací prvky se musí vytvářet na vlákně rozhraní
    private static Control Dispatch(Func<Control> make) => Dispatcher.UIThread.CheckAccess() ? make() : Dispatcher.UIThread.InvokeAsync(make).GetAwaiter().GetResult();
}
