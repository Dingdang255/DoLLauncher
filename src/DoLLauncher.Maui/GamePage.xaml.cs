using DoLLauncher.Maui.Platform;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class GamePage : ContentPage
{
    private readonly VersionInfo _version;
    private readonly bool _fileMode;
    private bool _closing;
    private bool _fileWarningShown;
    private bool _noticeShown;

    public GamePage(VersionInfo version, string url)
    {
        InitializeComponent();
        _version = version;
        Title = version.DisplayName;
        _fileMode = url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

        if (_fileMode)
        {
            // Android WebView 对 file:// 限制较多，MauiProgram 已放开文件访问；
            // 文件不存在时直接在页面上说明，避免出现一片空白。
            var exists = TryGetLocalPath(url, out var path) && File.Exists(path);
            if (exists)
            {
                GameView.Source = url;
                LogService.Append($"游戏页面使用 file:// 地址：{url}（已开启 WebView 文件访问）");
            }
            else
            {
                LogService.Error($"file:// 游戏文件不存在：{url}");
                ShowFileModeNotice("这个版本登记的 file:// 游戏文件不存在。" +
                                   "请检查版本目录是否完整，建议改为 http 版本（导入 zip 版本包或重新下载）。");
            }
        }
        else
        {
            GameView.Source = url;
        }
    }

    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success)
        {
            LogService.Error($"游戏页面加载失败：{e.Result} {e.Url}");
            if (_fileMode)
            {
                ShowFileModeNotice($"file:// 页面加载失败（{e.Result}）。" +
                                   "请把这个版本转为 http 版本后重试（导入 zip 版本包或重新下载）。");
            }
            return;
        }

        // 页面真正加载完成后重新登记：重置宽限期，让游戏初始化阶段的长时间
        // 主线程占用不会被误判成「页面已关闭」。
        if (!_fileMode) AndroidSessionWatcher.Track(_version.Folder);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BackRouter.Handler = HandleBack;

        if (_fileMode)
        {
            if (!_fileWarningShown)
            {
                _fileWarningShown = true;
                LogService.Append("file:// 版本已尝试用 WebView 打开，若页面空白请转为 http 版本。");
                _ = PlatformServices.Dialogs.WarnAsync(
                    "这个版本是 file:// 模式。Android WebView 对本地文件的限制较多，" +
                    "若页面空白或资源缺失，请转为 http 版本（导入 zip 版本包或重新下载）。",
                    "文件模式提示");
            }
            return;
        }

        // file:// 没有本地服务器，也就没有心跳，不需要登记关页检测。
        AndroidSessionWatcher.Track(_version.Folder);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BackRouter.Handler == HandleBack) BackRouter.Handler = null;
        AndroidSessionWatcher.Untrack(_version.Folder);
        // 这里不能直接停服：页面被对话框或其它页面短暂遮挡时也会触发 OnDisappearing，
        // 停服交给返回键关闭流程（CloseAsync）与关页检测处理。
    }

    /// <summary>返回键：先回退网页历史，再按关页策略退出。</summary>
    private bool HandleBack()
    {
        if (GameView.CanGoBack)
        {
            GameView.GoBack();
            return true;
        }
        if (_closing) return true;

        _closing = true;
        _ = CloseAsync();
        return true;
    }

    private async Task CloseAsync()
    {
        try
        {
            // 配置为「询问」时先确认，取消则留在游戏页面。
            if (string.Equals(AppConfig.Load().General.ClosePageBehavior, "ask", StringComparison.OrdinalIgnoreCase))
            {
                if (!await PlatformServices.Dialogs.ConfirmAsync("关闭游戏并停止本地服务器？"))
                {
                    _closing = false;
                    return;
                }
            }

            ServerManager.Stop(_version.Folder);
            LauncherStatusService.Sync();
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            _closing = false;
            LogService.Error("关闭游戏页失败", ex);
        }
    }

    private static bool TryGetLocalPath(string url, out string path)
    {
        path = string.Empty;
        try
        {
            path = new Uri(url).LocalPath;
            return path.Length > 0;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <summary>用页面内的说明替代空白 WebView，说明为什么打不开以及怎么处理。</summary>
    private void ShowFileModeNotice(string message)
    {
        if (_noticeShown) return;
        _noticeShown = true;
        GameView.IsVisible = false;
        var layout = new VerticalStackLayout
        {
            Padding = new Thickness(24),
            Spacing = 12,
            VerticalOptions = LayoutOptions.Center,
        };
        var title = new Label
        {
            Text = "无法以 file:// 方式打开游戏",
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        var detail = new Label
        {
            Text = message,
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        detail.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#606060"), Color.FromArgb("#B0B0B0"));
        layout.Children.Add(title);
        layout.Children.Add(detail);

        if (Content is Grid grid) grid.Children.Add(layout);
        else Content = layout;
    }
}
