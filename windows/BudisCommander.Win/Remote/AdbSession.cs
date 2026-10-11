using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BudisCommander.Core;

namespace BudisCommander.Remote;

public sealed record AdbDevice(string Serial, string State, string Model)
{
    public bool Ready => State == "device";
    public string Title => $"{(Model.Length > 0 ? Model.Replace('_', ' ') : Serial)}   [{Serial}]   {State}";
}

/// <summary>Android telefon přes program adb (ladění přes USB): soubory v telefonu jako další panel.</summary>
public static class Adb
{
    public static string ExeName => OperatingSystem.IsWindows() ? "adb.exe" : "adb";
    public static string InstallDir => Path.Combine(AppSettings.Directory, "platform-tools");
    public static string InstalledExe => Path.Combine(InstallDir, ExeName);
    public const string DefaultBaseUrl = "https://dl.google.com/android/repository";

    public static string FindExe(string? custom = null)
    {
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
        if (File.Exists(InstalledExe)) return InstalledExe;
        var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var v in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } sdk) dirs.Add(Path.Combine(sdk, "platform-tools"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools"));
        dirs.Add("/opt/homebrew/bin"); dirs.Add("/usr/local/bin");
        foreach (var d in dirs)
        {
            try { var f = Path.Combine(d.Trim('"'), ExeName); if (File.Exists(f)) return f; } catch { }
        }
        return "";
    }

    /// <summary>Stáhne Android platform-tools od Google a rozbalí je do složky aplikace.</summary>
    public static async Task<string> InstallAsync(HttpClient http, string installDir, string platform, string baseUrl,
                                                  IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "budis-adb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var zipPath = Path.Combine(work, "platform-tools.zip");
            using (var resp = await http.GetAsync($"{baseUrl}/platform-tools-latest-{platform}.zip", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buffer = new byte[128 * 1024]; long done = 0; int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            Directory.CreateDirectory(installDir);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var e in zip.Entries)
            {
                if (e.Length == 0 || string.IsNullOrEmpty(e.Name)) continue;
                var rel = e.FullName.StartsWith("platform-tools/") ? e.FullName["platform-tools/".Length..] : e.FullName;
                if (rel.Contains("..")) continue;
                var target = Path.Combine(installDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, true);
            }
            var exe = Path.Combine(installDir, ExeName);
            if (!File.Exists(exe)) throw new FileNotFoundException("Archiv neobsahuje " + ExeName);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return exe;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    public static List<AdbDevice> ParseDevices(string text)
    {
        var list = new List<AdbDevice>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith('*')) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:"))?["model:".Length..] ?? "";
            list.Add(new AdbDevice(parts[0], parts[1], model));
        }
        return list;
    }

    public static async Task<List<AdbDevice>> ListDevicesAsync(string exe, CancellationToken ct = default)
    {
        var r = await Rclone.RunAsync(exe, new[] { "devices", "-l" }, ct);
        if (r.Code != 0) throw new InvalidOperationException(Rclone.FirstLine(r.Err, "adb devices selhalo."));
        return ParseDevices(r.Text);
    }

    private static readonly Regex Entry = new(@"^([-dlbcps])[rwxsStT\-]{9}[.+@]?\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})\s+(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>Zpracuje výstup „ls -la“ z telefonu (toybox i GNU ls s ISO daty).</summary>
    public static List<FileEntry> ParseListing(string text, string dir)
    {
        var result = new List<FileEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var m = Entry.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            var name = m.Groups[5].Value;
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) name = name[..arrow];
            if (name is "." or "..") continue;
            var type = m.Groups[1].Value[0];
            DateTime? when = DateTime.TryParse($"{m.Groups[3].Value} {m.Groups[4].Value}", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
            result.Add(new FileEntry
            {
                Name = name, FullPath = RemotePath.Child(dir, name), IsDirectory = type is 'd' or 'l',
                Size = type is 'd' or 'l' ? 0 : long.Parse(m.Groups[2].Value), Modified = when, IsRemote = true,
            });
        }
        return result;
    }

    public static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";
}

public sealed class AdbSession : RemoteSession
{
    private readonly string _exe;
    private readonly string _serial;
    private readonly string _model;
    private static readonly Regex Percent = new(@"\[\s*(\d{1,3})%\]", RegexOptions.Compiled);

    public AdbSession(string exe, string serial, string model = "")
    {
        _exe = exe; _serial = serial; _model = model;
        Host = serial;
    }

    public override string DisplayName => "phone " + (_model.Length > 0 ? _model.Replace('_', ' ') : _serial);
    public override string StartPath => "/sdcard";

    private async Task<Rclone.Result> Adb_(IEnumerable<string> args, CancellationToken ct, Action<string>? onErr = null, int maxOut = int.MaxValue)
        => await Rclone.RunAsync(_exe, new[] { "-s", _serial }.Concat(args), ct, onErr, maxOut);

    private async Task<Rclone.Result> Shell(string command, CancellationToken ct)
    {
        var r = await Adb_(new[] { "shell", command }, ct);
        if (r.Code != 0) throw new IOException(Rclone.FirstLine(r.Err.Length > 0 ? r.Err : r.Text, "Příkaz v telefonu selhal."));
        return r;
    }

    public override async Task ConnectAsync(CancellationToken ct)
    {
        var r = await Adb_(new[] { "get-state" }, ct);
        var state = r.Text.Trim();
        if (r.Code != 0 || state != "device")
            throw new IOException(state == "unauthorized" || r.Err.Contains("unauthorized")
                ? "Telefon čeká na povolení: odemkněte ho a potvrďte dotaz „Povolit ladění přes USB“."
                : Rclone.FirstLine(r.Err.Length > 0 ? r.Err : r.Text, "Telefon není připojený nebo nemá zapnuté ladění přes USB."));
    }

    public override async Task<List<FileEntry>> ListAsync(string path, CancellationToken ct)
    {
        var dir = path.EndsWith('/') ? path : path + "/";
        var r = await Adb_(new[] { "shell", "ls -la " + Adb.Quote(dir) }, ct);
        var text = r.Text;
        var entries = Adb.ParseListing(text, path);
        if (entries.Count == 0 && (r.Code != 0 || text.Contains("ls:") || text.Contains("No such file")))
            throw new IOException(Rclone.FirstLine(text.Length > 0 ? text : r.Err, "Složku v telefonu nelze přečíst."));
        return entries;
    }

    private static Action<string>? PercentSink(Action<double>? progress) => progress == null ? null : line =>
    {
        var m = Percent.Match(line);
        if (m.Success) progress(Math.Clamp(int.Parse(m.Groups[1].Value) / 100.0, 0, 1));
    };

    protected override async Task DownloadFileAsync(string remote, string local, long size, Action<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        var r = await Adb_(new[] { "pull", remote, local }, ct, PercentSink(progress));
        if (r.Code != 0) throw new IOException(Rclone.FirstLine(r.Err.Length > 0 ? r.Err : r.Text, "Stažení z telefonu selhalo."));
        progress?.Invoke(1);
    }

    protected override async Task UploadFileAsync(string local, string remote, Action<double>? progress, CancellationToken ct)
    {
        var r = await Adb_(new[] { "push", local, remote }, ct, PercentSink(progress));
        if (r.Code != 0) throw new IOException(Rclone.FirstLine(r.Err.Length > 0 ? r.Err : r.Text, "Nahrání do telefonu selhalo."));
        progress?.Invoke(1);
    }

    protected override Task DeleteFileAsync(string path, CancellationToken ct) => Shell("rm -f " + Adb.Quote(path), ct);
    protected override Task DeleteEmptyDirectoryAsync(string path, CancellationToken ct) => Shell("rmdir " + Adb.Quote(path), ct);
    public override Task MkdirAsync(string path, CancellationToken ct) => Shell("mkdir -p " + Adb.Quote(path), ct);
    public override Task RenameAsync(string from, string to, CancellationToken ct) => Shell($"mv {Adb.Quote(from)} {Adb.Quote(to)}", ct);

    public override async Task<byte[]> ReadHeadAsync(string path, int maxBytes, CancellationToken ct)
    {
        var r = await Adb_(new[] { "shell", $"head -c {maxBytes} {Adb.Quote(path)}" }, ct);
        return r.Out;
    }

    public override void Dispose() { }
}
