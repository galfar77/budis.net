using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BudisCommander.Core;

namespace BudisCommander.Remote;

/// <summary>Pomocné funkce kolem programu rclone (cloudová úložiště: Dropbox, Google Drive, OneDrive a další).</summary>
public static class Rclone
{
    public static string ExeName => OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";

    /// <summary>Najde rclone: nejdřív cesta z nastavení, pak PATH a obvyklá místa instalace.</summary>
    public static string? FindExe(string? custom)
    {
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
        var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "rclone"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims"));
        }
        else { dirs.Add("/usr/local/bin"); dirs.Add("/opt/homebrew/bin"); dirs.Add("/usr/bin"); }
        foreach (var d in dirs)
        {
            try { var f = Path.Combine(d.Trim('"'), ExeName); if (File.Exists(f)) return f; } catch { }
        }
        return null;
    }

    public sealed record Result(int Code, byte[] Out, string Err)
    {
        public string Text => Encoding.UTF8.GetString(Out);
    }

    /// <summary>Spustí rclone; <paramref name="onErrLine"/> dostává řádky z chybového výstupu (průběh).</summary>
    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, Action<string>? onErrLine = null, int maxOutBytes = int.MaxValue)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Program rclone se nepodařilo spustit.");
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (err) { if (err.Length < 20_000) err.AppendLine(e.Data); }
            onErrLine?.Invoke(e.Data);
        };
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await p.StandardOutput.BaseStream.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            if (ms.Length < maxOutBytes) ms.Write(buffer, 0, (int)Math.Min(n, maxOutBytes - ms.Length));
            if (ms.Length >= maxOutBytes) { try { p.Kill(true); } catch { } break; }
        }
        await p.WaitForExitAsync(CancellationToken.None);
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        string text; lock (err) text = err.ToString();
        return new Result(p.ExitCode, ms.ToArray(), text);
    }

    /// <summary>Názvy nastavených úložišť (bez dvojtečky).</summary>
    public static async Task<List<string>> ListRemotesAsync(string exe, CancellationToken ct = default)
    {
        var r = await RunAsync(exe, new[] { "listremotes" }, ct);
        if (r.Code != 0) throw new InvalidOperationException(FirstLine(r.Err, "rclone listremotes selhal."));
        return r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().TrimEnd(':')).Where(l => l.Length > 0).ToList();
    }

    public static string FirstLine(string text, string fallback)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line ?? fallback;
    }
}

/// <summary>Cloudové úložiště přes rclone: výpis, nahrávání, stahování a mazání se provádějí příkazy rclone.</summary>
public sealed class RcloneSession : RemoteSession
{
    private readonly string _exe;
    private readonly string _remote;

    public RcloneSession(string exe, string remote)
    {
        _exe = exe;
        _remote = remote.TrimEnd(':');
        Host = _remote;
    }

    public override string DisplayName => "cloud " + _remote + ":";
    public override string StartPath => "/";

    private string Spec(string path) => _remote + ":" + RemotePath.Normalize(path).TrimStart('/');

    private async Task<Rclone.Result> Run(IEnumerable<string> args, CancellationToken ct, Action<string>? onErr = null, int maxOut = int.MaxValue)
    {
        var r = await Rclone.RunAsync(_exe, args, ct, onErr, maxOut);
        if (r.Code != 0 && !(maxOut < int.MaxValue))
            throw new IOException(Rclone.FirstLine(r.Err.Split('\n').LastOrDefault(l => l.Contains("ERROR") || l.Contains("Failed")) ?? r.Err, "rclone skončil chybou."));
        return r;
    }

    public override async Task ConnectAsync(CancellationToken ct) => await Run(new[] { "lsjson", "--max-depth", "1", "--dirs-only", Spec("/") }, ct);

    public override async Task<List<FileEntry>> ListAsync(string path, CancellationToken ct)
    {
        var r = await Run(new[] { "lsjson", Spec(path) }, ct);
        var result = new List<FileEntry>();
        using var doc = JsonDocument.Parse(r.Out.Length == 0 ? "[]"u8.ToArray() : r.Out);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var name = e.GetProperty("Name").GetString() ?? "";
            if (name.Length == 0) continue;
            bool dir = e.TryGetProperty("IsDir", out var d) && d.GetBoolean();
            long size = !dir && e.TryGetProperty("Size", out var s) && s.ValueKind == JsonValueKind.Number ? Math.Max(0, s.GetInt64()) : 0;
            DateTime? mod = null;
            if (e.TryGetProperty("ModTime", out var m) && m.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(m.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dto))
                mod = dto.LocalDateTime;
            result.Add(new FileEntry { Name = name, FullPath = RemotePath.Child(path, name), IsDirectory = dir, Size = size, Modified = mod, IsRemote = true });
        }
        return result;
    }

    private static IEnumerable<string> ProgressArgs => new[] { "--use-json-log", "--stats", "300ms", "-v" };

    private static Action<string> ProgressParser(Action<double>? progress) => line =>
    {
        if (progress == null || !line.Contains("\"stats\"")) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("stats", out var st)) return;
            var total = st.TryGetProperty("totalBytes", out var t) ? t.GetDouble() : 0;
            var bytes = st.TryGetProperty("bytes", out var b) ? b.GetDouble() : 0;
            if (total > 0) progress(Math.Clamp(bytes / total, 0, 1));
        }
        catch { /* řádek bez statistiky */ }
    };

    protected override async Task DownloadFileAsync(string remote, string local, long size, Action<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await Run(new[] { "copyto" }.Concat(ProgressArgs).Concat(new[] { Spec(remote), local }), ct, ProgressParser(progress));
        progress?.Invoke(1);
    }

    protected override async Task UploadFileAsync(string local, string remote, Action<double>? progress, CancellationToken ct)
    {
        await Run(new[] { "copyto" }.Concat(ProgressArgs).Concat(new[] { local, Spec(remote) }), ct, ProgressParser(progress));
        progress?.Invoke(1);
    }

    protected override Task DeleteFileAsync(string path, CancellationToken ct) => Run(new[] { "deletefile", Spec(path) }, ct);
    protected override Task DeleteEmptyDirectoryAsync(string path, CancellationToken ct) => Run(new[] { "rmdir", Spec(path) }, ct);
    public override Task MkdirAsync(string path, CancellationToken ct) => Run(new[] { "mkdir", Spec(path) }, ct);
    public override Task RenameAsync(string from, string to, CancellationToken ct) => Run(new[] { "moveto", Spec(from), Spec(to) }, ct);

    public override async Task<byte[]> ReadHeadAsync(string path, int maxBytes, CancellationToken ct)
    {
        var r = await Run(new[] { "cat", "--count", maxBytes.ToString(), Spec(path) }, ct, null, maxBytes);
        return r.Out;
    }

    public override void Dispose() { }
}
