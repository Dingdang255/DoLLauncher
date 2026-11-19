using System.IO;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Extensions.Logging;

namespace DoLLauncher.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// 启动器根目录：应用私有目录下的 DoL，目录结构与 Windows 保持一致
		var root = Path.Combine(FileSystem.AppDataDirectory, "DoL");
		PlatformServices.Initialize(new AndroidPlatformBundle(root));
		LogService.Configure(AppConfig.Load().General.LogVerbosity);
		LogService.Append("DoL 启动器（Android）启动，根目录：" + Paths.Root);

		// 全局异常钩子：未捕获异常与未观察的任务异常都写入 launcher-error.log，避免静默丢失
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			LogService.Crash(e.ExceptionObject as Exception
				?? new Exception("未处理异常：" + e.ExceptionObject));
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			LogService.Crash(e.Exception);
			e.SetObserved();
		};

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
