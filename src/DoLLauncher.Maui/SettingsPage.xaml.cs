using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class SettingsPage : ContentPage
{
    private static readonly string[] ThemeValues = { "system", "light", "dark" };
    private static readonly string[] LogValues = { "standard", "detailed" };
    private static readonly string[] CloseValues = { "close", "ask" };
    private static readonly string[] SourceValues = { "i18n", "neo" };

    private LauncherConfig _config = new();
    /// <summary>程序化设置选中项时置位，避免触发保存。</summary>
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        ThemePicker.ItemsSource = new[] { "跟随系统", "亮色", "深色" };
        LogPicker.ItemsSource = new[] { "标准", "详细" };
        ClosePicker.ItemsSource = new[] { "直接关闭", "询问确认" };
        SourcePicker.ItemsSource = new[] { "汉化整包 i18n", "Neo 单文件" };

        RootLabel.Text = Paths.Root;
        VersionLabel.Text = AppInfo.Current.VersionString;

        Load();
    }

    /// <summary>从配置刷新界面，并同步主题与日志详细度。</summary>
    private void Load()
    {
        _config = AppConfig.Load();
        _loading = true;
        try
        {
            ThemePicker.SelectedIndex = IndexOf(ThemeValues, _config.General.ThemeMode, 0);
            LogPicker.SelectedIndex = IndexOf(LogValues, _config.General.LogVerbosity, 0);
            ClosePicker.SelectedIndex = IndexOf(CloseValues, _config.General.ClosePageBehavior, 0);
            SourcePicker.SelectedIndex = IndexOf(SourceValues, _config.General.GameSource, 0);
        }
        finally
        {
            _loading = false;
        }

        App.ApplyTheme(_config.General.ThemeMode);
        LogService.Configure(_config.General.LogVerbosity);
    }

    private async void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_loading || ThemePicker.SelectedIndex < 0) return;
        var value = ThemeValues[ThemePicker.SelectedIndex];
        if (Same(_config.General.ThemeMode, value)) return;

        _config.General.ThemeMode = value;
        App.ApplyTheme(value);
        await SaveAsync();
    }

    private async void OnLogChanged(object? sender, EventArgs e)
    {
        if (_loading || LogPicker.SelectedIndex < 0) return;
        var value = LogValues[LogPicker.SelectedIndex];
        if (Same(_config.General.LogVerbosity, value)) return;

        _config.General.LogVerbosity = value;
        LogService.Configure(value);
        await SaveAsync();
    }

    private async void OnCloseChanged(object? sender, EventArgs e)
    {
        if (_loading || ClosePicker.SelectedIndex < 0) return;
        var value = CloseValues[ClosePicker.SelectedIndex];
        if (Same(_config.General.ClosePageBehavior, value)) return;

        _config.General.ClosePageBehavior = value;
        await SaveAsync();
    }

    private async void OnSourceChanged(object? sender, EventArgs e)
    {
        if (_loading || SourcePicker.SelectedIndex < 0) return;
        var value = SourceValues[SourcePicker.SelectedIndex];
        if (Same(_config.General.GameSource, value)) return;

        _config.General.GameSource = value;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await Task.Run(() => AppConfig.Save(_config));
        }
        catch (Exception ex)
        {
            LogService.Error("保存设置失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("保存设置失败：" + ex.Message);
            // 恢复为磁盘上的配置，避免界面与文件不一致。
            Load();
        }
    }

    private static bool Same(string? current, string value) =>
        string.Equals(current, value, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(string[] values, string? current, int fallback)
    {
        var index = Array.FindIndex(values,
            v => string.Equals(v, current ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : fallback;
    }
}
