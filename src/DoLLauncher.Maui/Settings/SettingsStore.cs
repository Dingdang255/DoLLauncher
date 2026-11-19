using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Settings;

/// <summary>设置子页共用的配置读取 / 保存与选项匹配工具。</summary>
internal static class SettingsStore
{
    public static LauncherConfig Load() => AppConfig.Load();

    /// <summary>保存配置；失败时提示并返回 false，成功时按需写一条运行记录。</summary>
    public static async Task<bool> SaveAsync(LauncherConfig config, string? log = null)
    {
        try
        {
            await Task.Run(() => AppConfig.Save(config));
            if (!string.IsNullOrEmpty(log)) LogService.Append("设置：" + log);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("保存设置失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("保存设置失败：" + ex.Message);
            return false;
        }
    }

    public static bool Same(string? current, string value) =>
        string.Equals(current, value, StringComparison.OrdinalIgnoreCase);

    public static int IndexOf(string[] values, string? current, int fallback)
    {
        var index = Array.FindIndex(values,
            v => string.Equals(v, current ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : fallback;
    }
}
