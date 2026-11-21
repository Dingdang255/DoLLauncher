using DoLLauncher.Maui.Platform;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class SaveHelperPage : ContentPage
{
    private readonly VersionInfo _version;
    private bool _closing;

    public SaveHelperPage(VersionInfo version, string url)
    {
        InitializeComponent();
        _version = version;
        Title = "存档助手 · " + version.DisplayName;

        // Android WebView 不支持 blob 下载：挂上保存桥，页面里的「导出」改走系统保存对话框。
        HelperView.HandlerChanged += (_, _) =>
            SaveHelperWebViewBridge.Attach(HelperView.Handler?.PlatformView as Android.Webkit.WebView);

        HelperView.Source = url;
    }

    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success)
            LogService.Error($"存档助手页面加载失败：{e.Result} {e.Url}");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BackRouter.Handler = HandleBack;
        AndroidSessionWatcher.Track(_version.Folder);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BackRouter.Handler == HandleBack) BackRouter.Handler = null;
        AndroidSessionWatcher.Untrack(_version.Folder);
        // 游戏页还在后台运行时不跟着停服（对应 WPF 端只关掉存档助手页、游戏窗口仍在）。
        if (!GameSurface.HasSession(_version.Folder)) ServerManager.Stop(_version.Folder);
        LauncherStatusService.Sync();
    }

    private bool HandleBack()
    {
        if (HelperView.CanGoBack)
        {
            HelperView.GoBack();
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
            // 与游戏页保持一致：配置为「询问」时先确认。
            if (string.Equals(AppConfig.Load().General.ClosePageBehavior, "ask", StringComparison.OrdinalIgnoreCase))
            {
                if (!await PlatformServices.Dialogs.ConfirmAsync("关闭存档助手并停止本地服务器？"))
                {
                    _closing = false;
                    return;
                }
            }

            ServerManager.Stop(_version.Folder);
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            _closing = false;
            LogService.Error("关闭存档助手失败", ex);
        }
    }
}
