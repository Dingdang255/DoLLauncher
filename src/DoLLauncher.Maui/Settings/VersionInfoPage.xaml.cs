using System.Text;
using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Settings;

public partial class VersionInfoPage : ContentPage
{
    private LauncherConfig _config = new();
    private bool _loading;

    public VersionInfoPage()
    {
        InitializeComponent();
        VersionLabel.Text = BuildInfo.Display;
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
            AutoUpdateSwitch.IsToggled = _config.General.AutoCheckUpdate;
            VersionLabel.Text = BuildInfo.Display;
            CreditsLabel.Text = BuildCreditsText();
        }
        finally
        {
            _loading = false;
        }
    }

    private static string BuildCreditsText()
    {
        try
        {
            var credits = CreditsService.Load();
            if (credits.Entries.Count == 0) return "（credits.json 里还没有致谢条目）";

            var text = new StringBuilder();
            foreach (var entry in credits.Entries)
            {
                var role = (entry.Role ?? string.Empty).Trim();
                var name = (entry.Name ?? string.Empty).Trim();
                var line = role.Length == 0 ? name : $"{role}  {name}";
                text.AppendLine(line.Trim());

                var links = new[]
                {
                    entry.Bilibili, entry.Github, entry.Website,
                }.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).ToArray();
                if (links.Length > 0) text.AppendLine("  " + string.Join(" · ", links));
            }
            return text.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            LogService.Error("加载致谢名单失败", ex);
            return "致谢名单读取失败：" + ex.Message;
        }
    }

    private async void OnAutoUpdateToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        if (_config.General.AutoCheckUpdate == e.Value) return;

        _config.General.AutoCheckUpdate = e.Value;
        await SettingsStore.SaveAsync(_config, "启动时自动检测更新 = " + (e.Value ? "开" : "关"));
    }
}
