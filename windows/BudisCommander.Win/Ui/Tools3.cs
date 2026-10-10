using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BudisCommander.Core;
using BudisCommander.Remote;

namespace BudisCommander.Ui;

/// <summary>Připojení k FTP, FTP+TLS nebo SFTP serveru.</summary>
public sealed class ConnectWindow : Window
{
    private readonly AppCore _core;
    private readonly ComboBox _saved = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _proto = new() { ItemsSource = new[] { "FTP", "FTP + TLS", "SFTP" }, SelectedIndex = 2 };
    private readonly TextBox _host = new() { Watermark = "např. ftp.example.com" };
    private readonly TextBox _port = new() { Width = 80 };
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { PasswordChar = '•' };
    private readonly TextBox _key = new() { Watermark = "volitelné, soukromý SSH klíč (SFTP)" };
    private readonly CheckBox _insecure = new() { Content = "Důvěřovat serveru bez ověření klíče/certifikátu" };
    private readonly CheckBox _remember = new() { Content = "Zapamatovat heslo (zašifrované pro tohoto uživatele Windows)" };
    private readonly Button _connect = new() { Content = "Připojit" };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, IsVisible = false, Height = 4 };
    private bool _filling;

    public ConnectWindow(AppCore core)
    {
        _core = core;
        Title = "Připojit k serveru";
        Width = 520; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var servers = core.Settings.Servers;
        _saved.ItemsSource = servers.Select(s => s.Title).ToList();
        _saved.SelectionChanged += (_, _) =>
        {
            if (_saved.SelectedIndex >= 0 && _saved.SelectedIndex < servers.Count) Fill(servers[_saved.SelectedIndex]);
        };
        _proto.SelectionChanged += (_, _) => { if (!_filling) _port.Watermark = DefaultPort().ToString(); };
        _port.Watermark = DefaultPort().ToString();

        var pick = UiKit.Button("Vybrat…", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Soukromý SSH klíč", AllowMultiple = false });
            if (files.Count > 0) _key.Text = files[0].TryGetLocalPath();
        });
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"), RowSpacing = 6 };
        void Row(int r, string label, Control c, Control? extra = null)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1);
            form.Children.Add(l); form.Children.Add(c);
            if (extra != null) { Grid.SetRow(extra, r); Grid.SetColumn(extra, 2); form.Children.Add(extra); }
        }
        Row(0, "Uložené", _saved); Row(1, "Protokol", _proto); Row(2, "Server", _host, _port);
        Row(3, "Uživatel", _user); Row(4, "Heslo", _password); Row(5, "Klíč", _key, pick);
        _host.Margin = new Thickness(0, 0, 6, 0);

        _connect.Click += async (_, _) => await ConnectAsync();
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Připojit k serveru", 15, true), form, _insecure, _remember, _busy,
                UiKit.Label("U SFTP s klíčem je heslo heslem ke klíči. Prázdný uživatel u FTP = anonymní přihlášení.", dim: true),
                UiKit.ButtonRow(UiKit.Button("Zrušit", Close), _connect)),
        };
        _connect.Classes.Add("accent");
        this.CloseOnEscape();
        Opened += (_, _) => _host.Focus();
    }

    private int DefaultPort() => _proto.SelectedIndex == 2 ? 22 : 21;

    private void Fill(SavedServer s)
    {
        _filling = true;
        _proto.SelectedIndex = (int)s.Protocol == 0 ? 0 : (int)s.Protocol == 1 ? 1 : 2;
        _host.Text = s.Host; _port.Text = s.Port > 0 ? s.Port.ToString() : "";
        _user.Text = s.User; _key.Text = s.KeyPath; _insecure.IsChecked = s.Insecure;
        var pw = AppSettings.Unprotect(s.ProtectedPassword);
        _password.Text = pw ?? ""; _remember.IsChecked = pw != null;
        _filling = false;
    }

    private async Task ConnectAsync()
    {
        var host = (_host.Text ?? "").Trim();
        if (host.Length == 0) return;
        var server = new SavedServer
        {
            Protocol = _proto.SelectedIndex switch { 0 => RemoteProtocol.Ftp, 1 => RemoteProtocol.FtpTls, _ => RemoteProtocol.Sftp },
            Host = host,
            Port = int.TryParse(_port.Text, out var p) ? p : 0,
            User = (_user.Text ?? "").Trim(),
            Insecure = _insecure.IsChecked == true,
            KeyPath = (_key.Text ?? "").Trim(),
        };
        var password = _password.Text ?? "";
        _connect.IsEnabled = false; _busy.IsVisible = true;
        bool ok = await _core.ConnectAsync(server, password);
        _connect.IsEnabled = true; _busy.IsVisible = false;
        if (!ok) return;
        if (_remember.IsChecked == true) server.ProtectedPassword = AppSettings.Protect(password);
        var list = _core.Settings.Servers;
        list.RemoveAll(s => s.Title == server.Title);
        list.Insert(0, server);
        if (list.Count > 20) list.RemoveRange(20, list.Count - 20);
        _core.Settings.Save();
        Close();
    }
}

/// <summary>Nastavení: vzhled, sloupce a klávesové zkratky.</summary>
public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettings s)
    {
        Title = "Nastavení";
        Width = 560; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var theme = new ComboBox { ItemsSource = new[] { "Podle systému", "Světlý", "Tmavý" }, SelectedIndex = s.Theme switch { "light" => 1, "dark" => 2, _ => 0 } };
        var language = new ComboBox { ItemsSource = new[] { "Čeština", "English" }, SelectedIndex = s.Language == "en" ? 1 : 0 };
        var updates = new CheckBox { Content = "Při startu zkontrolovat, jestli je dostupná nová verze", IsChecked = s.AutoCheckUpdates };
        var font = new NumericUpDown { Value = (decimal)s.FontSize, Minimum = 9, Maximum = 24, Increment = 1, FormatString = "0" };
        var ext = new CheckBox { Content = "Samostatný sloupec s příponou", IsChecked = s.ShowExtColumn };
        var attr = new CheckBox { Content = "Sloupec Atributy (R H S A)", IsChecked = s.ShowAttrColumn };
        var auto = new CheckBox { Content = "Automaticky počítat velikosti složek (na pozadí, jen lokální složky)", IsChecked = s.AutoDirSizes };
        var bar = new CheckBox { Content = "Tlačítková lišta uživatelských příkazů", IsChecked = s.ShowButtonBar };

        var rows = new List<(ActionDef Action, TextBox Box)>();
        var grid = new StackPanel { Spacing = 4 };
        foreach (var a in Actions.All.Where(a => a.Menu != ""))
        {
            var box = new TextBox { Text = Actions.GestureText(a, s), Width = 170, Watermark = "žádná" };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = a.Title, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            grid.Children.Add(row);
            rows.Add((a, box));
        }
        var error = new TextBlock { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap };
        var reset = UiKit.Button("Obnovit výchozí zkratky", () => { foreach (var (a, box) in rows) box.Text = a.DefaultGesture ?? ""; });
        var save = UiKit.Button("Uložit", () =>
        {
            var gestures = new Dictionary<string, string>();
            foreach (var (a, box) in rows)
            {
                var t = (box.Text ?? "").Trim();
                if (t == (a.DefaultGesture ?? "")) continue;
                if (t.Length > 0)
                {
                    try { KeyGesture.Parse(t); }
                    catch { error.Text = $"Zkratka „{t}“ u akce „{a.Title}“ není platná (příklad: Ctrl+Shift+K)."; return; }
                }
                gestures[a.Id] = t;
            }
            s.Gestures = gestures;
            s.Theme = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
            s.FontSize = (double)(font.Value ?? 13);
            s.Language = language.SelectedIndex == 1 ? "en" : "cs";
            s.AutoCheckUpdates = updates.IsChecked == true;
            s.ShowExtColumn = ext.IsChecked == true; s.ShowAttrColumn = attr.IsChecked == true;
            s.AutoDirSizes = auto.IsChecked == true; s.ShowButtonBar = bar.IsChecked == true;
            Close();
        }, true);

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 6 };
        void Row(int r, string label, Control c) { var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(l, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1); form.Children.Add(l); form.Children.Add(c); }
        Row(0, "Vzhled", theme); Row(1, "Velikost písma", font); Row(2, "Jazyk (po restartu)", language);

        var dock = new DockPanel { Margin = new Thickness(16) };
        var top = UiKit.VStack(8, UiKit.Label("Nastavení", 15, true), form, ext, attr, auto, bar, updates,
            UiKit.Label("Klávesové zkratky (např. Ctrl+Shift+K; prázdné = žádná)", dim: true));
        var bottom = UiKit.VStack(6, error, UiKit.HStack(8, reset, new Border { Width = 30 }, UiKit.Button("Zrušit", Close), save));
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(top); dock.Children.Add(bottom);
        dock.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Margin = new Thickness(0, 6) });
        Content = dock;
        this.CloseOnEscape();
    }
}

/// <summary>Uživatelské příkazy pro menu a tlačítkovou lištu.</summary>
public sealed class UserMenuWindow : Window
{
    private readonly AppSettings _settings;
    private readonly StackPanel _rows = new() { Spacing = 6 };

    public UserMenuWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "Uživatelské příkazy";
        Width = 700; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Render();
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Uživatelské příkazy", 15, true),
                UiKit.Label("Zástupné znaky: %f soubor pod kurzorem, %n jeho název, %d složka panelu, %o složka druhého panelu, %F označené soubory (nebo soubor pod kurzorem), %% znak procenta. Cesty se samy uzavřou do uvozovek. Příkazy se spouštějí v příkazovém řádku Windows.", dim: true),
                UiKit.Scroll(_rows, 240),
                UiKit.HStack(8,
                    UiKit.Button("Přidat příkaz", () => { _settings.UserCommands.Add(new UserCommand { Name = "Nový", Command = "" }); Render(); }),
                    UiKit.Button("Výchozí", () => { _settings.UserCommands = UserCommand.Defaults(); Render(); }),
                    new Border { Width = 30 }, UiKit.Button("Hotovo", Close, true))),
        };
        this.CloseOnEscape();
    }

    private void Render()
    {
        _rows.Children.Clear();
        foreach (var cmd in _settings.UserCommands.ToList())
        {
            var c = cmd;
            var name = new TextBox { Text = c.Name, Width = 170 };
            var command = new TextBox { Text = c.Command, FontFamily = new FontFamily("Consolas, monospace") };
            name.TextChanged += (_, _) => c.Name = name.Text ?? "";
            command.TextChanged += (_, _) => c.Command = command.Text ?? "";
            var del = new Button { Content = "✕" };
            del.Click += (_, _) => { _settings.UserCommands.Remove(c); Render(); };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*,Auto"), ColumnSpacing = 6 };
            Grid.SetColumn(command, 1); Grid.SetColumn(del, 2);
            row.Children.Add(name); row.Children.Add(command); row.Children.Add(del);
            _rows.Children.Add(row);
        }
    }
}

/// <summary>Cloudová úložiště (Dropbox, Google Drive, OneDrive…) přes program rclone.</summary>
public sealed class CloudWindow : Window
{
    private readonly AppCore _core;
    private readonly ComboBox _remotes = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBox _path = new() { Watermark = "cesta k rclone (volitelné, jinak se hledá v PATH)" };
    private string? _exe;

    public CloudWindow(AppCore core)
    {
        _core = core;
        Title = "Cloudová úložiště (rclone)";
        Width = 540; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _path.Text = core.Settings.RclonePath;
        var connect = UiKit.Button("Připojit", async () =>
        {
            if (_remotes.SelectedItem is not string remote) return;
            Close();
            await _core.ConnectCloudAsync(remote);
        }, true);
        var configure = UiKit.Button("Nastavit úložiště…", () =>
        {
            if (_exe == null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_exe, "config") { UseShellExecute = true }); }
            catch (Exception e) { _status.Text = e.Message; }
        });
        var refresh = UiKit.Button("Obnovit", async () => { _core.Settings.RclonePath = (_path.Text ?? "").Trim(); await Fill(); });
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Cloudová úložiště", 15, true),
                UiKit.Label("Úložiště se nastavují programem rclone (příkaz „rclone config“). Tady stačí vybrat jedno z nich a připojit ho do aktivního panelu.", dim: true),
                _remotes, _status, _path,
                UiKit.HStack(8, connect, configure, refresh, UiKit.Button("Zavřít", Close))),
        };
        this.CloseOnEscape();
        Opened += async (_, _) => await Fill();
    }

    private async Task Fill()
    {
        _exe = Rclone.FindExe(_core.Settings.RclonePath);
        if (_exe == null)
        {
            _remotes.ItemsSource = new List<string>();
            _status.Text = "Program rclone nebyl nalezen. Nainstalujte ho příkazem „winget install Rclone.Rclone“ (nebo z rclone.org), pak klikněte na Obnovit.";
            return;
        }
        try
        {
            var list = await Rclone.ListRemotesAsync(_exe);
            _remotes.ItemsSource = list;
            if (list.Count > 0) _remotes.SelectedIndex = 0;
            _status.Text = list.Count == 0 ? "Zatím není nastavené žádné úložiště. Klikněte na „Nastavit úložiště…“." : $"rclone: {_exe}";
        }
        catch (Exception e) { _status.Text = e.Message; }
    }
}

/// <summary>Okno nápovědy: seznam odrážek s nadpisy (co je nového, přehled funkcí, o aplikaci).</summary>
public sealed class InfoWindow : Window
{
    public InfoWindow(string title, IEnumerable<string> lines, string? header = null, Action? onUpdate = null)
    {
        Title = Tr.T(title);
        Width = 640; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Spacing = 6 };
        if (header != null) stack.Children.Add(UiKit.Label(header, 13));
        foreach (var raw in lines)
        {
            if (raw.StartsWith("# "))
                stack.Children.Add(new TextBlock { Text = Tr.T(raw)[2..], FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            else
                stack.Children.Add(new TextBlock { Text = "•  " + Tr.T(raw), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) });
        }
        var buttons = new List<Control>();
        if (onUpdate != null) buttons.Add(UiKit.Button("Zkontrolovat aktualizace", () => { Close(); onUpdate(); }));
        buttons.Add(UiKit.Button("Zavřít", Close, true));
        var dock = new DockPanel { Margin = new Thickness(16) };
        var bar = UiKit.ButtonRow(buttons.ToArray());
        bar.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(bar, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = dock;
        this.CloseOnEscape();
    }
}
