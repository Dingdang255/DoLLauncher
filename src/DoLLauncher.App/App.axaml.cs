using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DoLLauncher.Platform;
using DoLLauncher.Platform.Desktop;
using DoLLauncher.Services;
using DoLLauncher.Themes;
using DoLLauncher.Views;

namespace DoLLauncher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RegisterExceptionHandlers();

        // 平台能力最先装配，后续主题与各服务都通过接口访问
        PlatformServices.Initialize(new DesktopPlatformBundle());

        var general = AppConfig.Load().General;
        LogService.Configure(general.LogVerbosity);
        ThemeManager.Initialize(general.ThemeMode);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RegisterExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            TryWriteCrashLog(e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            TryWriteCrashLog(args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryWriteCrashLog(args.Exception);
            args.SetObserved();
        };
    }

    private static void TryWriteCrashLog(Exception? ex)
    {
        if (ex == null) return;
        // launcher-error.log 始终保留完整堆栈；launcher.log 按详细度决定是否附上堆栈
        LogService.Crash(ex);
        LogService.Error("未处理异常", ex);
    }
}
