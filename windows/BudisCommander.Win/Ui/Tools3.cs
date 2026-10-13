using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BudisCommander.Core;
using Avalonia.Threading;
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
        var colorTheme = new ComboBox { ItemsSource = ColorThemes.All.Select(t => t.Title).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(ColorThemes.All, t => t.Id == s.ColorTheme)) };
        var glass = new CheckBox { Content = "Průhledné okno ve stylu skla (Mica / Acrylic)", IsChecked = s.Glass };
        var updates = new CheckBox { Content = "Při startu zkontrolovat, jestli je dostupná nová verze", IsChecked = s.AutoCheckUpdates };
        var autoInstall = new CheckBox { Content = "Novou verzi stáhnout a nainstalovat automaticky (projeví se po restartu)", IsChecked = s.AutoInstallUpdates };
        var font = new NumericUpDown { Value = (decimal)s.FontSize, Minimum = 9, Maximum = 24, Increment = 1, FormatString = "0" };
        var ext = new CheckBox { Content = "Samostatný sloupec s příponou", IsChecked = s.ShowExtColumn };
        var attr = new CheckBox { Content = "Sloupec Atributy (R H S A)", IsChecked = s.ShowAttrColumn };
        var auto = new CheckBox { Content = "Automaticky počítat velikosti složek (na pozadí, jen lokální složky)", IsChecked = s.AutoDirSizes };
        var bar = new CheckBox { Content = "Tlačítková lišta uživatelských příkazů", IsChecked = s.ShowButtonBar };
        var thumbs = new CheckBox { Content = "Miniatury souborů místo ikon (obrázky, videa, PDF, dokumenty)", IsChecked = s.ShowThumbs, IsEnabled = Thumbnails.Available };

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
            s.AutoInstallUpdates = autoInstall.IsChecked == true;
            s.Glass = glass.IsChecked == true;
            s.ColorTheme = ColorThemes.All[Math.Max(0, colorTheme.SelectedIndex)].Id;
            s.ShowExtColumn = ext.IsChecked == true; s.ShowAttrColumn = attr.IsChecked == true;
            s.AutoDirSizes = auto.IsChecked == true; s.ShowButtonBar = bar.IsChecked == true; s.ShowThumbs = thumbs.IsChecked == true;
            Close();
        }, true);

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), RowSpacing = 6 };
        void Row(int r, string label, Control c) { var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(l, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1); form.Children.Add(l); form.Children.Add(c); }
        Row(0, "Vzhled", theme); Row(1, "Velikost písma", font); Row(2, "Jazyk (po restartu)", language); Row(3, "Barevné téma", colorTheme);

        var dock = new DockPanel { Margin = new Thickness(16) };
        var top = UiKit.VStack(8, UiKit.Label("Nastavení", 15, true), form, glass, ext, attr, auto, bar, thumbs, updates, autoInstall,
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
    private readonly Button _install = UiKit.Button("Nainstalovat rclone", () => { }, true);
    private readonly ComboBox _provider = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { Watermark = "název úložiště (písmena a číslice)" };
    private readonly StackPanel _fields = new() { Spacing = 4 };
    private readonly Dictionary<string, TextBox> _fieldBoxes = new();
    private readonly ProgressBar _bar = new() { Maximum = 1, Height = 6, IsVisible = false };
    private readonly Button _connect = UiKit.Button("Připojit", () => { }, true);
    private readonly Button _add = UiKit.Button("Přidat úložiště", () => { });
    private string? _exe;

    public CloudWindow(AppCore core)
    {
        _core = core;
        Title = "Cloudová úložiště (rclone)";
        Width = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _connect.Click += async (_, _) =>
        {
            if (_remotes.SelectedItem is not string remote) return;
            Close();
            await _core.ConnectCloudAsync(remote);
        };
        _install.Click += async (_, _) => await InstallAsync();
        _add.Click += async (_, _) => await AddAsync();
        _provider.ItemsSource = RcloneSetup.Providers.Select(p => p.Title).ToList();
        _provider.SelectionChanged += (_, _) => BuildFields();
        _provider.SelectedIndex = 0;

        var refresh = UiKit.Button("Obnovit", async () => await Fill());
        var advanced = UiKit.Button("Pokročilé nastavení (rclone config)…", () =>
        {
            if (_exe == null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_exe, "config") { UseShellExecute = true }); }
            catch (Exception e) { _status.Text = e.Message; }
        });
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Cloudová úložiště", 15, true),
                UiKit.Label("Vyberte úložiště a připojte ho do aktivního panelu. Nové přidáte níže; program rclone si aplikace umí sama stáhnout.", dim: true),
                _remotes, _status, _install, _bar,
                UiKit.HStack(8, _connect, refresh, UiKit.Button("Zavřít", Close)),
                new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), Margin = new Thickness(0, 6) },
                UiKit.Label("Přidat úložiště", 13, true), _provider, _name, _fields,
                UiKit.HStack(8, _add, advanced)),
        };
        this.CloseOnEscape();
        Opened += async (_, _) => await Fill();
    }

    private void BuildFields()
    {
        _fields.Children.Clear(); _fieldBoxes.Clear();
        var p = RcloneSetup.Providers[Math.Max(0, _provider.SelectedIndex)];
        _name.Text = p.Id;
        if (p.Browser)
        {
            _fields.Children.Add(UiKit.Label("Otevře se okno programu rclone a prohlížeč s přihlášením ke službě. Po dokončení zavřete okno a klikněte na Obnovit.", dim: true));
            return;
        }
        foreach (var f in p.Fields)
        {
            var box = new TextBox { Watermark = f.Label, Text = f.Default, PasswordChar = f.Secret ? '•' : '\0' };
            _fieldBoxes[f.Key] = box;
            _fields.Children.Add(box);
        }
    }

    private async Task InstallAsync()
    {
        _install.IsEnabled = false; _bar.IsVisible = true; _bar.Value = 0;
        _status.Text = "Stahuji rclone z oficiálního webu rclone.org…";
        try
        {
            using var http = Updater.NewClient();
            var progress = new Progress<double>(f => _bar.Value = f);
            await RcloneSetup.InstallAsync(http, RcloneSetup.InstallDir, RcloneSetup.PlatformSuffix, RcloneSetup.DefaultBaseUrl, progress);
        }
        catch (Exception e) { _status.Text = "Instalace se nezdařila: " + e.Message; }
        finally { _bar.IsVisible = false; _install.IsEnabled = true; }
        await Fill();
    }

    private async Task AddAsync()
    {
        if (_exe == null) { _status.Text = "Nejdřív nainstalujte rclone."; return; }
        var p = RcloneSetup.Providers[Math.Max(0, _provider.SelectedIndex)];
        var name = (_name.Text ?? "").Trim();
        if (!RcloneSetup.IsValidName(name)) { _status.Text = "Název smí obsahovat jen písmena, číslice, pomlčku a podtržítko."; return; }
        if (_remotes.ItemsSource is IEnumerable<string> existing && existing.Contains(name)) { _status.Text = $"Úložiště „{name}“ už existuje."; return; }
        try
        {
            if (p.Browser) { RcloneSetup.AddWithBrowser(_exe, name, p); _status.Text = "Dokončete přihlášení v okně rclone a prohlížeči, pak klikněte na Obnovit."; return; }
            await RcloneSetup.AddWithFieldsAsync(_exe, name, p, _fieldBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text ?? ""));
            _status.Text = $"Úložiště „{name}“ přidáno.";
        }
        catch (Exception e) { _status.Text = e.Message; }
        await Fill();
    }

    private async Task Fill()
    {
        _exe = Rclone.FindExe(_core.Settings.RclonePath);
        _install.IsVisible = _exe == null;
        _connect.IsEnabled = _exe != null;
        if (_exe == null)
        {
            _remotes.ItemsSource = new List<string>();
            _status.Text = "Program rclone zatím není nainstalovaný. Klikněte na „Nainstalovat rclone“.";
            return;
        }
        try
        {
            var list = await Rclone.ListRemotesAsync(_exe);
            _remotes.ItemsSource = list;
            if (list.Count > 0) _remotes.SelectedIndex = 0;
            _connect.IsEnabled = list.Count > 0;
            _status.Text = list.Count == 0 ? "Zatím není nastavené žádné úložiště. Přidejte ho níže." : $"rclone: {_exe}";
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

/// <summary>Správce procesů: seznam, hledání a ukončení procesů.</summary>
public sealed class ProcessWindow : Window
{
    private readonly AppCore _core;
    private readonly TextBox _search = new() { Watermark = "hledat podle názvu, titulku okna nebo PID" };
    private readonly ListBox _list = new() { Height = 380, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 12 };
    private readonly TextBlock _status = new() { Opacity = 0.7 };
    private List<ProcInfo> _all = new();
    private List<ProcInfo> _shown = new();
    private bool _byMemory;

    public ProcessWindow(AppCore core)
    {
        _core = core;
        Title = "Správce procesů";
        Width = 680; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _search.TextChanged += (_, _) => Fill();
        var sort = UiKit.Button("Řadit podle paměti", () => { _byMemory = !_byMemory; Fill(); });
        var refresh = UiKit.Button("Obnovit", Reload);
        var end = UiKit.Button("Ukončit", async () => await KillSelectedAsync(false), true);
        var force = UiKit.Button("Vynutit ukončení", async () => await KillSelectedAsync(true));
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Správce procesů", 15, true), _search, UiKit.Frame(_list),
                UiKit.HStack(8, end, force, refresh, sort, UiKit.Button("Zavřít", Close)), _status),
        };
        this.CloseOnEscape();
        Opened += (_, _) => { Reload(); _search.Focus(); };
    }

    private void Reload()
    {
        _all = ProcessList.Snapshot();
        Fill();
    }

    private void Fill()
    {
        _shown = ProcessList.Filter(_all, _search.Text ?? "", _byMemory).ToList();
        _list.ItemsSource = _shown.Select(p => $"{p.Pid,7}  {Trunc(p.Name, 30),-30} {Formatting.Size(p.MemoryBytes),10}  {p.Threads,4} {Trunc(p.Title, 24)}").ToList().Select(s => s).ToList();
        _status.Text = $"{_shown.Count} z {_all.Count} procesů";
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private async Task KillSelectedAsync(bool tree)
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _shown.Count) return;
        var p = _shown[i];
        var info = tree ? "Proces i všechny jeho podřízené procesy budou okamžitě ukončeny. Neuložená práce se ztratí." : "Procesu se nejdřív pošle požadavek na zavření okna.";
        if (!await _core.Ui.ConfirmAsync($"Ukončit „{p.Name}“ (PID {p.Pid})?", info, tree ? "Vynutit ukončení" : "Ukončit")) return;
        var error = ProcessList.Kill(p.Pid, tree);
        if (error != null) await _core.Ui.ShowErrorAsync(error);
        await Task.Delay(300);
        Reload();
    }
}

/// <summary>Správce přenosů: běžící úloha, fronta a historie; zrušení, odebrání z fronty a opakování.</summary>
public sealed class TransfersWindow : Window
{
    private readonly AppCore _core;
    private readonly ListBox _list = new() { Height = 320 };
    private readonly TextBlock _status = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Maximum = 1, Height = 6 };
    private List<JobRecord> _shown = new();
    private readonly Action _onChanged;

    public TransfersWindow(AppCore core)
    {
        _core = core;
        Title = "Správce přenosů";
        Width = 700; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = UiKit.Button("Zrušit vybranou", () => { if (Selected() is { } j) _core.CancelJobById(j.Id); });
        var retry = UiKit.Button("Spustit znovu", () => { if (Selected() is { } j) _core.RetryJob(j.Id); });
        var all = UiKit.Button("Zrušit vše", () => _core.CancelJob(true));
        var clear = UiKit.Button("Vyčistit hotové", () => _core.ClearFinishedJobs());
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Správce přenosů", 15, true), _bar, _status, UiKit.Frame(_list),
                UiKit.HStack(8, cancel, retry, all, clear, UiKit.Button("Zavřít", Close, true)),
                UiKit.Label("Nové přenosy (F5, F6) se řadí do fronty a běží postupně. Zrušenou nebo selhanou úlohu lze spustit znovu.", dim: true)),
        };
        _onChanged = () => Dispatcher.UIThread.Post(Fill);
        core.JobsChanged += _onChanged; core.StatusChanged += _onChanged;
        Closed += (_, _) => { core.JobsChanged -= _onChanged; core.StatusChanged -= _onChanged; };
        this.CloseOnEscape();
        Opened += (_, _) => Fill();
    }

    private JobRecord? Selected() => _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count ? _shown[_list.SelectedIndex] : null;

    public static string Line(JobRecord j)
    {
        var icon = j.State switch { JobState.Running => "▶", JobState.Queued => "⏳", JobState.Done => "✔", JobState.Cancelled => "⏹", _ => "⚠" };
        var state = j.State switch { JobState.Running => "probíhá", JobState.Queued => "ve frontě", JobState.Done => "hotovo", JobState.Cancelled => "zrušeno", _ => "chyba" };
        var when = (j.Finished ?? j.Started ?? j.Added).ToString("HH:mm");
        return $"{icon} {when}  {Tr.T(state)}  {Tr.T(j.Title)}" + (j.Error != null ? "  – " + j.Error : "");
    }

    private void Fill()
    {
        int sel = _list.SelectedIndex;
        _shown = _core.Jobs.ToList();
        _list.ItemsSource = _shown.Select(Line).ToList();
        if (sel >= 0 && sel < _shown.Count) _list.SelectedIndex = sel;
        _bar.IsVisible = _core.Progress.HasValue;
        if (_core.Progress.HasValue) _bar.Value = _core.Progress.Value;
        _status.Text = _core.CanCancel ? _core.ProgressText : (_shown.Count == 0 ? "Zatím žádné přenosy." : "");
    }
}

/// <summary>Úprava jedné úlohy synchronizace.</summary>
public sealed class SyncTaskEditWindow : Window
{
    private static readonly (string Label, int Minutes)[] Intervals =
    {
        ("Jen ručně", 0), ("Každých 5 minut", 5), ("Každých 15 minut", 15), ("Každých 30 minut", 30),
        ("Každou hodinu", 60), ("Každých 6 hodin", 360), ("Jednou denně", 1440),
    };

    public bool Saved { get; private set; }

    public SyncTaskEditWindow(SyncTask task, AppCore core)
    {
        Title = task.Name.Length == 0 ? "Nová synchronizace" : "Upravit synchronizaci";
        Width = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var name = new TextBox { Text = task.Name, Watermark = "např. Dokumenty na zálohu" };
        var source = new TextBox { Text = task.Source };
        var target = new TextBox { Text = task.Target, Watermark = @"složka, nebo rclone:název:/cesta" };
        var interval = new ComboBox { ItemsSource = Intervals.Select(i => i.Label).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(Intervals, i => i.Minutes == task.IntervalMinutes)) };
        var delete = new CheckBox { Content = "Mazat v cíli soubory, které ve zdroji nejsou", IsChecked = task.DeleteExtra };
        var mode = new ComboBox { ItemsSource = new[] { "Jednosměrně (zdroj → cíl)", "Obousměrně (nové a novější soubory na obě strany, nic se nemaže)" }, SelectedIndex = task.TwoWay ? 1 : 0 };
        mode.SelectionChanged += (_, _) => { delete.IsEnabled = mode.SelectedIndex == 0; if (mode.SelectedIndex == 1) delete.IsChecked = false; };
        delete.IsEnabled = !task.TwoWay;
        var error = new TextBlock { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap };
        var pickSource = UiKit.Button("Vybrat…", async () =>
        {
            var f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Zdrojová složka" });
            if (f.Count > 0 && f[0].TryGetLocalPath() is { } p) source.Text = p;
        });
        var pickTarget = UiKit.Button("Vybrat…", async () =>
        {
            var f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Cílová složka" });
            if (f.Count > 0 && f[0].TryGetLocalPath() is { } p) target.Text = p;
        });
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"), RowSpacing = 6 };
        void Row(int r, string label, Control c, Control? extra = null)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1); form.Children.Add(l); form.Children.Add(c);
            if (extra != null) { Grid.SetRow(extra, r); Grid.SetColumn(extra, 2); form.Children.Add(extra); }
        }
        Row(0, "Název", name); Row(1, "Zdroj", source, pickSource); Row(2, "Cíl", target, pickTarget); Row(3, "Směr", mode); Row(4, "Spouštět", interval);
        var save = UiKit.Button("Uložit", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "Zadejte název."; return; }
            if (!Directory.Exists((source.Text ?? "").Trim())) { error.Text = "Zdrojová složka neexistuje."; return; }
            if (string.IsNullOrWhiteSpace(target.Text)) { error.Text = "Zadejte cíl."; return; }
            if (target.Text!.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase) && SyncTask.ParseRclone(target.Text) == null) { error.Text = "Cíl v cloudu zapište jako rclone:název:/cesta."; return; }
            task.Name = name.Text!.Trim(); task.Source = source.Text!.Trim(); task.Target = target.Text!.Trim();
            task.IntervalMinutes = Intervals[Math.Max(0, interval.SelectedIndex)].Minutes; task.DeleteExtra = delete.IsChecked == true; task.TwoWay = mode.SelectedIndex == 1;
            Saved = true; Close();
        }, true);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Synchronizace na pozadí", 15, true), form, delete,
                UiKit.Label("Jednosměrně: zdroj se kopíruje do cíle. Cíl v cloudu zapište jako rclone:název:/cesta (úložiště přidáte v menu Servery → Cloudová úložiště).", dim: true),
                error, UiKit.ButtonRow(UiKit.Button("Zrušit", Close), save)),
        };
        this.CloseOnEscape();
    }
}

/// <summary>Synchronizace na pozadí: seznam úloh, které se opakují, dokud aplikace běží.</summary>
public sealed class SyncTasksWindow : Window
{
    private readonly AppCore _core;
    private readonly ListBox _list = new() { Height = 260 };
    private readonly TextBlock _status = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

    public SyncTasksWindow(AppCore core)
    {
        _core = core;
        Title = "Synchronizace na pozadí";
        Width = 720; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var add = UiKit.Button("Přidat…", async () =>
        {
            var t = new SyncTask { Source = _core.Active.IsRemote ? "" : _core.Active.PersistentPath };
            var w = new SyncTaskEditWindow(t, _core);
            await w.ShowDialog(this);
            if (w.Saved) { _core.Settings.SyncTasks.Add(t); _core.Settings.Save(); Fill(); }
        });
        var edit = UiKit.Button("Upravit…", async () =>
        {
            if (Selected() is not { } t) return;
            var w = new SyncTaskEditWindow(t, _core);
            await w.ShowDialog(this);
            if (w.Saved) { _core.Settings.Save(); Fill(); }
        });
        var del = UiKit.Button("Smazat", () =>
        {
            if (Selected() is not { } t) return;
            _core.Settings.SyncTasks.Remove(t); _core.Settings.Save(); Fill();
        });
        var run = UiKit.Button("Spustit teď", async () =>
        {
            if (Selected() is not { } t) return;
            _status.Text = "Synchronizuji…";
            var result = await _core.RunSyncNowAsync(t);
            _status.Text = $"„{t.Name}“: {result}";
            Fill();
        }, true);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Synchronizace na pozadí", 15, true), UiKit.Frame(_list),
                UiKit.HStack(8, add, edit, del, run, UiKit.Button("Zavřít", Close)), _status,
                UiKit.Label("Úlohy se spouštějí podle nastavené doby, dokud je aplikace spuštěná. Běh a výsledek je vidět ve správci přenosů.", dim: true)),
        };
        this.CloseOnEscape();
        Opened += (_, _) => Fill();
    }

    private SyncTask? Selected() => _list.SelectedIndex >= 0 && _list.SelectedIndex < _core.Settings.SyncTasks.Count ? _core.Settings.SyncTasks[_list.SelectedIndex] : null;

    private void Fill()
    {
        _list.ItemsSource = _core.Settings.SyncTasks.Select(t =>
            t.Describe() + (t.LastRun is { } l ? $"   – naposledy {l:d. M. HH:mm}: {t.LastResult}" : "")).ToList();
        if (_core.Settings.SyncTasks.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
    }
}

/// <summary>Vestavěný terminál: skutečný pseudoterminál (ConPTY), takže fungují i vim, top, ssh a další celoobrazovkové programy.</summary>
public sealed class TerminalWindow : Window
{
    private readonly AppCore _core;
    private readonly BudisCommander.Ui.TerminalControl _term = new();

    public TerminalWindow(AppCore core)
    {
        _core = core;
        Title = "Terminál";
        Width = 900; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cd = UiKit.Button("Přejít do složky panelu", () =>
        {
            var dir = CurrentDir();
            _term.SendText(OperatingSystem.IsWindows() ? $"cd /d \"{dir}\"\r" : $"cd '{dir}'\r");
            _term.Focus();
        });
        var restart = UiKit.Button("Restartovat", () => { _term.Restart(); _term.Focus(); });
        var bar = UiKit.HStack(8, cd, restart, UiKit.Button("Zavřít", Close));
        bar.Margin = new Thickness(8, 6);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_term);
        Content = dock;
        _term.TitleOrStateChanged += () => Title = _term.Title.Length > 0 ? "Terminál – " + _term.Title : "Terminál";
        Opened += (_, _) => { _term.Start(CurrentDir()); _term.Focus(); };
        Closed += (_, _) => _term.Stop();
    }

    private string CurrentDir() => _core.Active.IsRemote ? Environment.CurrentDirectory : _core.Active.PersistentPath;
}

/// <summary>Rozšířené označování: maska, druh, velikost a datum změny najednou.</summary>
public sealed class MarkAdvancedWindow : Window
{
    public MarkAdvancedWindow(PaneState pane)
    {
        Title = "Označit podle podmínek";
        Width = 480; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var mask = new TextBox { Text = "*", Watermark = "např. *.jpg;*.png" };
        var kind = new ComboBox { ItemsSource = new[] { "Soubory i složky", "Jen soubory", "Jen složky" }, SelectedIndex = 1 };
        var min = new TextBox { Watermark = "např. 10 MB" };
        var max = new TextBox { Watermark = "např. 1 GB" };
        var from = new TextBox { Watermark = "např. 1. 1. 2025" };
        var to = new TextBox { Watermark = "např. 31. 12. 2025" };
        var action = new ComboBox { ItemsSource = new[] { "Označit", "Odznačit" }, SelectedIndex = 0 };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"), RowSpacing = 6 };
        void Row(int r, string label, Control c)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1); form.Children.Add(l); form.Children.Add(c);
        }
        Row(0, "Maska názvu", mask); Row(1, "Druh", kind); Row(2, "Velikost od", min); Row(3, "Velikost do", max);
        Row(4, "Změněno od", from); Row(5, "Změněno do", to); Row(6, "Akce", action);
        var go = UiKit.Button("Provést", () =>
        {
            if (!SelectionCriteria.TryParseSize(min.Text, out var mn) || !SelectionCriteria.TryParseSize(max.Text, out var mx)) { result.Text = "Velikost zapište např. 500, 10 KB nebo 1,5 MB."; result.Foreground = Brushes.Red; return; }
            if (!SelectionCriteria.TryParseDate(from.Text, out var df) || !SelectionCriteria.TryParseDate(to.Text, out var dt)) { result.Text = "Datum zapište např. 31. 12. 2025 nebo 2025-12-31."; result.Foreground = Brushes.Red; return; }
            var c = new SelectionCriteria { Mask = mask.Text ?? "*", Kind = kind.SelectedIndex, MinSize = mn, MaxSize = mx, ModifiedFrom = df, ModifiedTo = dt };
            var on = action.SelectedIndex == 0;
            var n = pane.MarkWhere(c, on);
            result.Foreground = null; result.Text = (on ? "Označeno: " : "Odznačeno: ") + n;
        }, true);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Označit podle podmínek", 15, true), form,
                UiKit.Label("Vyplněné podmínky platí zároveň. Složky nemají velikost, takže při zadané velikosti se vynechají.", dim: true),
                result, UiKit.ButtonRow(UiKit.Button("Zavřít", Close), go)),
        };
        this.CloseOnEscape();
    }
}

/// <summary>Telefon (Android přes adb): seznam připojených telefonů, instalace adb a připojení do panelu.</summary>
public sealed class PhoneWindow : Window
{
    private readonly AppCore _core;
    private readonly ListBox _list = new() { Height = 150 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly Button _install = UiKit.Button("Nainstalovat adb", () => { }, true);
    private readonly ProgressBar _bar = new() { Maximum = 1, Height = 6, IsVisible = false };
    private List<AdbDevice> _devices = new();
    private string _exe = "";

    public PhoneWindow(AppCore core)
    {
        _core = core;
        Title = "Telefon (Android)";
        Width = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _install.Click += async (_, _) => await InstallAsync();
        _list.DoubleTapped += async (_, _) => await ConnectAsync();
        var connect = UiKit.Button("Připojit", async () => await ConnectAsync(), true);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = UiKit.VStack(8, UiKit.Label("Telefon (Android)", 15, true),
                UiKit.Label("V telefonu zapněte Nastavení → Možnosti pro vývojáře → Ladění přes USB, připojte ho kabelem a potvrďte v telefonu dotaz na povolení. Soubory telefonu se pak otevřou v aktivním panelu.", dim: true),
                UiKit.Frame(_list), _status, _install, _bar,
                UiKit.HStack(8, connect, UiKit.Button("Obnovit", async () => await Fill()), UiKit.Button("Zavřít", Close)),
                UiKit.Label("iPhone a iPad: Apple neumožňuje přímý přístup k souborům přes kabel, použijte AirDrop nebo iCloud.", dim: true)),
        };
        this.CloseOnEscape();
        Opened += async (_, _) => await Fill();
    }

    private async Task Fill()
    {
        _exe = Adb.FindExe(_core.Settings.AdbPath);
        _install.IsVisible = _exe.Length == 0;
        if (_exe.Length == 0) { _list.ItemsSource = new List<string>(); _status.Text = "Program adb zatím není nainstalovaný. Klikněte na „Nainstalovat adb“ (stáhne se od Googlu)."; return; }
        try
        {
            _devices = await Adb.ListDevicesAsync(_exe);
            _list.ItemsSource = _devices.Select(d => d.Title).ToList();
            if (_devices.Count > 0) _list.SelectedIndex = 0;
            _status.Text = _devices.Count == 0 ? "Žádný telefon nenalezen. Zkontrolujte kabel a ladění přes USB." : "";
        }
        catch (Exception e) { _status.Text = e.Message; }
    }

    private async Task InstallAsync()
    {
        _install.IsEnabled = false; _bar.IsVisible = true; _bar.Value = 0;
        _status.Text = "Stahuji Android platform-tools z dl.google.com…";
        try
        {
            using var http = Updater.NewClient();
            await Adb.InstallAsync(http, Adb.InstallDir, OperatingSystem.IsWindows() ? "windows" : "darwin", Adb.DefaultBaseUrl, new Progress<double>(f => _bar.Value = f));
        }
        catch (Exception e) { _status.Text = "Instalace se nezdařila: " + e.Message; }
        finally { _bar.IsVisible = false; _install.IsEnabled = true; }
        await Fill();
    }

    private async Task ConnectAsync()
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _devices.Count) return;
        var d = _devices[i];
        Close();
        await _core.ConnectPhoneAsync(d);
    }
}
