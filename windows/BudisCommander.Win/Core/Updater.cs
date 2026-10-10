using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BudisCommander.Core;

public sealed record UpdateInfo(string Commit, string AssetName, string AssetUrl, long Size);

/// <summary>Kontrola nové verze na stránce Releases (tag windows-latest) a výměna běžícího .exe.</summary>
public static class Updater
{
    public const string Repo = "galfar77/budis.net";
    public const string Tag = "windows-latest";

    /// <summary>Commit, ze kterého byla tato aplikace sestavena (z InformationalVersion); prázdný u vývojového sestavení.</summary>
    public static string CurrentCommit
    {
        get
        {
            var v = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var i = v.IndexOf('+');
            return i >= 0 ? v[(i + 1)..].Trim() : "";
        }
    }

    public static string AssetNameForThisMachine =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "BudisCommander-win-arm64.zip" : "BudisCommander-win-x64.zip";

    public static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BudisCommander");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Vrátí informace o nové verzi, nebo null, když je aplikace aktuální.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, string current, string assetName, CancellationToken ct = default)
    {
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/tags/{Tag}", ct);
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var m = Regex.Match(body, @"commitu:\s*`?([0-9a-f]{7,40})`?", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var commit = m.Groups[1].Value.ToLowerInvariant();
        if (current.Length >= 7 && (commit.StartsWith(current.ToLowerInvariant()) || current.ToLowerInvariant().StartsWith(commit))) return null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() != assetName) continue;
            return new UpdateInfo(commit, assetName, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64());
        }
        return null;
    }

    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string dir, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, info.AssetName);
        using var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? info.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(zip);
        var buffer = new byte[128 * 1024];
        long done = 0; int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (total > 0) progress?.Report((double)done / total);
        }
        return zip;
    }

    /// <summary>Vymění soubor aplikace za novou verzi ze ZIPu. Běžící .exe se dá přejmenovat, ne přepsat, proto starý zůstane jako .old.</summary>
    public static void Apply(string zipPath, string exePath)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(zipPath)!, "extract");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ZipFile.ExtractToDirectory(zipPath, tmp);
        var fresh = Directory.EnumerateFiles(tmp, Path.GetFileName(exePath), SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new FileNotFoundException("ZIP neobsahuje " + Path.GetFileName(exePath));
        var old = exePath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exePath, old);
        try { File.Copy(fresh, exePath); }
        catch { File.Move(old, exePath); throw; }
    }

    /// <summary>Po restartu smaže zbytek staré verze.</summary>
    public static void CleanupOld(string exePath)
    {
        try { var old = exePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
    }

    /// <summary>Jestli jde aplikaci vyměnit za sebe samu (běží jako samostatný BudisCommander.exe).</summary>
    public static string? SelfExePath()
    {
        var p = Environment.ProcessPath;
        return p != null && string.Equals(Path.GetFileName(p), "BudisCommander.exe", StringComparison.OrdinalIgnoreCase) ? p : null;
    }
}
