using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Settings;

public partial class AboutPage : ContentPage
{
    private string _projectUrl = string.Empty;

    public AboutPage()
    {
        InitializeComponent();
        AppVersionLabel.Text = "v" + AppInfo.Current.VersionString;
        RootLabel.Text = Paths.Root;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadLinks();
    }

    private void LoadLinks()
    {
        try
        {
            _projectUrl = (CreditsService.Load().ProjectUrl ?? string.Empty).Trim();
        }
        catch (Exception ex)
        {
            LogService.Error("读取项目主页失败", ex);
            _projectUrl = string.Empty;
        }

        var hasUrl = _projectUrl.Length > 0;
        ProjectHomeButton.IsEnabled = hasUrl;
        FeedbackButton.IsEnabled = hasUrl;
        LinkStatusLabel.Text = hasUrl
            ? "项目主页：" + _projectUrl
            : "credits.json 里还没有填写项目主页地址。";
    }

    private async void OnCopyRoot(object? sender, EventArgs e)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(Paths.Root);
            await PlatformServices.Dialogs.InfoAsync("应用根目录已复制到剪贴板。", "关于");
        }
        catch (Exception ex)
        {
            LogService.Error("复制应用根目录失败", ex);
        }
    }

    private async void OnProjectHome(object? sender, EventArgs e) => await OpenAsync(_projectUrl);

    private async void OnFeedback(object? sender, EventArgs e)
    {
        var home = _projectUrl.TrimEnd('/');
        await OpenAsync(home.Length == 0 ? string.Empty : home + "/issues/new/choose");
    }

    private static async Task OpenAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            await Launcher.Default.OpenAsync(url);
        }
        catch (Exception ex)
        {
            LogService.Error("打开链接失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开链接：" + ex.Message);
        }
    }
}
