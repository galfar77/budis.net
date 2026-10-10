using System.Reflection;

namespace BudisCommander.Core;

/// <summary>Vytváření zástupců (.lnk) na ploše a v nabídce Start; jen Windows.</summary>
public static class Shortcuts
{
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Budis Commander.lnk");
    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Budis Commander.lnk");

    public static void Create(string linkPath, string target, string description)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Zástupce lze vytvořit jen ve Windows.");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host není k dispozici.");
        var shell = Activator.CreateInstance(type)!;
        var link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath })!;
        var lt = link.GetType();
        void Set(string name, object value) => lt.InvokeMember(name, BindingFlags.SetProperty, null, link, new[] { value });
        Set("TargetPath", target);
        Set("WorkingDirectory", Path.GetDirectoryName(target) ?? "");
        Set("Description", description);
        Set("IconLocation", target + ",0");
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
    }

    /// <summary>Vytvoří zástupce na ploše a v nabídce Start pro běžící BudisCommander.exe.</summary>
    public static void CreateAll(string exePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StartMenuLink)!);
        Create(DesktopLink, exePath, "Budis Commander");
        Create(StartMenuLink, exePath, "Budis Commander");
    }
}
