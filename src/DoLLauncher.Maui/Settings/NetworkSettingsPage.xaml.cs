using DoLLauncher.Models;

namespace DoLLauncher.Maui.Settings;

public partial class NetworkSettingsPage : ContentPage
{
    private static readonly string[] DownloadValues =
        { "auto", "ghproxy", "ghproxycom", "llkk", "fastgit", "ddlc", "boki", "ghproxynet", "worker", "github" };

    private static readonly string[] DownloadNames =
    {
        "自动（测速选最快）", "GH-Proxy 镜像", "GH-Proxy.com 镜像", "LLKK 镜像", "FastGit 镜像",
        "DDLC 镜像", "Boki 镜像", "GHProxy.net 镜像", "ModHub 加速", "GitHub 直连",
    };

    private static readonly string[] ApiValues = { "auto", "github", "ghproxy" };
    private static readonly string[] ApiNames = { "自动（直连失败走镜像）", "GitHub 直连", "GH-Proxy 镜像" };
    private static readonly string[] GameValues = { "i18n", "neo" };
    private static readonly string[] GameNames = { "汉化整包 i18n", "Neo 单文件" };

    private LauncherConfig _config = new();
    private bool _loading;

    public NetworkSettingsPage()
    {
        InitializeComponent();
        ModDownloadPicker.ItemsSource = DownloadNames;
        ApiSourcePicker.ItemsSource = ApiNames;
        GameSourcePicker.ItemsSource = GameNames;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Load();
    }

    private void Load()
    {
        _config = SettingsStore.Load();
        _loading = true;
        try
        {
            ModDownloadPicker.SelectedIndex =
                SettingsStore.IndexOf(DownloadValues, _config.General.ModDownloadSource, 0);
            ApiSourcePicker.SelectedIndex =
                SettingsStore.IndexOf(ApiValues, _config.General.GithubApiSource, 0);
            GameSourcePicker.SelectedIndex =
                SettingsStore.IndexOf(GameValues, _config.General.GameSource, 0);
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnModDownloadChanged(object? sender, EventArgs e)
    {
        if (_loading || ModDownloadPicker.SelectedIndex < 0) return;
        var value = DownloadValues[ModDownloadPicker.SelectedIndex];
        if (SettingsStore.Same(_config.General.ModDownloadSource, value)) return;

        _config.General.ModDownloadSource = value;
        await SettingsStore.SaveAsync(_config, "模组下载源 = " + DownloadNames[ModDownloadPicker.SelectedIndex]);
    }

    private async void OnApiSourceChanged(object? sender, EventArgs e)
    {
        if (_loading || ApiSourcePicker.SelectedIndex < 0) return;
        var value = ApiValues[ApiSourcePicker.SelectedIndex];
        if (SettingsStore.Same(_config.General.GithubApiSource, value)) return;

        _config.General.GithubApiSource = value;
        await SettingsStore.SaveAsync(_config, "GitHub 接口源 = " + ApiNames[ApiSourcePicker.SelectedIndex]);
    }

    private async void OnGameSourceChanged(object? sender, EventArgs e)
    {
        if (_loading || GameSourcePicker.SelectedIndex < 0) return;
        var value = GameValues[GameSourcePicker.SelectedIndex];
        if (SettingsStore.Same(_config.General.GameSource, value)) return;

        _config.General.GameSource = value;
        await SettingsStore.SaveAsync(_config, "游戏本体下载源 = " + GameNames[GameSourcePicker.SelectedIndex]);
    }
}
