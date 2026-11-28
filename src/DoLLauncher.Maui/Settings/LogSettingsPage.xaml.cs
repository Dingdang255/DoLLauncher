using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Settings;

public partial class LogSettingsPage : ContentPage
{
    private static readonly string[] VerbosityValues = { "standard", "detailed" };
    private static readonly string[] VerbosityNames = { "标准", "详细（含堆栈）" };
    private static readonly int[] KeepValues = { 5, 10, 20, 50 };
    private static readonly int[] MaxMbValues = { 0, 10, 30, 100 };
    private static readonly string[] MaxMbNames = { "无限制", "10 MB", "30 MB", "100 MB" };
    private static readonly string[] OverflowValues = { "newfile", "stop" };
    private static readonly string[] OverflowNames = { "写新文件（-2、-3…）", "停止写入" };

    private LauncherConfig _config = new();
    private bool _loading;
    private bool _uploadDirty;

    public LogSettingsPage()
    {
        InitializeComponent();
        VerbosityPicker.ItemsSource = VerbosityNames;
        KeepPicker.ItemsSource = KeepValues.Select(v => v.ToString()).ToList();
        MaxMbPicker.ItemsSource = MaxMbNames;
        OverflowPicker.ItemsSource = OverflowNames;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Load();
    }

    protected override void OnDisappearing()
    {
        if (_uploadDirty) _ = SaveUploadAsync();
        base.OnDisappearing();
    }

    private void Load()
    {
        _config = SettingsStore.Load();
        var general = _config.General;
        _loading = true;
        try
        {
            VerbosityPicker.SelectedIndex = SettingsStore.IndexOf(VerbosityValues, general.LogVerbosity, 0);
            KeepPicker.SelectedIndex = Math.Max(0, Array.IndexOf(KeepValues, general.GameLogKeep));
            MaxMbPicker.SelectedIndex = Math.Max(0, Array.IndexOf(MaxMbValues, general.GameLogMaxMb));
            OverflowPicker.SelectedIndex = SettingsStore.IndexOf(OverflowValues, general.GameLogOverflow, 0);

            QueueSlider.Value = Math.Clamp(general.LogQueueKb, 1, 64);
            FlushSlider.Value = Math.Clamp(general.LogFlushMs, 200, 5000);
            UpdateUploadTexts();
            _uploadDirty = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateUploadTexts()
    {
        QueueText.Text = (int)Math.Round(QueueSlider.Value) + " KB";
        FlushText.Text = (int)Math.Round(FlushSlider.Value) + " ms";
    }

    private async void OnVerbosityChanged(object? sender, EventArgs e)
    {
        if (_loading || VerbosityPicker.SelectedIndex < 0) return;
        var value = VerbosityValues[VerbosityPicker.SelectedIndex];
        if (SettingsStore.Same(_config.General.LogVerbosity, value)) return;

        _config.General.LogVerbosity = value;
        LogService.Configure(value);
        await SettingsStore.SaveAsync(_config, "日志详细度 = " + VerbosityNames[VerbosityPicker.SelectedIndex]);
    }

    private async void OnKeepChanged(object? sender, EventArgs e)
    {
        if (_loading || KeepPicker.SelectedIndex < 0) return;
        var value = KeepValues[KeepPicker.SelectedIndex];
        if (_config.General.GameLogKeep == value) return;

        _config.General.GameLogKeep = value;
        await SettingsStore.SaveAsync(_config, "游戏日志保留份数 = " + value);
    }

    private async void OnMaxMbChanged(object? sender, EventArgs e)
    {
        if (_loading || MaxMbPicker.SelectedIndex < 0) return;
        var value = MaxMbValues[MaxMbPicker.SelectedIndex];
        if (_config.General.GameLogMaxMb == value) return;

        _config.General.GameLogMaxMb = value;
        await SettingsStore.SaveAsync(_config, "单份游戏日志上限 = " + MaxMbNames[MaxMbPicker.SelectedIndex]);
    }

    private async void OnOverflowChanged(object? sender, EventArgs e)
    {
        if (_loading || OverflowPicker.SelectedIndex < 0) return;
        var value = OverflowValues[OverflowPicker.SelectedIndex];
        if (SettingsStore.Same(_config.General.GameLogOverflow, value)) return;

        _config.General.GameLogOverflow = value;
        await SettingsStore.SaveAsync(_config, "游戏日志超限后 = " + OverflowNames[OverflowPicker.SelectedIndex]);
    }

    private void OnUploadChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_loading) return;
        UpdateUploadTexts();
        _uploadDirty = true;
    }

    private async void OnUploadDragCompleted(object? sender, EventArgs e)
    {
        if (_loading) return;
        await SaveUploadAsync();
    }

    private async Task SaveUploadAsync()
    {
        _uploadDirty = false;
        var queue = (int)Math.Round(QueueSlider.Value);
        var flush = (int)Math.Round(FlushSlider.Value / 100.0) * 100;
        if (_config.General.LogQueueKb == queue && _config.General.LogFlushMs == flush) return;

        _config.General.LogQueueKb = queue;
        _config.General.LogFlushMs = flush;
        await SettingsStore.SaveAsync(_config, $"日志上报队列 {queue} KB / 合并间隔 {flush} ms");
    }

}
