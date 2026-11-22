using DoLLauncher.Models;

namespace DoLLauncher.Maui.Settings;

public partial class DataIndexSettingsPage : ContentPage
{
    private static readonly string[] MarketValues = { "auto", "primary", "backup" };
    private static readonly string[] MarketNames =
        { "自动（主源失败用备用）", "主源 dol.alseece.top", "备用源 workers.dev" };

    // Android 端只提供「同时拉取 / 不预取」，自定义顺序是桌面端专属
    private static readonly string[] PrefetchValues = { "parallel", "none" };
    private static readonly string[] PrefetchNames = { "同时拉取（推荐）", "启动时不预取" };

    private LauncherConfig _config = new();
    private bool _loading;

    public DataIndexSettingsPage()
    {
        InitializeComponent();
        MarketSourcePicker.ItemsSource = MarketNames;
        PrefetchPicker.ItemsSource = PrefetchNames;
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
            MarketSourcePicker.SelectedIndex =
                SettingsStore.IndexOf(MarketValues, _config.General.MarketSource, 0);
            PrefetchPicker.SelectedIndex =
                SettingsStore.IndexOf(PrefetchValues, _config.General.Prefetch, 0);
            WriteMetaSwitch.IsToggled = _config.General.WriteModMetadata;
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnMarketSourceChanged(object? sender, EventArgs e)
    {
        if (_loading || MarketSourcePicker.SelectedIndex < 0) return;
        var value = MarketValues[MarketSourcePicker.SelectedIndex];
        if (SettingsStore.Same(_config.General.MarketSource, value)) return;

        _config.General.MarketSource = value;
        await SettingsStore.SaveAsync(_config, "模组市场索引源 = " + MarketNames[MarketSourcePicker.SelectedIndex]);
    }

    private async void OnPrefetchChanged(object? sender, EventArgs e)
    {
        if (_loading || PrefetchPicker.SelectedIndex < 0) return;
        var value = PrefetchValues[PrefetchPicker.SelectedIndex];
        if (SettingsStore.Same(_config.General.Prefetch, value)) return;

        _config.General.Prefetch = value;
        await SettingsStore.SaveAsync(_config, "启动时预取索引 = " + PrefetchNames[PrefetchPicker.SelectedIndex]);
    }

    private async void OnWriteMetaToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        if (_config.General.WriteModMetadata == e.Value) return;

        _config.General.WriteModMetadata = e.Value;
        await SettingsStore.SaveAsync(_config, "补全信息写回模组包 = " + (e.Value ? "开" : "关"));
    }
}
