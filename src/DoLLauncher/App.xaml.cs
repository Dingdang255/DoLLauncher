using System.IO;
using System.Windows;
using System.Windows.Threading;
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

        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            SelfTest.Run();
            Shutdown();
            return;
        }

        // 版本目录不存在时给出明确提示，而不是让界面空白
        if (!Directory.Exists(Paths.VersionsDir))
        {
            Directory.CreateDirectory(Paths.VersionsDir);
        }

        // 主题与过渡动画需要在主窗口创建前准备好
        UiAnimations.Enabled = AppConfig.Load().General.ShowAnimations;
        UiAnimations.Initialize();
        TextToolTip.Initialize();
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
        try
        {
            var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                       ex + Environment.NewLine + Environment.NewLine;
            File.AppendAllText(Path.Combine(Paths.Root, "launcher-error.log"), text);
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }
}
