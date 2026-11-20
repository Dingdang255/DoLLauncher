using System.IO;
using DoLLauncher.Core.Theme;
using DoLLauncher.Core.Ui;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Settings;

public partial class AppearanceSettingsPage : ContentPage
{
    private static readonly string[] ThemeValues = { "system", "light", "dark" };
    private static readonly string[] ThemeNames = { "跟随系统", "亮色", "深色" };

    private static readonly string[] Presets =
    {
        "#FF1370F3", "#FF6C5CE7", "#FFEC4899", "#FFE5484D", "#FFF59E0B", "#FF22A06B", "#FF0EA5E9",
    };

    private static readonly FilePickerFileType SkinFileType = new(
        new Dictionary<Microsoft.Maui.Devices.DevicePlatform, IEnumerable<string>>
        {
            [Microsoft.Maui.Devices.DevicePlatform.Android] = new[]
            {
                "application/zip", "application/x-zip-compressed", "application/octet-stream",
            },
        });

    private LauncherConfig _config = new();
    private bool _loading;

    // 存储色自身的 HSL：色相 / 亮度由滑块决定，饱和度滑块是调色板的相对比例（与 WPF 一致）
    private double _baseSaturation;
    private bool _accentDirty;

    public AppearanceSettingsPage()
    {
        InitializeComponent();
        ThemePicker.ItemsSource = ThemeNames;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BuildPresets();
        Load();
    }

    protected override void OnDisappearing()
    {
        if (_accentDirty) _ = SaveAccentAsync(CurrentHex(), (int)Math.Round(SaturationSlider.Value), log: false);

        // 界面缩放按需求在退出本子页后才生效，避免拖动滑块时整页反复重排。
        if (Math.Abs(_config.General.UiScale - MainApplication.UiScale) > 0.001)
        {
            var target = _config.General.UiScale;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                // 等返回动画结束再换密度，避免过渡过程中控件跳动。
                await Task.Delay(260);
                MainApplication.ApplyUiScaleChange(target);
            });
        }

        base.OnDisappearing();
    }

    private void BuildPresets()
    {
        if (PresetRow.Children.Count > 0) return;
        foreach (var hex in Presets)
        {
            var chip = new Border
            {
                WidthRequest = 34,
                HeightRequest = 34,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 17 },
                BackgroundColor = Color.FromArgb(hex),
                // 两侧留一样的间距，配合 FlexLayout 居中，避免有的机型上整排往左偏。
                Margin = new Thickness(5, 0, 5, 8),
            };
            var tap = new TapGestureRecognizer { CommandParameter = hex };
            tap.Tapped += OnPresetTapped;
            chip.GestureRecognizers.Add(tap);
            PresetRow.Children.Add(chip);
        }
    }

    private void Load()
    {
        _config = SettingsStore.Load();
        _loading = true;
        try
        {
            ThemePicker.SelectedIndex = SettingsStore.IndexOf(ThemeValues, _config.General.ThemeMode, 0);

            var rgb = ParseAccent(_config.General.ThemeAccentColor);
            var (h, s, l) = rgb.ToHsl();
            _baseSaturation = s;
            HueSlider.Value = Math.Round(h);
            LightnessSlider.Value = Math.Round(l * 100);
            SaturationSlider.Value = Math.Clamp(_config.General.ThemeAccentSaturation, 0, 100);

            SubtitleEntry.Text = _config.General.Subtitle ?? string.Empty;
            AnimSwitch.IsToggled = _config.General.ShowAnimations;

            var scale = Math.Clamp(_config.General.UiScale, MainApplication.MinUiScale, MainApplication.MaxUiScale);
            UiScaleSlider.Value = scale;
            UpdateUiScaleLabel(scale);

            _accentDirty = false;
            UpdateAccentLabels();
            UpdateAvatar();
            UpdateSkinStatus();
        }
        finally
        {
            _loading = false;
        }
    }

    private static RgbColor ParseAccent(string? hex)
    {
        if (RgbColor.TryParse(hex, out var rgb)) return rgb;
        RgbColor.TryParse(ThemeAccent.DefaultAccent, out rgb);
        return rgb;
    }

    private string CurrentHex()
    {
        var color = RgbColor.FromHsl(HueSlider.Value, _baseSaturation, LightnessSlider.Value / 100.0);
        return color.ToArgbHex();
    }

    private void UpdateAccentLabels()
    {
        HueText.Text = (int)Math.Round(HueSlider.Value) + "°";
        SaturationText.Text = (int)Math.Round(SaturationSlider.Value) + "%";
        LightnessText.Text = (int)Math.Round(LightnessSlider.Value) + "%";

        var shown = ThemeAccent.PreviewAccent(CurrentHex(), (int)Math.Round(SaturationSlider.Value));
        AccentChip.BackgroundColor = Color.FromArgb(shown);
        AccentHexLabel.Text = shown;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_loading || ThemePicker.SelectedIndex < 0) return;
        var value = ThemeValues[ThemePicker.SelectedIndex];
        if (SettingsStore.Same(_config.General.ThemeMode, value)) return;

        _config.General.ThemeMode = value;
        App.ApplyTheme(value);
        UpdateAccentLabels();
        _ = SettingsStore.SaveAsync(_config, "界面主题 = " + ThemeNames[ThemePicker.SelectedIndex]);
    }

    private async void OnPresetTapped(object? sender, TappedEventArgs e)
    {
        if (_loading || e.Parameter is not string hex) return;

        var rgb = ParseAccent(hex);
        var (h, s, l) = rgb.ToHsl();
        _baseSaturation = s;
        _loading = true;
        try
        {
            HueSlider.Value = Math.Round(h);
            LightnessSlider.Value = Math.Round(l * 100);
            SaturationSlider.Value = 100;
        }
        finally
        {
            _loading = false;
        }

        ApplyAccent(hex, 100);
        await SaveAccentAsync(hex, 100, log: true);
    }

    private void OnHslChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_loading) return;
        UpdateAccentLabels();
        ApplyAccent(CurrentHex(), (int)Math.Round(SaturationSlider.Value));
        _accentDirty = true;
    }

    private async void OnHslDragCompleted(object? sender, EventArgs e)
    {
        if (_loading) return;
        await SaveAccentAsync(CurrentHex(), (int)Math.Round(SaturationSlider.Value), log: true);
    }

    private async void OnAccentReset(object? sender, EventArgs e)
    {
        if (_loading) return;

        var rgb = ParseAccent(string.Empty);
        var (h, s, l) = rgb.ToHsl();
        _baseSaturation = s;
        _loading = true;
        try
        {
            HueSlider.Value = Math.Round(h);
            LightnessSlider.Value = Math.Round(l * 100);
            SaturationSlider.Value = 100;
        }
        finally
        {
            _loading = false;
        }

        ApplyAccent(string.Empty, 100);
        _config.General.ThemeAccentColor = string.Empty;
        _config.General.ThemeAccentSaturation = 100;
        _accentDirty = false;
        await SettingsStore.SaveAsync(_config, "主题主色已恢复默认");
    }

    private void ApplyAccent(string? hex, int saturation)
    {
        ThemeAccent.Apply(null, hex, saturation);
        var shown = ThemeAccent.PreviewAccent(hex, saturation);
        AccentChip.BackgroundColor = Color.FromArgb(shown);
        AccentHexLabel.Text = shown;
    }

    private async Task SaveAccentAsync(string hex, int saturation, bool log)
    {
        _accentDirty = false;
        var storedHex = string.Equals(hex, ThemeAccent.DefaultAccent, StringComparison.OrdinalIgnoreCase) &&
                        string.IsNullOrWhiteSpace(_config.General.ThemeAccentColor)
            ? string.Empty
            : hex;
        if (string.Equals(_config.General.ThemeAccentColor, storedHex, StringComparison.OrdinalIgnoreCase) &&
            _config.General.ThemeAccentSaturation == saturation) return;

        _config.General.ThemeAccentColor = storedHex;
        _config.General.ThemeAccentSaturation = saturation;
        await SettingsStore.SaveAsync(_config, log ? $"主题主色 = {storedHex}（饱和度 {saturation}%）" : null);
    }

    private void UpdateAvatar()
    {
        var text = (_config.General.AvatarPath ?? string.Empty).Trim();
        var full = text.Length == 0 ? null : Path.IsPathRooted(text) ? text : Path.Combine(Paths.Root, text);
        if (full != null && AvatarRules.Validate(full).Ok)
        {
            try
            {
                var bytes = File.ReadAllBytes(full);
                AvatarImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
                AvatarImage.IsVisible = true;
                AvatarPlaceholder.IsVisible = false;
                AvatarStatusLabel.Text = "当前头像：" + Path.GetFileName(full);
                return;
            }
            catch (Exception ex)
            {
                LogService.Error("读取玩家头像失败", ex);
            }
        }

        AvatarImage.Source = null;
        AvatarImage.IsVisible = false;
        AvatarPlaceholder.IsVisible = true;
        AvatarStatusLabel.Text = "当前使用内置占位图；点击「上传头像」可选择本地图片。";
    }

    private async void OnAvatarUpload(object? sender, EventArgs e)
    {
        FileResult? file;
        try
        {
            file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选择玩家头像",
                FileTypes = FilePickerFileType.Images,
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开图片选择器失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开图片选择器：" + ex.Message);
            return;
        }
        if (file == null) return;

        string? temp = null;
        try
        {
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
            temp = Path.Combine(FileSystem.CacheDirectory, "avatar-upload" + ext);
            await using (var source = await file.OpenReadAsync())
            await using (var dest = File.Create(temp))
            {
                await source.CopyToAsync(dest);
            }

            var (ok, reason) = AvatarRules.Validate(temp);
            if (!ok)
            {
                await PlatformServices.Dialogs.WarnAsync(reason, "玩家头像");
                return;
            }

            var target = Path.Combine(Paths.Root, "avatar.png");
            await Task.Run(() => File.Copy(temp, target, overwrite: true));

            _config.General.AvatarPath = "avatar.png";
            await SettingsStore.SaveAsync(_config, "已更新玩家头像");
            UpdateAvatar();
        }
        catch (Exception ex)
        {
            LogService.Error("保存玩家头像失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("保存头像失败：" + ex.Message);
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    private async void OnAvatarReset(object? sender, EventArgs e)
    {
        try
        {
            var target = Path.Combine(Paths.Root, "avatar.png");
            await Task.Run(() => { if (File.Exists(target)) File.Delete(target); });

            _config.General.AvatarPath = string.Empty;
            await SettingsStore.SaveAsync(_config, "已恢复默认头像");
            UpdateAvatar();
        }
        catch (Exception ex)
        {
            LogService.Error("恢复默认头像失败", ex);
        }
    }

    private async void OnSubtitleUnfocused(object? sender, EventArgs e)
    {
        if (_loading) return;
        var value = (SubtitleEntry.Text ?? string.Empty).Trim();
        if (SettingsStore.Same(_config.General.Subtitle, value)) return;

        _config.General.Subtitle = value;
        await SettingsStore.SaveAsync(_config, "启动器副标题 = " + (value.Length == 0 ? "（默认）" : value));
    }

    private async void OnAnimToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        if (_config.General.ShowAnimations == e.Value) return;

        _config.General.ShowAnimations = e.Value;
        await SettingsStore.SaveAsync(_config, "界面过渡动画 = " + (e.Value ? "开" : "关"));
    }

    private void OnUiScaleChanged(object? sender, ValueChangedEventArgs e) => UpdateUiScaleLabel(e.NewValue);

    private void UpdateUiScaleLabel(double value) => UiScaleText.Text = value.ToString("0.00") + "x";

    /// <summary>松手后按 0.05 取整并写入配置；真正应用在退出本页时进行。</summary>
    private async void OnUiScaleDragCompleted(object? sender, EventArgs e)
    {
        if (_loading) return;

        var snapped = Math.Round(UiScaleSlider.Value / 0.05) * 0.05;
        snapped = Math.Clamp(snapped, MainApplication.MinUiScale, MainApplication.MaxUiScale);
        _loading = true;
        try { UiScaleSlider.Value = snapped; }
        finally { _loading = false; }

        UpdateUiScaleLabel(snapped);
        if (Math.Abs(_config.General.UiScale - snapped) < 0.001) return;

        _config.General.UiScale = snapped;
        await SettingsStore.SaveAsync(_config, $"界面缩放 = {snapped:0.00}x");
    }

    // ---------- UI 美化包 ----------

    private void UpdateSkinStatus()
    {
        SkinStatusLabel.Text = AndroidSkinStore.StatusText;

        // CSS 解析 / 应用告警：可展开查看前 10 条
        var warnings = AndroidSkinStore.CssWarnings;
        SkinWarningButton.IsVisible = warnings.Count > 0;
        SkinWarningButton.Text = SkinWarningList.IsVisible
            ? "收起警告"
            : $"查看警告（{warnings.Count}）";
        if (warnings.Count > 0)
        {
            var lines = warnings.Take(10).Select((item, index) => $"{index + 1}. {item}");
            SkinWarningList.Text = string.Join("\n", lines);
        }
        else
        {
            SkinWarningList.IsVisible = false;
        }

        // 美化包已指定的值优先：把会被覆盖的设置整块置灰，并给出说明
        var requiresDark = AndroidSkinStore.Current != null && AndroidSkinStore.RequiresDarkPalette;
        var accent = AndroidSkinStore.AccentColor;

        ThemeCard.IsEnabled = !requiresDark;
        ThemeCard.Opacity = requiresDark ? 0.55 : 1;
        AccentCard.IsEnabled = accent == null;
        AccentCard.Opacity = accent == null ? 1 : 0.55;

        var notes = new List<string>();
        if (requiresDark) notes.Add("当前美化包指定了深色背景，主题已跟随美化包，无法在此修改。");
        if (accent != null) notes.Add($"当前美化包已指定主色 {accent}，主题色设置不生效。");
        SkinOverrideNote.Text = string.Join("\n", notes);
        SkinOverrideNote.IsVisible = notes.Count > 0;
    }

    private void OnSkinWarningsToggle(object? sender, EventArgs e)
    {
        SkinWarningList.IsVisible = !SkinWarningList.IsVisible;
        UpdateSkinStatus();
    }

    private async void OnSkinImport(object? sender, EventArgs e)
    {
        FileResult? file;
        try
        {
            file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选择 UI 美化包",
                FileTypes = SkinFileType,
            });
        }
        catch (Exception ex)
        {
            LogService.Error("打开美化包选择器失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开文件选择器：" + ex.Message, "UI 美化包");
            return;
        }
        if (file == null) return;

        string? temp = null;
        try
        {
            temp = Path.Combine(FileSystem.CacheDirectory, "skin-" + Guid.NewGuid().ToString("N") + ".zip");
            await using (var source = await file.OpenReadAsync())
            await using (var dest = File.Create(temp))
            {
                await source.CopyToAsync(dest);
            }

            var (ok, message) = await Task.Run(() =>
                AndroidSkinStore.ImportFromZip(temp, msg => LogService.Append(msg)));
            if (!ok)
            {
                UpdateSkinStatus();
                await PlatformServices.Dialogs.ErrorAsync(message, "UI 美化包");
                return;
            }

            AndroidSkinStore.Refresh();
            Load();
            await PlatformServices.Dialogs.InfoAsync(message + "\n\n如需还原，点击「卸载美化包」。", "UI 美化包");
        }
        catch (Exception ex)
        {
            LogService.Error("导入美化包失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导入美化包失败：" + ex.Message, "UI 美化包");
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    private async void OnSkinUnload(object? sender, EventArgs e)
    {
        if (AndroidSkinStore.Current == null)
        {
            await PlatformServices.Dialogs.InfoAsync("当前没有启用中的美化包。", "UI 美化包");
            return;
        }

        try
        {
            AndroidSkinStore.Deactivate();
            Load();
            await PlatformServices.Dialogs.InfoAsync("已卸载美化包，并恢复你原来的主题设置。", "UI 美化包");
        }
        catch (Exception ex)
        {
            LogService.Error("卸载美化包失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("卸载美化包失败：" + ex.Message, "UI 美化包");
        }
    }
}
