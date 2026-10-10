using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BudisCommander.Core;

public enum RemoteProtocol { Ftp, FtpTls, Sftp }

public sealed class SavedServer
{
    public RemoteProtocol Protocol { get; set; } = RemoteProtocol.Sftp;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";
    public bool Insecure { get; set; }
    public string KeyPath { get; set; } = "";
    /// <summary>Heslo zašifrované přes DPAPI (jen aktuální uživatel Windows); prázdné = neuloženo.</summary>
    public string? ProtectedPassword { get; set; }

    [JsonIgnore]
    public string Title => $"{Protocol.ToString().ToUpperInvariant()}  {(User.Length > 0 ? User + "@" : "")}{Host}:{EffectivePort}";
    [JsonIgnore]
    public int EffectivePort => Port > 0 ? Port : Protocol == RemoteProtocol.Sftp ? 22 : 21;
}

public sealed class UserCommand
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";

    public static List<UserCommand> Defaults() => new()
    {
        new UserCommand { Name = "Průzkumník zde", Command = "explorer.exe %d" },
        new UserCommand { Name = "Ukázat v Průzkumníku", Command = "explorer.exe /select,%f" },
        new UserCommand { Name = "PowerShell zde", Command = "powershell.exe -NoExit -Command Set-Location %d" },
    };
}

/// <summary>Pojmenovaná sada záložek obou panelů.</summary>
public sealed class TabSet
{
    public string Name { get; set; } = "";
    public List<string> Left { get; set; } = new();
    public int LeftSelected { get; set; }
    public List<string> Right { get; set; } = new();
    public int RightSelected { get; set; }
}

public sealed class SessionState
{
    public List<string> Left { get; set; } = new();
    public int LeftSelected { get; set; }
    public List<string> Right { get; set; } = new();
    public int RightSelected { get; set; }
    public bool ActiveLeft { get; set; } = true;
}

public sealed class AppSettings
{
    public double FontSize { get; set; } = 13;
    /// <summary>system, light, dark</summary>
    public string Theme { get; set; } = "system";
    public bool ShowExtColumn { get; set; }
    public bool ShowAttrColumn { get; set; }
    public bool AutoDirSizes { get; set; }
    public bool ShowHidden { get; set; }
    public bool ShowButtonBar { get; set; } = true;
    public List<string> Favorites { get; set; } = new();
    public List<string> Recents { get; set; } = new();
    public List<SavedServer> Servers { get; set; } = new();
    public List<UserCommand> UserCommands { get; set; } = UserCommand.Defaults();
    public Dictionary<string, string> Gestures { get; set; } = new();
    public Dictionary<string, string> KnownHosts { get; set; } = new();
    public SessionState Session { get; set; } = new();
    /// <summary>Volitelná cesta k programu rclone (jinak se hledá v PATH).</summary>
    public string RclonePath { get; set; } = "";
    public List<TabSet> TabSets { get; set; } = new();
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BudisCommander");
    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch { /* poškozený soubor: začít znovu s výchozím nastavením */ }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory_Create(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch { /* ukládání nastavení nesmí shodit aplikaci */ }
    }

    private static void Directory_Create(string dir) => System.IO.Directory.CreateDirectory(dir);

    public void AddRecent(string path)
    {
        Recents.RemoveAll(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase));
        Recents.Insert(0, path);
        if (Recents.Count > 25) Recents.RemoveRange(25, Recents.Count - 25);
    }

    // --- ochrana hesel přes DPAPI -------------------------------------------------

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || !OperatingSystem.IsWindows()) return null;
        try
        {
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }
        catch { return null; }
    }

    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText) || !OperatingSystem.IsWindows()) return null;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch { return null; }
    }
}
