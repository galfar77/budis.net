using Avalonia;

namespace BudisCommander;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Core.Encodings.Init();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
