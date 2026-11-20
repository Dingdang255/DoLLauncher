using DoLLauncher.Maui.Platform;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class GamePage : ContentPage
{
    private readonly VersionInfo _version;
    private readonly bool _fileMode;
    private readonly string _sourceUrl;
    private bool _closing;
    private bool _needsRecreate;
    private View? _errorLayer;

    public GamePage(VersionInfo version, string url)
    {
        InitializeComponent();
        _version = version;
        _sourceUrl = url;
        Title = version.DisplayName;
        _fileMode = url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BackRouter.Handler = HandleBack;

        // 游戏默认竖屏并允许旋转，返回启动器时恢复横屏锁定
        AppOrientation.EnterGame();
        // 收起状态栏：竖屏时它会盖住左侧栏顶部的收起/展开按钮
        GameSystemBars.EnterGame();

        EnterSurface();
        // 游戏页不参与心跳关页检测：WebView 就在本进程里，存活状态由渲染进程回调直接告知；
        // 而游戏初始化会长时间占用主线程，心跳脚本跟不上，按心跳判定会误判成「页面已关闭」。
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BackRouter.Handler == HandleBack) BackRouter.Handler = null;
        AppOrientation.ExitGame();
        if (GameFullscreen.IsActive) GameFullscreen.Hide();
        // 回到启动器时恢复正常状态栏
        GameSystemBars.ExitGame();

        // 关键：只隐藏画面，不销毁 WebView。回到启动器后游戏仍在运行，
        // 再次进入还是同一局（对应 WPF 端游戏页在独立浏览器窗口里常驻）。
        GameSurface.Hide();
    }

    /// <summary>把常驻 WebView 挂到窗口并回调到当前页面；同版本直接复用已有会话。</summary>
    private void EnterSurface()
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity == null)
        {
            ShowErrorLayer("无法打开游戏", "启动器窗口尚未就绪，请返回后重试。", null);
            return;
        }

        var url = _sourceUrl;
        if (_fileMode)
        {
            if (!TryGetLocalPath(url, out var path) || !File.Exists(path))
            {
                LogService.Error($"file:// 游戏文件不存在：{url}");
                ShowErrorLayer("无法打开游戏文件",
                    "这个版本登记的 file:// 游戏文件不存在。" +
                    "请检查版本目录是否完整，建议改为 http 版本（导入 zip 版本包或重新下载）。", null);
                return;
            }

            var mapped = GameAssetHost.MapFileUrl(url);
            if (mapped != null)
            {
                url = mapped;
                LogService.Append($"游戏页面使用本地资源映射：{mapped}（原始：{url}）");
            }
            else
            {
                LogService.Append($"游戏页面使用 file:// 地址：{_sourceUrl}（资源映射失败，可能空白）");
            }
        }

        GameSurface.OpenExternal = u => _ = OpenExternalAsync(u);
        GameSurface.Error = message => ShowErrorLayer("游戏页面加载失败",
            message + (_fileMode ? "\n若为 file:// 版本，建议转为 http 版本后再试。" : string.Empty),
            _sourceUrl);
        GameSurface.Ready = OnPageReady;
        GameSurface.CloseRequested = () => _ = CloseAsync();

        try
        {
            var reused = GameSurface.Enter(activity, _version.Folder, url, _fileMode);
            if (reused) LogService.Append($"复用后台运行中的游戏页面：{_version.DisplayName}");
            HideErrorLayer();
        }
        catch (Exception ex)
        {
            LogService.Error("打开游戏页面失败", ex);
            ShowErrorLayer("无法打开游戏", ex.Message, _sourceUrl);
        }
    }

    private void OnPageReady()
    {
        HideErrorLayer();
        _needsRecreate = false;
    }

    /// <summary>返回键：先退出全屏，再回退网页历史，最后回到启动器并让游戏继续在后台运行。</summary>
    private bool HandleBack()
    {
        if (GameFullscreen.IsActive)
        {
            GameFullscreen.Hide();
            return true;
        }
        if (GameSurface.View?.CanGoBack() == true)
        {
            GameSurface.View.GoBack();
            return true;
        }
        if (_closing) return true;

        _closing = true;
        _ = ExitToLauncherAsync();
        return true;
    }

    /// <summary>返回启动器：仅隐藏画面，不停服务器、不销毁 WebView。</summary>
    private async Task ExitToLauncherAsync()
    {
        try
        {
            GameSurface.Hide();
            if (Navigation.NavigationStack.Count > 1) await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("返回启动器失败", ex);
        }
        finally
        {
            _closing = false;
        }
    }

    /// <summary>页面自己请求关闭游戏（DoL.close）：真正停服并销毁游戏会话。</summary>
    private async Task CloseAsync()
    {
        try
        {
            if (!await PlatformServices.Dialogs.ConfirmAsync("关闭游戏并停止本地服务器？", "关闭游戏", "关闭"))
            {
                _closing = false;
                return;
            }

            GameSurface.DisposeSession();
            ServerManager.Stop(_version.Folder);
            LauncherStatusService.Sync();
            if (Navigation.NavigationStack.Count > 1) await Navigation.PopAsync();
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

    private static async Task OpenExternalAsync(string url)
    {
        try
        {
            await Launcher.Default.OpenAsync(url);
        }
        catch (Exception ex)
        {
            LogService.Error($"打开外部链接失败：{url}", ex);
        }
    }

    /// <summary>用页面内的错误层替代黑屏 WebView，提供重试与浏览器打开。</summary>
    private void ShowErrorLayer(string title, string message, string? openUrl)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _needsRecreate = message.Contains("渲染进程", StringComparison.Ordinal);

            // 先藏起覆盖层，否则错误提示会被 WebView 挡住。
            GameSurface.Hide();
            HideErrorLayer();

            var layout = new VerticalStackLayout
            {
                Padding = new Thickness(24),
                Spacing = 12,
                VerticalOptions = LayoutOptions.Center,
            };

            var titleLabel = new Label
            {
                Text = title,
                FontSize = 18,
                FontAttributes = FontAttributes.Bold,
                HorizontalTextAlignment = TextAlignment.Center,
            };
            titleLabel.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#212121"), Color.FromArgb("#E8E8E8"));

            var detail = new Label
            {
                Text = message,
                FontSize = 13,
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.CharacterWrap,
            };
            detail.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#606060"), Color.FromArgb("#B0B0B0"));

            var buttons = new HorizontalStackLayout
            {
                Spacing = 10,
                HorizontalOptions = LayoutOptions.Center,
            };

            var retry = CreateButton("重试");
            retry.Clicked += (_, _) =>
            {
                if (_needsRecreate)
                {
                    // 渲染进程已退出后 Reload 无法恢复，必须销毁旧 WebView 再新建
                    GameSurface.DisposeSession();
                    EnterSurface();
                    return;
                }

                HideErrorLayer();
                GameSurface.Show();
                GameSurface.View?.Reload();
            };
            buttons.Children.Add(retry);

            if (!string.IsNullOrWhiteSpace(openUrl) &&
                (openUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                 openUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
            {
                var open = CreateButton("用浏览器打开");
                open.Clicked += (_, _) => PlatformServices.Links.OpenUrl(openUrl);
                buttons.Children.Add(open);
            }

            layout.Children.Add(titleLabel);
            layout.Children.Add(detail);
            layout.Children.Add(buttons);

            _errorLayer = layout;
            RootGrid.Children.Add(layout);
        });
    }

    private static Button CreateButton(string text)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 13,
            Padding = new Thickness(14, 6),
            CornerRadius = 8,
            HeightRequest = 40,
        };
        button.SetDynamicResource(Button.BackgroundColorProperty, "AccentColor");
        button.SetDynamicResource(Button.TextColorProperty, "AccentOnColor");
        return button;
    }

    private void HideErrorLayer()
    {
        if (_errorLayer == null) return;
        RootGrid.Children.Remove(_errorLayer);
        _errorLayer = null;
    }
}
