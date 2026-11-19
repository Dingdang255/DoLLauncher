using DoLLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DoLLauncher.Maui;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		// 启动时按配置应用主题（此时 Application.Current 可能尚未就绪，直接作用于实例）
		var general = AppConfig.Load().General;
		UserAppTheme = ToTheme(general.ThemeMode);
		Settings.ThemeAccent.Apply(Resources, general.ThemeAccentColor, general.ThemeAccentSaturation);

		// 跟随系统时，Android 亮 / 暗切换需要重新派生强调色
		RequestedThemeChanged += (_, _) =>
		{
			if (UserAppTheme == AppTheme.Unspecified) Settings.ThemeAccent.Apply();
		};
	}

	/// <summary>把配置里的主题模式应用到 MAUI；设置页切换时调用。</summary>
	internal static void ApplyTheme(string? mode)
	{
		if (Current != null) Current.UserAppTheme = ToTheme(mode);
		Settings.ThemeAccent.Apply();
	}

	private static AppTheme ToTheme(string? mode) => mode switch
	{
		"light" => AppTheme.Light,
		"dark" => AppTheme.Dark,
		_ => AppTheme.Unspecified,
	};

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// 此时 Application.Current 已就绪，按实际深浅色再校准一次强调色
		Settings.ThemeAccent.Apply();

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
