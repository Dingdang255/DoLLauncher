using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Settings;

public partial class ProblemSettingsPage : ContentPage
{
    private static readonly string[] GapNames = { "相差 1 段（默认）", "相差 2 段", "相差 3 段", "相差 4 段" };

    private LauncherConfig _config = new();
    private bool _loading;

    public ProblemSettingsPage()
    {
        InitializeComponent();
        GapPicker.ItemsSource = GapNames;
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
            var threshold = ProblemService.NormalizeThreshold(_config.General.VersionGapThreshold);
            GapPicker.SelectedIndex = Math.Clamp(threshold, 1, 4) - 1;
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnGapChanged(object? sender, EventArgs e)
    {
        if (_loading || GapPicker.SelectedIndex < 0) return;
        var threshold = GapPicker.SelectedIndex + 1;
        if (_config.General.VersionGapThreshold == threshold) return;

        _config.General.VersionGapThreshold = threshold;
        await SettingsStore.SaveAsync(_config, "游戏版本差异告警阈值 = " + threshold);
    }
}
