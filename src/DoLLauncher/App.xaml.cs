using System.IO;
using System.Windows;
using System.Windows.Threading;
using DoLLauncher.Platform;
using DoLLauncher.Platform.Windows;
using DoLLauncher.Services;

namespace DoLLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            TryWriteCrashLog(ex);
        };
        // async / 后台任务的异常不会走上面两个事件，这里兜底，避免静默丢失
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryWriteCrashLog(args.Exception);
            args.SetObserved();
        };

        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            PlatformServices.Initialize(new WindowsPlatformBundle());
            SelfTest.Run();
            Shutdown();
            return;
        }

        // 版本目录不存在时给出明确提示，而不是让界面空白
        if (!Directory.Exists(Paths.VersionsDir))
        {
            Directory.CreateDirectory(Paths.VersionsDir);
        }

        // 平台能力最先装配，后续主题、美化包与各服务都通过接口访问；未装配时访问会抛明确异常
        PlatformServices.Initialize(new WindowsPlatformBundle());

        // 主题与过渡动画需要在主窗口创建前准备好
        var general = AppConfig.Load().General;
        UiAnimations.Enabled = general.ShowAnimations;
        LogService.Configure(general.LogVerbosity);
        UiAnimations.Initialize();
        TextToolTip.Initialize();
        // 美化包需要先于主题初始化，才能让包内主色参与调色板计算
        SkinService.Initialize();
        ThemeService.Initialize();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryWriteCrashLog(e.Exception);
        AppDialog.Error(
            "启动器遇到未处理的错误：\n\n" + e.Exception.Message +
            "\n\n详细信息已写入 launcher-error.log。");
        e.Handled = true;
    }

    private static void TryWriteCrashLog(Exception? ex)
    {
        if (ex == null) return;
        // launcher-error.log 始终保留完整堆栈；launcher.log 按详细度决定是否附上堆栈
        LogService.Crash(ex);
        LogService.Error("未处理异常", ex);
    }
}
