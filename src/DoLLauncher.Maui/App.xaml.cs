using DoLLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DoLLauncher.Maui;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		// 启动时按配置应用主题（此时 Application.Current 可能尚未就绪，直接作用于实例）
		UserAppTheme = ToTheme(AppConfig.Load().General.ThemeMode);
	}

	/// <summary>把配置里的主题模式应用到 MAUI；设置页切换时调用。</summary>
	internal static void ApplyTheme(string? mode)
	{
		if (Current != null) Current.UserAppTheme = ToTheme(mode);
	}

	private static AppTheme ToTheme(string? mode) => mode switch
	{
		"light" => AppTheme.Light,
		"dark" => AppTheme.Dark,
		_ => AppTheme.Unspecified,
	};

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new NavigationPage(new MainPage()));
		// 前后台切换只记录；应用真正退出时清掉所有本地服务器，避免残留监听
		window.Stopped += (_, _) => LogService.Append("应用进入后台");
		window.Resumed += (_, _) => LogService.Append("应用回到前台");
		window.Destroying += (_, _) =>
		{
			ServerManager.StopAll();
			LogService.Append("应用退出，本地服务器已全部停止");
		};
		return window;
	}
}
