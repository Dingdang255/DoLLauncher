using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class GamePage : ContentPage
{
    private readonly VersionInfo _version;
    private bool _closing;

    public GamePage(VersionInfo version, string url)
    {
        InitializeComponent();
        _version = version;
        Title = version.DisplayName;
        GameView.Source = url;
    }

    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success)
            LogService.Error($"游戏页面加载失败：{e.Result} {e.Url}");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BackRouter.Handler = HandleBack;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BackRouter.Handler == HandleBack) BackRouter.Handler = null;
        // 从工具栏返回箭头退出时也要停服，Stop 可重复调用
        ServerManager.Stop(_version.Folder);
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
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            _closing = false;
            LogService.Error("关闭游戏页失败", ex);
        }
    }
}
