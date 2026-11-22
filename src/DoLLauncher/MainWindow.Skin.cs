using System.IO;
using System.Windows;
using DoLLauncher.Controls;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher;

/// <summary>本地 UI 美化包：加载、卸载与状态展示。</summary>
public partial class MainWindow
{
    private void LoadSkinSettings()
    {
        SkinService.ApplyToWindow(this);
        UpdateSkinStatus();
    }

    private void UpdateSkinStatus()
    {
        SkinStatusText.Text = SkinService.StatusText;

        // 美化包已指定的值优先：把会被覆盖的设置整块置灰，避免改了没效果
        var hasThemeOverride = SkinService.Current != null && SkinService.RequiresDarkPalette;
        var accent = SkinService.AccentColor;
        var hasAccentOverride = !string.IsNullOrEmpty(accent);

        ThemeCard.IsEnabled = !hasThemeOverride;
        ThemeCard.ToolTip = hasThemeOverride ? "当前美化包指定了深色背景，主题已跟随美化包。" : null;

        AccentCard.IsEnabled = !hasAccentOverride;
        AccentCard.ToolTip = hasAccentOverride ? $"当前美化包已指定主色 {accent}，主题色设置不生效。" : null;
    }

    private void SkinLoad_Click(object sender, RoutedEventArgs e)
    {
        var path = PlatformServices.FilePicker.PickFile(
            "选择 UI 美化包", "美化包 (*.zip)|*.zip|所有文件|*.*");
        if (path == null) return;

        var (ok, message) = SkinService.LoadFromZip(path, Log);
        if (!ok)
        {
            UpdateSkinStatus();
            AppDialog.Error(message, "UI 美化包");
            return;
        }

        ThemeService.Reapply();
        SkinService.ApplyToWindow(this);
        UpdateSkinStatus();
        AppDialog.Info(message + "\n\n如需还原，点击「卸载美化包」。", "UI 美化包");
    }

    private void SkinUnload_Click(object sender, RoutedEventArgs e)
    {
        SkinService.Deactivate();
        ThemeService.Reapply();
        SkinService.ApplyToWindow(this);
        UpdateSkinStatus();
        Log("设置：已卸载 UI 美化包");
    }

    private void SkinSettings_Click(object sender, RoutedEventArgs e)
    {
        if (SkinService.Current == null)
        {
            AppDialog.Info("请先加载美化包。", "UI 美化包");
            return;
        }

        new SkinSettingsWindow { Owner = this }.ShowDialog();
        UpdateSkinStatus();
    }

    private void SkinOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SkinService.SkinsRoot);
            PlatformServices.Links.OpenPath(SkinService.SkinsRoot);
        }
        catch (Exception ex)
        {
            LogError("打开美化包目录失败", ex);
            AppDialog.Error("无法打开美化包目录：" + ex.Message, "UI 美化包");
        }
    }
}
