using DoLLauncher.Models;

namespace DoLLauncher.Maui.Settings;

public partial class GameSettingsPage : ContentPage
{
    private static readonly string[] CloseValues = { "close", "ask" };
    private static readonly string[] CloseNames = { "直接关闭本地服务器", "询问是否保留后台服务" };

    private LauncherConfig _config = new();
    private bool _loading;
    /// <summary>滑块已改动但还没落盘的参数，离开页面时兜底保存。</summary>
    private bool _timingDirty;

    public GameSettingsPage()
    {
        InitializeComponent();
        ClosePicker.ItemsSource = CloseNames;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Load();
    }

    protected override void OnDisappearing()
    {
        if (_timingDirty) _ = SaveTimingAsync();
        base.OnDisappearing();
    }

    private void Load()
    {
        _config = SettingsStore.Load();
        _loading = true;
        try
        {
            ClosePicker.SelectedIndex =
                SettingsStore.IndexOf(CloseValues, _config.General.ClosePageBehavior, 0);

            HeartbeatSlider.Value = Math.Clamp(_config.General.HeartbeatSeconds, 1, 30);
            TimeoutSlider.Value = Math.Clamp(_config.General.HeartbeatTimeoutSeconds, 5, 180);
            GraceSlider.Value = Math.Clamp(_config.General.FirstHeartbeatGraceSeconds, 5, 600);
            DebounceSlider.Value = Math.Clamp(_config.General.CloseDebounceSeconds, 0, 60);
            UpdateTexts();
            _timingDirty = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateTexts()
    {
        HeartbeatText.Text = (int)Math.Round(HeartbeatSlider.Value) + " 秒";
        TimeoutText.Text = (int)Math.Round(TimeoutSlider.Value) + " 秒";
        GraceText.Text = (int)Math.Round(GraceSlider.Value) + " 秒";
        DebounceText.Text = (int)Math.Round(DebounceSlider.Value) + " 秒";
    }

    private void OnTimingChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_loading) return;
        UpdateTexts();
        _timingDirty = true;
    }

    private async void OnTimingDragCompleted(object? sender, EventArgs e)
    {
        if (_loading) return;
        await SaveTimingAsync();
    }

    private async Task SaveTimingAsync()
    {
        _timingDirty = false;
        var heartbeat = (int)Math.Round(HeartbeatSlider.Value);
        var timeout = (int)Math.Round(TimeoutSlider.Value);
        var grace = (int)Math.Round(GraceSlider.Value);
        var debounce = (int)Math.Round(DebounceSlider.Value);
        if (_config.General.HeartbeatSeconds == heartbeat &&
            _config.General.HeartbeatTimeoutSeconds == timeout &&
            _config.General.FirstHeartbeatGraceSeconds == grace &&
            _config.General.CloseDebounceSeconds == debounce) return;

        _config.General.HeartbeatSeconds = heartbeat;
        _config.General.HeartbeatTimeoutSeconds = timeout;
        _config.General.FirstHeartbeatGraceSeconds = grace;
        _config.General.CloseDebounceSeconds = debounce;
        await SettingsStore.SaveAsync(_config,
            $"关页检测 心跳 {heartbeat}s / 超时 {timeout}s / 宽限 {grace}s / 防抖 {debounce}s");
    }

    private async void OnCloseChanged(object? sender, EventArgs e)
    {
        if (_loading || ClosePicker.SelectedIndex < 0) return;
        var value = CloseValues[ClosePicker.SelectedIndex];
        if (SettingsStore.Same(_config.General.ClosePageBehavior, value)) return;

        _config.General.ClosePageBehavior = value;
        await SettingsStore.SaveAsync(_config, "关闭游戏页面时 = " + CloseNames[ClosePicker.SelectedIndex]);
    }
}
