using DoLLauncher.Maui.Platform;
using DoLLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DoLLauncher.Maui;

public partial class App : Application
{
	private IDispatcherTimer? _statusTimer;

	public App()
	{
		InitializeComponent();
		// 启动时按配置应用主题（此时 Application.Current 可能尚未就绪，直接作用于实例）
		var general = AppConfig.Load().General;
		UserAppTheme = ToTheme(general.ThemeMode);
		Settings.ThemeAccent.Apply(Resources, general.ThemeAccentColor, general.ThemeAccentSaturation);

		// 恢复上次启用的 UI 美化包（主色 / 页面背景）
		AndroidSkinStore.Initialize(this, Resources);

		// 跟随系统时，Android 亮 / 暗切换需要重新派生强调色
		RequestedThemeChanged += (_, _) =>
		{
			if (UserAppTheme == AppTheme.Unspecified)
			{
				Settings.ThemeAccent.Apply();
				AndroidSkinStore.Refresh();
				RefreshPickerTheme();
			}
		};
	}

	/// <summary>把配置里的主题模式应用到 MAUI；设置页切换时调用。</summary>
	internal static void ApplyTheme(string? mode)
	{
		if (Current != null) Current.UserAppTheme = ToTheme(mode);
		Settings.ThemeAccent.Apply();
		// 美化包的主色 / 深色背景优先于用户设置
		AndroidSkinStore.Refresh();
		MainApplication.ReapplyUiScale();
		RefreshPickerTheme();
		MainActivity.ApplySystemBars();
	}

	/// <summary>等平台把深浅色配置刷新完，再统一重刷 Picker 的原生底色。</summary>
	private static void RefreshPickerTheme()
	{
#if ANDROID
		MauiProgram.RefreshPickers();
#endif
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
		AndroidSkinStore.Refresh();

		var navigation = new NavigationPage(new MainPage());
		// 美化包样式需要覆盖被 Push 的页面；页面内动态生成的控件由 styler 的 DescendantAdded 兜底
		navigation.Pushed += (_, e) =>
			MainThread.BeginInvokeOnMainThread(() => MauiSkinStyler.Attach(e.Page));
		navigation.Popped += (_, _) =>
			MainThread.BeginInvokeOnMainThread(() => MauiSkinStyler.ApplyAll());

		var window = new Window(navigation);
		// 启动后后台检查更新（AutoCheckUpdate 开启且今日未跳过时）
		window.Created += (_, _) =>
		{
			MauiSkinStyler.Attach(navigation.CurrentPage);
			MauiSkinStyler.ApplyAll();
			UpdateDialogPage.StartAutoCheck(window);
			StartStatusTimer();
		};
		// 前后台切换只记录；应用真正退出时清掉所有本地服务器，避免残留监听
		window.Stopped += (_, _) =>
		{
			LogService.Append("应用进入后台");
			// 后台 WebView 心跳会被系统暂停，暂停关页检测避免误杀；服务器本身保持运行
			AndroidSessionWatcher.OnAppBackgrounded();
			LauncherStatusService.Sync();
		};
		window.Resumed += (_, _) =>
		{
			LogService.Append("应用回到前台");
			AndroidSessionWatcher.OnAppForegrounded();
			LauncherStatusService.Sync();
		};
		window.Destroying += (_, _) =>
		{
			DoLLauncher.Maui.Platform.GameSurface.DisposeSession();
			ServerManager.StopAll();
			LauncherStatusService.Sync();
			LogService.Append("应用退出，本地服务器已全部停止");
		};
		return window;
	}

	/// <summary>
	/// 兜底定时同步运行状态通知：有些入口（如备份页）直接启动服务器，
	/// 这里每 8 秒对齐一次「有运行就启动服务，没有就停止」。
	/// </summary>
	private void StartStatusTimer()
	{
		try
		{
			_statusTimer = Dispatcher.CreateTimer();
			_statusTimer.Interval = TimeSpan.FromSeconds(8);
			_statusTimer.Tick += (_, _) => LauncherStatusService.Sync();
			_statusTimer.Start();
		}
		catch (Exception ex)
		{
			LogService.Error("启动运行状态同步定时器失败", ex);
		}
	}
}
