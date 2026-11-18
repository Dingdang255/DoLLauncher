using Avalonia;

namespace DoLLauncher.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DoLLauncher.App>()
            .UsePlatformDetect()
            .LogToTrace();
}
