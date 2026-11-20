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

		// 启动器根目录：外部应用目录（/storage/emulated/0/Android/data/<包名>/files/DoL），
		// 文件管理器拿到存储权限后可以直接浏览；首次升级时把旧的内部目录整体迁过去。
		var root = ResolveRoot();
		PlatformServices.Initialize(new AndroidPlatformBundle(root));
		LogService.Configure(AppConfig.Load().General.LogVerbosity);

		// 界面缩放需要在任何页面创建前生效（OnCreate 里先按默认值套过一次）。
		MainApplication.SetUiScale(AppConfig.Load().General.UiScale);
		MainApplication.ApplyUiScale();

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
				RegisterPicker(edit);
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
	// 已创建的 Picker 原生控件，切主题时逐个刷新（隐藏页面里的不在可视树遍历范围内）。
	private static readonly List<WeakReference<Android.Widget.EditText>> PickerViews = new();

	private static void RegisterPicker(Android.Widget.EditText edit)
	{
		foreach (var item in PickerViews)
		{
			if (item.TryGetTarget(out var existing) && ReferenceEquals(existing, edit)) return;
		}
		PickerViews.Add(new WeakReference<Android.Widget.EditText>(edit));
	}

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
	/// 这里刷新所有登记过的原生控件（包括仍在导航栈里的页面），避免停留在旧配色。
	/// </summary>
	internal static void RefreshPickers()
	{
		try
		{
			for (var i = PickerViews.Count - 1; i >= 0; i--)
			{
				if (!PickerViews[i].TryGetTarget(out var edit))
				{
					PickerViews.RemoveAt(i);
					continue;
				}
				try { ApplyPickerChrome(edit); }
				catch { /* 单个控件失败不影响其它控件 */ }
			}
		}
		catch (Exception ex)
		{
			LogService.Error("刷新 Picker 主题失败", ex);
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

	/// <summary>
	/// 解析启动器根目录。优先用外部媒体目录 Android/media/&lt;包名&gt;/DoL：
	/// 它不像 Android/data 那样被系统限制，文件管理器有存储权限就能直接读写；
	/// 首次升级时把内部目录或旧的 Android/data 目录整体迁移过去，失败则继续用旧目录。
	/// </summary>
	private static string ResolveRoot()
	{
		var legacyRoots = new List<string> { Path.Combine(FileSystem.AppDataDirectory, "DoL") };

#if ANDROID
		var context = Android.App.Application.Context;
#pragma warning disable CA1422 // Android/media 在 Android 11+ 依然可用，且不受 Android/data 的限制
		var mediaRoot = context.GetExternalMediaDirs()?.FirstOrDefault()?.AbsolutePath;
#pragma warning restore CA1422

		var filesRoot = context.GetExternalFilesDir(null)?.AbsolutePath;
		if (!string.IsNullOrWhiteSpace(filesRoot))
			legacyRoots.Add(Path.Combine(filesRoot, "DoL"));

		if (!string.IsNullOrWhiteSpace(mediaRoot))
		{
			var root = Path.Combine(mediaRoot, "DoL");
			if (File.Exists(Path.Combine(root, ".storage-migrated"))) return root;
			if (File.Exists(Path.Combine(root, "launcher-versions.json"))) return root;

			var legacy = legacyRoots.FirstOrDefault(Directory.Exists);
			if (legacy == null) return root;

			try
			{
				CopyDirectory(legacy, root);
				File.WriteAllText(Path.Combine(root, ".storage-migrated"),
					DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"));
				Directory.Delete(legacy, recursive: true);
				return root;
			}
			catch
			{
				// 迁移失败：继续留在旧目录，下次启动重试
				return legacy;
			}
		}
#endif
		return legacyRoots.FirstOrDefault(Directory.Exists) ?? legacyRoots[0];
	}

	private static void CopyDirectory(string source, string target)
	{
		Directory.CreateDirectory(target);
		foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(source, dir);
			Directory.CreateDirectory(Path.Combine(target, relative));
		}
		foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(source, file);
			File.Copy(file, Path.Combine(target, relative), overwrite: true);
		}
	}
}
