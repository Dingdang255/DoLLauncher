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
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (BackRouter.Handler == HandleBack) BackRouter.Handler = null;
        ServerManager.Stop(_version.Folder);
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
