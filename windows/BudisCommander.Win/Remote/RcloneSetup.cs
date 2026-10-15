using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BudisCommander.Core;

namespace BudisCommander.Remote;

/// <summary>Služba cloudu: kterou položku v nabídce nabízíme a jaká pole k ní patří.</summary>
public sealed record CloudProvider(string Id, string Title, string RcloneType, bool Browser, (string Key, string Label, bool Secret, string Default)[] Fields);

/// <summary>Stažení programu rclone z oficiálního webu a přidání úložiště bez ruční práce s „rclone config“.</summary>
public static class RcloneSetup
{
    public const string DefaultBaseUrl = "https://downloads.rclone.org";

    public static readonly CloudProvider[] Providers =
    {
        new("drive", "Google Disk", "drive", true, Array.Empty<(string, string, bool, string)>()),
        new("dropbox", "Dropbox", "dropbox", true, Array.Empty<(string, string, bool, string)>()),
        new("onedrive", "Microsoft OneDrive", "onedrive", true, Array.Empty<(string, string, bool, string)>()),
        new("box", "Box", "box", true, Array.Empty<(string, string, bool, string)>()),
        new("s3", "Amazon S3 a kompatibilní", "s3", false, new[]
        {
            ("provider", "Poskytovatel (AWS, Minio, Wasabi…)", false, "AWS"),
            ("access_key_id", "Access key ID", false, ""),
            ("secret_access_key", "Secret access key", true, ""),
            ("region", "Region (např. eu-central-1)", false, ""),
            ("endpoint", "Adresa serveru (jen mimo AWS)", false, ""),
        }),
        new("webdav", "WebDAV (Nextcloud, ownCloud…)", "webdav", false, new[]
        {
            ("url", "Adresa (https://…)", false, ""),
            ("vendor", "Druh (nextcloud, owncloud, other)", false, "other"),
            ("user", "Uživatel", false, ""),
            ("pass", "Heslo", true, ""),
        }),
        new("b2", "Backblaze B2", "b2", false, new[]
        {
            ("account", "ID účtu nebo klíče", false, ""),
            ("key", "Aplikační klíč", true, ""),
        }),
    };

    public static string InstallDir => Path.Combine(AppSettings.Directory, "rclone");
    public static string InstalledExe => Path.Combine(InstallDir, "rclone.exe");

    public static string PlatformSuffix => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "windows-arm64" : "windows-amd64";

    public static bool IsValidName(string name) => Regex.IsMatch(name, @"^[A-Za-z0-9_-]{1,40}$");

    /// <summary>Stáhne rclone z oficiálního webu, ověří kontrolní součet SHA-256 a rozbalí ho do složky aplikace.</summary>
    public static async Task<string> InstallAsync(HttpClient http, string installDir, string platform, string baseUrl,
                                                  IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var versionText = (await http.GetStringAsync($"{baseUrl}/version.txt", ct)).Trim();
        var version = versionText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();           // „rclone v1.68.2“
        if (!Regex.IsMatch(version, @"^v\d+\.\d+\.\d+$")) throw new InvalidOperationException("Nečekaná verze rclone: " + versionText);
        var zipName = $"rclone-{version}-{platform}.zip";

        var sums = await http.GetStringAsync($"{baseUrl}/{version}/SHA256SUMS", ct);
        var line = sums.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(" " + zipName, StringComparison.Ordinal))
                   ?? throw new InvalidOperationException("Kontrolní součet pro " + zipName + " nebyl nalezen.");
        var expected = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

        var work = Path.Combine(Path.GetTempPath(), "budis-rclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var zipPath = Path.Combine(work, zipName);
            using (var resp = await http.GetAsync($"{baseUrl}/{version}/{zipName}", HttpCompletionOption.ResponseHeadersRead, ct))
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
            string actual;
            await using (var fs = File.OpenRead(zipPath)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("Stažený soubor nesouhlasí s kontrolním součtem, instalace byla zrušena.");

            Directory.CreateDirectory(installDir);
            var exeName = platform.StartsWith("windows") ? "rclone.exe" : "rclone";
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.Name == exeName) ?? throw new FileNotFoundException("Archiv neobsahuje " + exeName);
            var target = Path.Combine(installDir, exeName);
            entry.ExtractToFile(target, true);
            return target;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>Vytvoří úložiště bez prohlížeče (S3, WebDAV, B2); hesla rclone zašifruje sám přepínačem --obscure.</summary>
    public static async Task AddWithFieldsAsync(string exe, string name, CloudProvider provider, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        if (!IsValidName(name)) throw new ArgumentException("Název smí obsahovat jen písmena, číslice, pomlčku a podtržítko.");
        var args = new List<string> { "config", "create", name, provider.RcloneType };
        foreach (var f in provider.Fields)
            if (values.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v)) { args.Add(f.Key); args.Add(v.Trim()); }
        args.Add("--obscure");
        var r = await Rclone.RunAsync(exe, args, ct);
        if (r.Code != 0) throw new InvalidOperationException(Rclone.FirstLine(r.Err, "rclone config create selhal."));
    }

    /// <summary>Otevře konzoli s příkazem „rclone config create“; rclone sám otevře prohlížeč s přihlášením ke službě.</summary>
    public static void AddWithBrowser(string exe, string name, CloudProvider provider)
    {
        if (!IsValidName(name)) throw new ArgumentException("Název smí obsahovat jen písmena, číslice, pomlčku a podtržítko.");
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true };
        psi.Arguments = $"/c \"\"{exe}\" config create {name} {provider.RcloneType} & echo. & echo Hotovo. Toto okno zavrete a v aplikaci kliknete na Obnovit. & pause\"";
        Process.Start(psi);
    }

    /// <summary>Chyba rclone, kterou řeší znovu dokončené přihlášení (OneDrive bez vybraného disku).</summary>
    public static bool NeedsReconnect(string message) =>
        message.Contains("drive_id", StringComparison.OrdinalIgnoreCase) || message.Contains("config reconnect", StringComparison.OrdinalIgnoreCase);

    /// <summary>Otevře konzoli s příkazem „rclone config reconnect“: znovu proběhne přihlášení a výběr disku.</summary>
    public static void Reconnect(string exe, string remote)
    {
        var name = remote.TrimEnd(':');
        if (!IsValidName(name)) throw new ArgumentException("Název smí obsahovat jen písmena, číslice, pomlčku a podtržítko.");
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true };
        psi.Arguments = $"/c \"\"{exe}\" config reconnect {name}: & echo. & echo Hotovo. Toto okno zavrete a v aplikaci se pripojte znovu. & pause\"";
        Process.Start(psi);
    }
}
