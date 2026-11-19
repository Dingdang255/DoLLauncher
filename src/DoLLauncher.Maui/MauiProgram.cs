using System.IO;
using System.Reflection;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Extensions.Logging;

namespace DoLLauncher.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// Android 没有托管入口程序集，把本程序集里构建注入的版本信息交给 Core。
		BuildInfo.Override(typeof(MauiProgram).Assembly
			.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion);

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				// 图标字体（Fluent System Icons，MIT），用 IconGlyph 里的码位显示
				fonts.AddFont("FluentSystemIcons-Regular.ttf", "FluentIcons");
			});

		// 启动器根目录：应用私有目录下的 DoL，目录结构与 Windows 保持一致
		var root = Path.Combine(FileSystem.AppDataDirectory, "DoL");
		PlatformServices.Initialize(new AndroidPlatformBundle(root));
		LogService.Configure(AppConfig.Load().General.LogVerbosity);
		LogService.Append("DoL 启动器（Android）启动，根目录：" + Paths.Root);

		// 关页检测：订阅 SessionLost，按设置决定是否停服
		AndroidSessionWatcher.Initialize();

#if ANDROID
		// 所有 Picker 统一成 WPF 风格：圆角底色 + 描边 + 右侧下拉箭头（替换 Android 默认下划线样式）
		Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("DoLPickerChrome", (handler, _) =>
		{
			if (handler.PlatformView is not Android.Widget.EditText edit) return;
			try
			{
				ApplyPickerChrome(edit);
			}
			catch (Exception ex)
			{
				LogService.Error("配置 Picker 外观失败", ex);
			}
		});

		// file:// 版本：Android WebView 默认禁止本地文件访问，这里放开后再尝试加载，
		// 若仍受限制（例如目录与资源路径不符），游戏页会给出「请转为 http 版本」的提示。
		Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("DoLFileAccess", (handler, _) =>
		{
			try
			{
				var settings = handler.PlatformView.Settings;
				settings.AllowContentAccess = true;
				settings.AllowFileAccess = true;
				settings.AllowFileAccessFromFileURLs = true;
				settings.AllowUniversalAccessFromFileURLs = true;
			}
			catch (Exception ex)
			{
				LogService.Error("配置 WebView 文件访问失败", ex);
			}
		});
#endif

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

#if ANDROID
	/// <summary>按当前系统深浅色重新给一个 Picker 刷底色、描边与箭头。</summary>
	internal static void ApplyPickerChrome(Android.Widget.EditText edit)
	{
		var ctx = edit.Context!;
		var res = ctx.Resources!;
		var dark = (res.Configuration!.UiMode & Android.Content.Res.UiMode.NightMask)
			== Android.Content.Res.UiMode.NightYes;
		var density = res.DisplayMetrics?.Density ?? 1f;

		var background = new Android.Graphics.Drawables.GradientDrawable();
		background.SetCornerRadius(8 * density);
		background.SetColor(Android.Graphics.Color.ParseColor(dark ? "#2A2A2A" : "#F2F2F2"));
		background.SetStroke((int)Math.Max(1, density),
			Android.Graphics.Color.ParseColor(dark ? "#4A4A4A" : "#DCDCDC"));
		edit.Background = background;

		var padH = (int)(10 * density);
		var padV = (int)(8 * density);
		edit.SetPadding(padH, padV, padH, padV);
		edit.SetTextColor(Android.Graphics.Color.ParseColor(dark ? "#E8E8E8" : "#333333"));

		var chevron = CreateChevron(ctx, dark);
		if (chevron != null)
		{
			edit.SetCompoundDrawablesRelativeWithIntrinsicBounds(null, null, chevron, null);
			edit.CompoundDrawablePadding = (int)(6 * density);
		}
	}

	/// <summary>
	/// 应用内切换深浅色后，已创建的 Picker 不会自动重跑映射，
	/// 这里遍历当前窗口的可视树逐个刷新，避免停留在旧配色。
	/// </summary>
	internal static void RefreshPickers()
	{
		try
		{
			var windows = Application.Current?.Windows;
			if (windows == null) return;
			foreach (var window in windows)
			{
				if (window.Page is IVisualTreeElement root) RefreshNode(root);
			}
		}
		catch (Exception ex)
		{
			LogService.Error("刷新 Picker 主题失败", ex);
		}

		static void RefreshNode(IVisualTreeElement node)
		{
			if (node is Picker picker)
			{
				try { picker.Handler?.UpdateValue("DoLPickerChrome"); }
				catch { /* 单个控件失败不影响其它控件 */ }
			}
			foreach (var child in node.GetVisualChildren())
				RefreshNode(child);
		}
	}

	/// <summary>画一个与 WPF 下拉框一致的小箭头。</summary>
	private static Android.Graphics.Drawables.Drawable? CreateChevron(Android.Content.Context ctx, bool dark)
	{
		try
		{
			var density = ctx.Resources!.DisplayMetrics!.Density;
			var w = (int)(9 * density);
			var h = (int)(5 * density);
			var bitmap = Android.Graphics.Bitmap.CreateBitmap(w * 2, Math.Max(h, 1),
				Android.Graphics.Bitmap.Config.Argb8888!);
			if (bitmap == null) return null;

			using var canvas = new Android.Graphics.Canvas(bitmap);
			using var paint = new Android.Graphics.Paint
			{
				AntiAlias = true,
				Color = Android.Graphics.Color.ParseColor(dark ? "#A0A0A0" : "#707070"),
			};
			paint.SetStyle(Android.Graphics.Paint.Style.Stroke);
			paint.StrokeWidth = 1.6f * density;
			paint.StrokeJoin = Android.Graphics.Paint.Join.Round;
			using var path = new Android.Graphics.Path();
			path.MoveTo(0, 0);
			path.LineTo(w, h);
			path.LineTo(w * 2, 0);
			canvas.DrawPath(path, paint);

			return new Android.Graphics.Drawables.BitmapDrawable(ctx.Resources, bitmap);
		}
		catch
		{
			return null;
		}
	}
#endif
}
