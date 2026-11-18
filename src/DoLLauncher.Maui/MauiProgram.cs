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

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
