using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using DoLLauncher.Controls;
using DoLLauncher.Core.Browser;
using DoLLauncher.Core.Search;
using DoLLauncher.Core.Theme;
using DoLLauncher.Core.Versions;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Win32;
using SubtitleTextHelper = DoLLauncher.Core.Ui.SubtitleText;

namespace DoLLauncher;

public partial class MainWindow
{
    // ---------- 设置 ----------

    private void LoadGeneralSettings()
    {
        _loadingGeneralSettings = true;
        try
        {
            var cfg = AppConfig.Load();
            WriteModMetaCheck.IsChecked = cfg.General.WriteModMetadata;
            PrefetchBox.SelectedItem = PrefetchBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.Prefetch);
            if (PrefetchBox.SelectedItem == null) PrefetchBox.SelectedIndex = 0;

            MarketSourceBox.SelectedItem = MarketSourceBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.MarketSource);
            if (MarketSourceBox.SelectedItem == null) MarketSourceBox.SelectedIndex = 0;

            DownloadSourceBox.SelectedItem = DownloadSourceBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.ModDownloadSource);
            if (DownloadSourceBox.SelectedItem == null) DownloadSourceBox.SelectedIndex = 0;

            ApiSourceBox.SelectedItem = ApiSourceBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.GithubApiSource);
            if (ApiSourceBox.SelectedItem == null) ApiSourceBox.SelectedIndex = 0;

            VersionGapBox.SelectedItem = VersionGapBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.VersionGapThreshold.ToString());
            if (VersionGapBox.SelectedItem == null) VersionGapBox.SelectedIndex = 0;

            var theme = ThemeService.Normalize(cfg.General.ThemeMode);
            ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == theme);
            if (ThemeBox.SelectedItem == null) ThemeBox.SelectedIndex = 0;

            AnimationCheck.IsChecked = cfg.General.ShowAnimations;
            UiAnimations.Enabled = cfg.General.ShowAnimations;
            UpdateAccentControls(cfg.General.ThemeAccentColor);
            LoadTimingControls(cfg.General);

            var subtitle = (cfg.General.Subtitle ?? string.Empty).Trim();
            SubtitleCustomBox.Text = subtitle;

            BrowserModeBox.SelectedItem = BrowserModeBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == NormalizeBrowserMode(cfg.General.BrowserMode));
            if (BrowserModeBox.SelectedItem == null) BrowserModeBox.SelectedIndex = 0;
            BrowserExeBox.Text = cfg.General.BrowserExecutable ?? string.Empty;
            BrowserArgsBox.Text = cfg.General.BrowserArguments ?? string.Empty;
            BrowserProfileCheck.IsChecked = cfg.General.BrowserUseProfile;
            var closePage = (cfg.General.ClosePageBehavior ?? string.Empty).Trim().ToLowerInvariant() == "ask"
                ? "ask"
                : "close";
            ClosePageBox.SelectedItem = ClosePageBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == closePage);
            if (ClosePageBox.SelectedItem == null) ClosePageBox.SelectedIndex = 0;
            BrowserSettingsStatus.Text = string.Empty;
            UpdateBrowserCustomState();

            VersionInfoText.Text = BuildInfo.Display;
        }
        finally
        {
            _loadingGeneralSettings = false;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        Log("检查更新：开始");
        try
        {
            var release = await UpdateService.CheckAsync();
            if (release.Tag.Length == 0)
            {
                UpdateStatusText.Text = "没有可用的 Release 信息。";
                return;
            }

            var hasUpdate = BuildInfo.Tag == "dev" || UpdateService.IsNewer(release.Tag, BuildInfo.Tag);
            if (hasUpdate)
            {
                UpdateStatusText.Text = BuildInfo.Tag == "dev"
                    ? $"当前为开发版构建，最新 Release 为 {release.Tag}。"
                    : $"发现新版本 {release.Tag}（当前 {BuildInfo.Tag}）。";
                Log("检查更新：发现新版本 " + release.Tag);
                if (release.Url.Length > 0 &&
                    AppDialog.Confirm(
                        $"发现新版本 {release.Tag}（当前 {BuildInfo.Tag}）。\n要打开下载页面吗？",
                        "检查更新", "打开下载页"))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url)
                    {
                        UseShellExecute = true,
                    });
                }
            }
            else
            {
                UpdateStatusText.Text = $"已是最新版本（{BuildInfo.Tag}）。";
                Log("检查更新：已是最新版本");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = ex.Message.Contains("404")
                ? "仓库还没有发布任何 Release。"
                : "检查更新失败：" + ex.Message;
            Log("检查更新失败：" + ex.Message);
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private void Prefetch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var mode = (PrefetchBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "parallel";
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.Prefetch = mode;
            AppConfig.Save(cfg);
            Log("设置：启动时预取索引 = " +
                ((PrefetchBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? mode));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void MarketSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var source = (MarketSourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.MarketSource = source;
            AppConfig.Save(cfg);
            Log("设置：模组市场索引源 = " +
                ((MarketSourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? source));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void ApiSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var source = (ApiSourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.GithubApiSource = source;
            AppConfig.Save(cfg);
            Log("设置：GitHub 接口源 = " +
                ((ApiSourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? source));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void VersionGap_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var raw = (VersionGapBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1";
        var threshold = ProblemService.NormalizeThreshold(int.TryParse(raw, out var value) ? value : 1);
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.VersionGapThreshold = threshold;
            AppConfig.Save(cfg);
            Log("设置：游戏版本差异告警阈值 = " + threshold);
            if (_selected != null) ApplyModIssues();
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void DownloadSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var source = (DownloadSourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.ModDownloadSource = source;
            AppConfig.Save(cfg);
            Log("设置：模组下载源 = " +
                ((DownloadSourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? source));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void WriteModMeta_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.WriteModMetadata = WriteModMetaCheck.IsChecked == true;
            AppConfig.Save(cfg);
            Log("设置：补全信息写回模组包 = " + (cfg.General.WriteModMetadata ? "开" : "关"));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    // ---------- 设置分类导航 ----------

    /// <summary>进入某个设置分类；不传参数表示回到分类首页。路径可多级，用 / 分隔。</summary>
    private void NavigateSettings(params string[] path)
    {
        _settingsPath.Clear();
        if (path != null) _settingsPath.AddRange(path.Where(p => p.Length > 0));

        SettingsRootPanel.Visibility = Visibility.Collapsed;
        SettingsNetworkPanel.Visibility = Visibility.Collapsed;
        SettingsDataPanel.Visibility = Visibility.Collapsed;
        SettingsModInfoPanel.Visibility = Visibility.Collapsed;
        SettingsProblemPanel.Visibility = Visibility.Collapsed;
        SettingsBrowserPanel.Visibility = Visibility.Collapsed;
        SettingsAppearancePanel.Visibility = Visibility.Collapsed;
        SettingsVersionPanel.Visibility = Visibility.Collapsed;

        var panel = SettingsPanelForPath();
        panel.Visibility = Visibility.Visible;
        BuildSettingsBreadcrumb();
        SettingsDescText.Text = SettingsDescription();
        SettingsScroll.ScrollToTop();

        if (_settingsPath.Count == 0) UiAnimations.Fade(SettingsRootPanel, 0.5, 1, 150);
        else UiAnimations.ShowPage(panel);
    }

    private FrameworkElement SettingsPanelForPath()
    {
        return string.Join("/", _settingsPath) switch
        {
            "网络下载" => SettingsNetworkPanel,
            "数据与索引" => SettingsDataPanel,
            "数据与索引/模组信息" => SettingsModInfoPanel,
            "问题检查" => SettingsProblemPanel,
            "游戏浏览器" => SettingsBrowserPanel,
            "界面与外观" => SettingsAppearancePanel,
            "版本信息" => SettingsVersionPanel,
            _ => SettingsRootPanel,
        };
    }

    private string SettingsDescription()
    {
        var key = string.Join("/", _settingsPath);
        return key switch
        {
            "" => "选择一个分类查看相关设置。",
            "网络下载" => "下载安装包与访问 GitHub 接口时使用的线路，失败会自动回退。",
            "数据与索引" => "模组市场索引的拉取地址与时机，以及模组信息写回。",
            "数据与索引/模组信息" => "是否把索引里补全的作者 / 主页写回模组包。",
            "问题检查" => "「可能存在问题」里版本差异告警的敏感度。",
            "游戏浏览器" => "打开游戏页使用的浏览器与关页停服行为，修改后下次启动游戏生效。",
            "界面与外观" => "主题、主题色、玩家头像、UI 美化包、副标题与过渡动画，修改后立即生效。",
            "版本信息" => "当前启动器的版本、提交与构建时间，并可检查更新。",
            _ => "",
        };
    }

    private void BuildSettingsBreadcrumb()
    {
        SettingsCrumbPanel.Children.Clear();

        if (_settingsPath.Count == 0)
        {
            SettingsCrumbPanel.Children.Add(new TextBlock
            {
                Text = "设置",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        AddSettingsCrumb("设置", "", false);
        var prefix = new List<string>();
        for (var i = 0; i < _settingsPath.Count; i++)
        {
            prefix.Add(_settingsPath[i]);
            SettingsCrumbPanel.Children.Add(new TextBlock
            {
                Text = "›",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 15,
                Margin = new Thickness(5, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            AddSettingsCrumb(_settingsPath[i], string.Join("/", prefix), i == _settingsPath.Count - 1);
        }
    }

    private void AddSettingsCrumb(string text, string path, bool current)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("LinkButton"),
            FontSize = 17,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            Tag = path,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += SettingsCrumb_Click;
        SettingsCrumbPanel.Children.Add(button);
    }

    private void SettingsCrumb_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string raw) return;
        NavigateSettings(raw.Length == 0 ? Array.Empty<string>() : raw.Split('/'));
    }

    private void SettingsCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string category || category.Length == 0) return;
        NavigateSettings(category.Split('/'));
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var mode = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ThemeService.ModeSystem;
        try
        {
            ThemeService.SetMode(mode);
            Log("设置：界面主题 = " +
                ((ThemeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? mode));
        }
        catch (Exception ex)
        {
            Log("切换主题失败：" + ex.Message);
        }
    }

    private void Animation_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var enabled = AnimationCheck.IsChecked == true;
        UiAnimations.Enabled = enabled;
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.ShowAnimations = enabled;
            AppConfig.Save(cfg);
            Log("设置：界面过渡动画 = " + (enabled ? "开" : "关"));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    // ---------- 设置：启动器副标题 ----------

    private void SubtitleCustom_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        SaveSubtitle(SubtitleCustomBox.Text.Trim());
    }

    private void SaveSubtitle(string value)
    {
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.Subtitle = value;
            AppConfig.Save(cfg);
            ApplySubtitle(value);
            Log("设置：启动器副标题 = " + (value.Length == 0 ? "（默认）" : value));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void ApplySubtitle(string? value)
    {
        SubtitleText.Text = SubtitleTextHelper.Resolve(value);
        SubtitleText.Visibility = Visibility.Visible;
    }

    // ---------- 设置：主题色 ----------

    private void UpdateAccentControls(string? hex)
    {
        var cfg = AppConfig.Load();
        var rgb = ParseAccent(hex);
        var (h, _, l) = rgb.ToHsl();

        AccentHueSlider.Value = Math.Round(h);
        AccentLightnessSlider.Value = Math.Round(l * 100);
        AccentSaturationSlider.Value = Math.Clamp(cfg.General.ThemeAccentSaturation, 0, 100);
        UpdateAccentHslTexts();
        UpdateAccentPreview(hex, cfg.General.ThemeAccentSaturation);
    }

    private void UpdateAccentHslTexts()
    {
        AccentHueText.Text = (int)Math.Round(AccentHueSlider.Value) + "°";
        AccentSaturationText.Text = (int)Math.Round(AccentSaturationSlider.Value) + "%";
        AccentLightnessText.Text = (int)Math.Round(AccentLightnessSlider.Value) + "%";
    }

    /// <summary>按当前滑块组合生成主色：色相 / 亮度取滑块，基础饱和度沿用已存颜色的色相饱和度。</summary>
    private RgbColor AccentColorFromSliders()
    {
        var stored = ParseAccent(AppConfig.Load().General.ThemeAccentColor);
        var (_, s, _) = stored.ToHsl();
        return RgbColor.FromHsl(AccentHueSlider.Value, s, AccentLightnessSlider.Value / 100.0);
    }

    /// <summary>预览色块用最终强调色（含对比度与饱和度处理），与界面实际显示一致。</summary>
    private void UpdateAccentPreview(string? accentHex, int saturation)
    {
        var hex = accentHex ?? AppConfig.Load().General.ThemeAccentColor;
        var shown = ThemeService.PreviewAccent(hex, saturation);
        if (!RgbColor.TryParse(shown, out var rgb)) rgb = ParseAccent(hex);
        var brush = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
        AccentColorButton.Background = brush;
        AccentColorButton.BorderBrush = brush;
        AccentColorText.Text = rgb.ToArgbHex();
    }

    private static RgbColor ParseAccent(string? hex)
    {
        if (RgbColor.TryParse(hex, out var rgb)) return rgb;
        RgbColor.TryParse(ThemePaletteBuilder.DefaultAccentColor, out rgb);
        return rgb;
    }

    private void AccentColor_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var cfg = AppConfig.Load();
        var picked = AccentColorDialog.Show(Window.GetWindow(this), ParseAccent(cfg.General.ThemeAccentColor));
        if (picked == null) return;

        try
        {
            cfg = AppConfig.Load();
            cfg.General.ThemeAccentColor = picked.Value.ToArgbHex();
            // 新选的颜色按推荐的鲜艳度显示，滑块回到 100%
            cfg.General.ThemeAccentSaturation = 100;
            AppConfig.Save(cfg);
            UpdateAccentControls(cfg.General.ThemeAccentColor);
            ThemeService.Reapply();
            Log("设置：主题主色 = " + cfg.General.ThemeAccentColor);
        }
        catch (Exception ex)
        {
            Log("保存主题主色失败：" + ex.Message);
        }
    }

    /// <summary>恢复默认配色：清空自定义主色与饱和度。</summary>
    private void AccentReset_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.ThemeAccentColor = string.Empty;
            cfg.General.ThemeAccentSaturation = 100;
            AppConfig.Save(cfg);
            UpdateAccentControls(string.Empty);
            ThemeService.Reapply();
            Log("设置：主题主色已恢复默认");
        }
        catch (Exception ex)
        {
            Log("恢复默认主题色失败：" + ex.Message);
        }
    }

    private void AccentHsl_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingGeneralSettings) return;
        UpdateAccentHslTexts();

        var dragging = AccentHueSlider.IsMouseCaptureWithin ||
                       AccentSaturationSlider.IsMouseCaptureWithin ||
                       AccentLightnessSlider.IsMouseCaptureWithin;

        // 饱和度滑块是独立的比例设置（100 = 推荐鲜艳度），不影响色相 / 亮度
        if (ReferenceEquals(sender, AccentSaturationSlider))
        {
            var percent = (int)Math.Round(AccentSaturationSlider.Value);
            UpdateAccentPreview(null, percent);
            if (!dragging) SaveAccentSaturation(percent);
            return;
        }

        var hex = AccentColorFromSliders().ToArgbHex();
        UpdateAccentPreview(hex, (int)Math.Round(AccentSaturationSlider.Value));
        if (!dragging) SaveAccentColor(hex);
    }

    private void AccentHsl_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        if (ReferenceEquals(sender, AccentSaturationSlider)) SaveAccentSaturation((int)Math.Round(AccentSaturationSlider.Value));
        else SaveAccentColor(AccentColorFromSliders().ToArgbHex());
    }

    private void SaveAccentColor(string hex)
    {
        try
        {
            var cfg = AppConfig.Load();
            if (string.Equals(cfg.General.ThemeAccentColor, hex, StringComparison.OrdinalIgnoreCase)) return;
            cfg.General.ThemeAccentColor = hex;
            AppConfig.Save(cfg);
            ThemeService.Reapply();
            Log("设置：主题主色 = " + hex);
        }
        catch (Exception ex)
        {
            Log("保存主题主色失败：" + ex.Message);
        }
    }

    private void SaveAccentSaturation(int percent)
    {
        try
        {
            var cfg = AppConfig.Load();
            if (cfg.General.ThemeAccentSaturation == percent) return;
            cfg.General.ThemeAccentSaturation = percent;
            AppConfig.Save(cfg);
            ThemeService.Reapply();
            Log("设置：主题主色饱和度 = " + percent + "%");
        }
        catch (Exception ex)
        {
            Log("保存主题主色饱和度失败：" + ex.Message);
        }
    }

    // ---------- 设置：关页检测时间参数 ----------

    private void TimingSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 加载阶段设置 Minimum 会把 Value 从 0 纠偏，此时其余控件还没创建
        if (_loadingGeneralSettings || !TimingControlsReady) return;
        UpdateTimingTexts();
        if (AnyTimingSliderDragging()) return;
        SaveTiming();
    }

    private void TimingSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_loadingGeneralSettings || !TimingControlsReady) return;
        SaveTiming();
    }

    private bool TimingControlsReady =>
        HeartbeatIntervalSlider != null && HeartbeatTimeoutSlider != null &&
        FirstGraceSlider != null && CloseDebounceSlider != null &&
        HeartbeatIntervalText != null && HeartbeatTimeoutText != null &&
        FirstGraceText != null && CloseDebounceText != null;

    private bool AnyTimingSliderDragging() =>
        HeartbeatIntervalSlider.IsMouseCaptureWithin ||
        HeartbeatTimeoutSlider.IsMouseCaptureWithin ||
        FirstGraceSlider.IsMouseCaptureWithin ||
        CloseDebounceSlider.IsMouseCaptureWithin;

    private void UpdateTimingTexts()
    {
        if (!TimingControlsReady) return;
        HeartbeatIntervalText.Text = (int)Math.Round(HeartbeatIntervalSlider.Value) + " 秒";
        HeartbeatTimeoutText.Text = (int)Math.Round(HeartbeatTimeoutSlider.Value) + " 秒";
        FirstGraceText.Text = (int)Math.Round(FirstGraceSlider.Value) + " 秒";
        CloseDebounceText.Text = (int)Math.Round(CloseDebounceSlider.Value) + " 秒";
    }

    private void LoadTimingControls(GeneralSettings general)
    {
        HeartbeatIntervalSlider.Value = Math.Clamp(general.HeartbeatSeconds, 1, 30);
        HeartbeatTimeoutSlider.Value = Math.Clamp(general.HeartbeatTimeoutSeconds, 5, 180);
        FirstGraceSlider.Value = Math.Clamp(general.FirstHeartbeatGraceSeconds, 5, 600);
        CloseDebounceSlider.Value = Math.Clamp(general.CloseDebounceSeconds, 0, 60);
        UpdateTimingTexts();
    }

    private void SaveTiming()
    {
        if (!TimingControlsReady) return;
        try
        {
            var cfg = AppConfig.Load();
            var heartbeat = (int)Math.Round(HeartbeatIntervalSlider.Value);
            var timeout = (int)Math.Round(HeartbeatTimeoutSlider.Value);
            var grace = (int)Math.Round(FirstGraceSlider.Value);
            var debounce = (int)Math.Round(CloseDebounceSlider.Value);
            if (cfg.General.HeartbeatSeconds == heartbeat &&
                cfg.General.HeartbeatTimeoutSeconds == timeout &&
                cfg.General.FirstHeartbeatGraceSeconds == grace &&
                cfg.General.CloseDebounceSeconds == debounce) return;

            cfg.General.HeartbeatSeconds = heartbeat;
            cfg.General.HeartbeatTimeoutSeconds = timeout;
            cfg.General.FirstHeartbeatGraceSeconds = grace;
            cfg.General.CloseDebounceSeconds = debounce;
            AppConfig.Save(cfg);
            Log($"设置：关页检测 心跳 {heartbeat}s / 超时 {timeout}s / 宽限 {grace}s / 防抖 {debounce}s");
        }
        catch (Exception ex)
        {
            Log("保存关页检测设置失败：" + ex.Message);
        }
    }

    // ---------- 设置：游戏浏览器 ----------

    private void BrowserMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var mode = (BrowserModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "profile";
        if (mode == "custom" && !IsBrowserPathValid(BrowserExeBox.Text))
        {
            // 先选「自定义浏览器」时还没有 exe：直接弹出选择框，避免和「浏览…」按钮互相卡住
            if (!PickBrowserExecutable())
            {
                RestoreBrowserModeSelection(AppConfig.Load().General.BrowserMode);
                return;
            }
        }

        try
        {
            var cfg = AppConfig.Load();
            cfg.General.BrowserMode = mode;
            AppConfig.Save(cfg);
            BrowserSettingsStatus.Text = "已保存，下次启动游戏时生效。";
            Log("设置：游戏浏览器承载方式 = " +
                ((BrowserModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? mode));
            UpdateBrowserCustomState();
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void ClosePage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var behavior = (ClosePageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "close";
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.ClosePageBehavior = behavior;
            AppConfig.Save(cfg);
            Log("设置：关闭游戏页面时 = " +
                ((ClosePageBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? behavior));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void RestoreBrowserModeSelection(string? mode)
    {
        _loadingGeneralSettings = true;
        try
        {
            BrowserModeBox.SelectedItem = BrowserModeBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == NormalizeBrowserMode(mode));
            if (BrowserModeBox.SelectedItem == null) BrowserModeBox.SelectedIndex = 0;
        }
        finally
        {
            _loadingGeneralSettings = false;
        }
        UpdateBrowserCustomState();
    }

    private void BrowseBrowserExe_Click(object sender, RoutedEventArgs e)
    {
        PickBrowserExecutable();
    }

    /// <summary>弹出文件选择框；选中并保存后返回 true，取消返回 false。</summary>
    private bool PickBrowserExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择浏览器可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return false;
        BrowserExeBox.Text = dialog.FileName;
        SaveBrowserExecutable(dialog.FileName);
        UpdateBrowserCustomState();
        return true;
    }

    private void BrowserExe_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var path = BrowserExeBox.Text.Trim();
        if (path.Length > 0 && !File.Exists(path))
        {
            AppDialog.Warn("浏览器路径不存在：\n" + path + "\n\n请重新选择，或清空后改用其它承载方式。", "游戏浏览器");
            _loadingGeneralSettings = true;
            BrowserExeBox.Text = AppConfig.Load().General.BrowserExecutable ?? string.Empty;
            _loadingGeneralSettings = false;
            return;
        }
        SaveBrowserExecutable(path);
    }

    private void SaveBrowserExecutable(string path)
    {
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.BrowserExecutable = path;
            AppConfig.Save(cfg);
            BrowserSettingsStatus.Text = "已保存，下次启动游戏时生效。";
            Log("设置：自定义浏览器路径 = " + (path.Length == 0 ? "（空）" : path));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void BrowserArgs_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.BrowserArguments = BrowserArgsBox.Text.Trim();
            AppConfig.Save(cfg);
            BrowserSettingsStatus.Text = "已保存，下次启动游戏时生效。";
            Log("设置：自定义浏览器参数已更新");
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void BrowserProfile_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.BrowserUseProfile = BrowserProfileCheck.IsChecked == true;
            AppConfig.Save(cfg);
            Log("设置：自定义浏览器独立 Profile = " + (cfg.General.BrowserUseProfile ? "开" : "关"));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void UpdateBrowserCustomState()
    {
        var custom = (BrowserModeBox.SelectedItem as ComboBoxItem)?.Tag as string == "custom";
        BrowserExeBox.IsEnabled = custom;
        BrowserArgsBox.IsEnabled = custom;
        BrowserProfileCheck.IsEnabled = custom;
        BrowseBrowserExeBtn.IsEnabled = custom;
    }

    private static bool IsBrowserPathValid(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim());

    private static string NormalizeBrowserMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "default" => "default",
        "custom" => "custom",
        _ => "profile",
    };

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        PaneSplitter.RestoreDefaults(this);
        Log("设置：已恢复默认界面布局尺寸");
    }

}
