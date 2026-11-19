using System.Collections.ObjectModel;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace DoLLauncher.Maui;

public partial class BackupPage : ContentView
{
    private readonly ObservableCollection<BackupEntry> _entries = new();
    private readonly List<BackupEntry> _all = new();
    private readonly string? _preferredFolder;
    private List<VersionInfo> _versions = new();
    private VersionInfo? _version;
    private bool _loading;
    private bool _busy;
    private bool _loadingSettings;
    private bool _suppressVersionEvent;
    private int _chip;

    public BackupPage(VersionInfo? version = null)
    {
        InitializeComponent();
        _preferredFolder = version?.Folder;
        BackupList.ItemsSource = _entries;
        VersionPicker.ItemDisplayBinding = new Binding(nameof(VersionInfo.DisplayName));
        StyleChips();
        LoadBackupSettings();
        UpdateActionsEnabled();
    }

    /// <summary>主界面切换到本面板时调用。</summary>
    public async Task ActivateAsync()
    {
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadVersionsAsync();
        await ReloadAsync();
    }

    // ---------- 版本选择 ----------

    private async Task LoadVersionsAsync()
    {
        List<VersionInfo> scanned;
        try
        {
            scanned = await Task.Run(VersionService.Scan);
        }
        catch (Exception ex)
        {
            LogService.Error("扫描版本失败", ex);
            scanned = new List<VersionInfo>();
        }

        // 备份页只处理 http 版本（file 伪版本没有备份目录）。
        _versions = scanned.Where(v => v.Mode == "http").ToList();
        var preferred = _version?.Folder ?? _preferredFolder;
        var target = _versions.FirstOrDefault(v => v.Folder == preferred) ?? _versions.FirstOrDefault();

        _suppressVersionEvent = true;
        try
        {
            VersionPicker.ItemsSource = _versions;
            // 只有一个版本时固定显示，不再让用户切换。
            VersionPicker.IsEnabled = _versions.Count > 1;
            VersionPicker.SelectedIndex = target == null ? -1 : _versions.IndexOf(target);
        }
        finally
        {
            _suppressVersionEvent = false;
        }

        ApplyVersion(target);
    }

    private void OnVersionChanged(object? sender, EventArgs e)
    {
        if (_suppressVersionEvent) return;
        var index = VersionPicker.SelectedIndex;
        var version = index >= 0 && index < _versions.Count ? _versions[index] : null;
        ApplyVersion(version);
        _ = ReloadAsync();
    }

    private void ApplyVersion(VersionInfo? version)
    {
        _version = version;
        VersionHintLabel.IsVisible = version == null;
        UpdateActionsEnabled();
        if (version == null) MirrorLabel.Text = "未选择版本。";
    }

    private void UpdateActionsEnabled()
    {
        var enabled = _version != null && !_busy;
        OpenHelperButton.IsEnabled = enabled;
        BackupSavesButton.IsEnabled = enabled;
        ExportConfigButton.IsEnabled = enabled;
        RestoreConfigButton.IsEnabled = enabled;
        CreateSnapshotButton.IsEnabled = enabled;
        QuickSnapshotButton.IsEnabled = enabled;
        SnapshotGameCheck.IsEnabled = enabled;
        RefreshMirrorButton.IsEnabled = enabled;
    }

    // ---------- 列表 ----------

    private async Task ReloadAsync()
    {
        if (_loading)
        {
            Refresher.IsRefreshing = false;
            return;
        }

        _loading = true;
        var version = _version;
        LoadingBar.IsVisible = true;
        FailurePanel.IsVisible = false;
        try
        {
            if (version == null)
            {
                _all.Clear();
                ApplyFilter();
                return;
            }

            var (entries, mirror) = await Task.Run(() =>
            {
                var list = BackupService.List(version.Folder);
                var text = BackupService.DescribeMirror(version.Folder);
                return (list, text);
            });

            // 扫描期间用户可能切换了版本，过期结果直接丢弃。
            if (!string.Equals(version.Folder, _version?.Folder, StringComparison.OrdinalIgnoreCase))
                return;

            _all.Clear();
            _all.AddRange(entries);
            MirrorLabel.Text = mirror;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            LogService.Error("读取备份列表失败", ex);
            FailureLabel.Text = "读取备份列表失败：" + ex.Message;
            FailurePanel.IsVisible = true;
            EmptyPanel.IsVisible = false;
        }
        finally
        {
            _loading = false;
            LoadingBar.IsVisible = false;
            Refresher.IsRefreshing = false;
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e) => await ReloadAsync();

    private async void OnRetryClicked(object? sender, EventArgs e) => await ReloadAsync();

    private void ApplyFilter()
    {
        _entries.Clear();
        foreach (var entry in _all.Where(MatchesChip)) _entries.Add(entry);
        CountLabel.Text = _all.Count == 0 ? string.Empty : $"{_entries.Count} / {_all.Count} 项";
        EmptyPanel.IsVisible = _version != null && _entries.Count == 0 && !FailurePanel.IsVisible;
    }

    private bool MatchesChip(BackupEntry entry) => _chip switch
    {
        // 取不到类型的条目归为快照。
        1 => entry.IsSnapshot || (!entry.IsSaves && !entry.IsModsConfig),
        2 => entry.IsSaves,
        3 => entry.IsModsConfig,
        _ => true,
    };

    private void OnChipAllClicked(object? sender, EventArgs e) => SetChip(0);

    private void OnChipSnapshotClicked(object? sender, EventArgs e) => SetChip(1);

    private void OnChipSavesClicked(object? sender, EventArgs e) => SetChip(2);

    private void OnChipConfigClicked(object? sender, EventArgs e) => SetChip(3);

    private void SetChip(int chip)
    {
        _chip = chip;
        StyleChips();
        ApplyFilter();
    }

    private void StyleChips()
    {
        StyleChip(ChipAll, _chip == 0);
        StyleChip(ChipSnapshot, _chip == 1);
        StyleChip(ChipSaves, _chip == 2);
        StyleChip(ChipConfig, _chip == 3);
    }

    private static void StyleChip(Button button, bool active)
    {
        if (active)
        {
            // 跟随设置里的主题色，而不是固定紫色。
            button.SetDynamicResource(Button.BackgroundColorProperty, "AccentColor");
            button.SetDynamicResource(Button.TextColorProperty, "AccentOnColor");
        }
        else
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty,
                Color.FromArgb("#E0E0E0"), Color.FromArgb("#3C3C3C"));
            button.SetAppThemeColor(Button.TextColorProperty,
                Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));
        }
    }

    // ---------- 快捷操作 ----------

    private async void OnOpenHelperClicked(object? sender, EventArgs e)
    {
        var version = _version;
        if (version == null || _busy) return;

        SetBusy(true);
        try
        {
            var server = await Task.Run(() =>
                ServerManager.EnsureStarted(version, saveHelperOnly: true));
            await Navigation.PushAsync(new SaveHelperPage(version, server.SaveHelperUrl));
        }
        catch (Exception ex)
        {
            LogService.Error($"为版本 {version.Folder} 打开存档助手失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开存档助手：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnBackupSavesClicked(object? sender, EventArgs e)
    {
        var version = _version;
        if (version == null || _busy) return;

        SetBusy(true);
        try
        {
            var (path, reason) = await Task.Run(() =>
            {
                var created = BackupService.BackupSavesFromMirror(version.Folder, out var why);
                return (created, why);
            });

            if (path == null)
            {
                await PlatformServices.Dialogs.InfoAsync(reason, "立即备份存档");
                return;
            }

            await ReloadAsync();
            await PlatformServices.Dialogs.InfoAsync(
                "已备份存档：" + Path.GetFileName(path), "立即备份存档");
        }
        catch (Exception ex)
        {
            LogService.Error("立即备份存档失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("备份失败：" + ex.Message, "立即备份存档");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnExportConfigClicked(object? sender, EventArgs e)
    {
        var version = _version;
        if (version == null || _busy) return;

        SetBusy(true);
        try
        {
            var path = await Task.Run(() => BackupService.ExportModsConfig(version));
            LogService.Append("已导出模组配置：" + path);
            await ReloadAsync();

            var result = await SaveFileHelper.SaveAsync(path, Path.GetFileName(path), "application/json");
            switch (result)
            {
                case SaveFileResult.Saved:
                    await PlatformServices.Dialogs.InfoAsync("模组顺序与启停已导出。", "导出模组配置");
                    break;
                case SaveFileResult.Canceled:
                    await PlatformServices.Dialogs.InfoAsync(
                        "已取消保存。\n\n导出的文件仍保留在：\n" + path, "导出模组配置");
                    break;
                default:
                    await Share.RequestAsync(new ShareFileRequest
                    {
                        Title = "导出模组配置",
                        File = new ShareFile(path),
                    });
                    await PlatformServices.Dialogs.InfoAsync(
                        "已打开系统分享面板，可另存为文件。\n\n缓存位置：\n" + path, "导出模组配置");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("导出模组配置失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导出失败：" + ex.Message, "导出模组配置");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRestoreConfigClicked(object? sender, EventArgs e)
    {
        var version = _version;
        if (version == null || _busy) return;

        FileResult? file;
        try
        {
            file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选择模组配置（*.json）",
            });
        }
        catch (Exception ex)
        {
            LogService.Error("选择模组配置失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开文件选择器：" + ex.Message);
            return;
        }
        if (file == null) return;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"将用「{file.FileName}」里的顺序与启停覆盖当前模组列表，继续吗？",
                "还原模组配置", "覆盖", danger: true))
            return;

        SetBusy(true);
        try
        {
            // SAF 返回 content://，Core 需要真实路径，先复制到应用缓存。
            var cacheDir = Path.Combine(FileSystem.CacheDirectory, "restore");
            Directory.CreateDirectory(cacheDir);
            var target = Path.Combine(cacheDir,
                "mods-config-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            await using (var source = await file.OpenReadAsync())
            await using (var dest = File.Create(target))
            {
                await source.CopyToAsync(dest);
            }

            var count = await Task.Run(() => BackupService.RestoreModsConfig(
                version.FullPath, target, msg => LogService.Append("[模组配置] " + msg)));
            await ReloadAsync();
            await PlatformServices.Dialogs.InfoAsync(
                $"已还原 {count} 个模组的顺序与启停。", "还原模组配置");
        }
        catch (Exception ex)
        {
            LogService.Error("还原模组配置失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("还原失败：" + ex.Message, "还原模组配置");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnCreateSnapshotClicked(object? sender, EventArgs e)
    {
        var page = Navigation.NavigationStack.LastOrDefault();
        if (page == null) return;
        var note = await page.DisplayPromptAsync("快照备注",
            "可留空；备注会写进快照 manifest，还原时能看见。",
            "确定", "跳过", string.Empty, maxLength: 100);
        await CreateSnapshotAsync(SnapshotGameCheck.IsChecked, note ?? string.Empty);
    }

    private async void OnQuickSnapshotClicked(object? sender, EventArgs e) =>
        await CreateSnapshotAsync(false, string.Empty);

    private async Task CreateSnapshotAsync(bool includeGame, string note)
    {
        var version = _version;
        if (version == null || _busy) return;

        var message = $"为「{version.DisplayName}」创建快照？\n" +
                      (includeGame ? "包含游戏本体，体积较大。\n" : "只包含模组与配置，体积较小。\n") +
                      "快照保存在启动器的 backups 目录。";
        if (!await PlatformServices.Dialogs.ConfirmAsync(message, "创建快照", "创建")) return;

        SetBusy(true);
        try
        {
            LogService.Append("[快照] 正在创建…");
            var path = await Task.Run(() =>
            {
                // 有备注时直接用 PackService 写包，保留 Core 的 backups\<版本>\snapshot-*.dolpack 命名。
                if (string.IsNullOrWhiteSpace(note))
                {
                    return BackupService.CreateSnapshot(version, includeGame,
                        msg => LogService.Append("[快照] " + msg));
                }

                var dir = BackupService.VersionDir(version.Folder);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir,
                    "snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".dolpack");
                PackService.Export(version, includeGame, file, note,
                    msg => LogService.Append("[快照] " + msg));
                return file;
            });

            await ReloadAsync();
            await PlatformServices.Dialogs.InfoAsync(
                "快照已创建：" + Path.GetFileName(path), "创建快照");
        }
        catch (Exception ex)
        {
            LogService.Error("创建快照失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("创建快照失败：" + ex.Message, "创建快照");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRefreshMirrorClicked(object? sender, EventArgs e)
    {
        var version = _version;
        if (version == null) return;
        try
        {
            MirrorLabel.Text = await Task.Run(() => BackupService.DescribeMirror(version.Folder));
        }
        catch (Exception ex)
        {
            LogService.Error("读取镜像状态失败", ex);
            MirrorLabel.Text = "镜像状态读取失败：" + ex.Message;
        }
    }

    // ---------- 行操作 ----------

    private async void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is BackupEntry entry) await RestoreAsync(entry);
    }

    private async void OnRestoreClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: BackupEntry entry }) return;
        await RestoreAsync(entry);
    }

    private async Task RestoreAsync(BackupEntry entry)
    {
        var version = _version;
        if (version == null || _busy) return;

        if (entry.IsSaves)
        {
            await RestoreSavesAsync(version, entry);
            return;
        }
        if (entry.IsModsConfig)
        {
            await RestoreModsConfigAsync(version, entry);
            return;
        }
        if (!entry.IsSnapshot)
        {
            await PlatformServices.Dialogs.WarnAsync(
                $"「{entry.FileName}」不是可识别的备份类型，无法还原。", "还原备份");
            return;
        }

        await RestoreSnapshotAsync(version, entry);
    }

    private async Task RestoreSavesAsync(VersionInfo version, BackupEntry entry)
    {
        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"会用「{entry.FileName}」覆盖该版本下次打开游戏时的存档库。\n\n" +
                "不会立即打开浏览器；下次启动游戏页时自动导入。继续吗？",
                "还原存档", "还原", danger: true))
            return;

        SetBusy(true);
        try
        {
            await Task.Run(() => BackupService.QueueSavesRestore(version.Folder, entry.FullPath));
            LogService.Append("已排队还原存档：" + entry.FileName);
            await ReloadAsync();
            await PlatformServices.Dialogs.InfoAsync(
                "已排队：下次启动这个版本时会自动导入存档。", "还原存档");
        }
        catch (Exception ex)
        {
            LogService.Error("还原存档失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("还原失败：" + ex.Message, "还原存档");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RestoreModsConfigAsync(VersionInfo version, BackupEntry entry)
    {
        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"将按「{entry.FileName}」还原模组顺序与启停，继续吗？",
                "还原模组配置", "覆盖", danger: true))
            return;

        SetBusy(true);
        try
        {
            var count = await Task.Run(() => BackupService.RestoreModsConfig(
                version.FullPath, entry.FullPath, msg => LogService.Append("[模组配置] " + msg)));
            await ReloadAsync();
            await PlatformServices.Dialogs.InfoAsync(
                $"已还原 {count} 个模组的顺序与启停。", "还原模组配置");
        }
        catch (Exception ex)
        {
            LogService.Error("还原模组配置失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("还原失败：" + ex.Message, "还原模组配置");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RestoreSnapshotAsync(VersionInfo version, BackupEntry entry)
    {
        PackService.PackInspection inspection;
        try
        {
            inspection = await Task.Run(() => PackService.Inspect(entry.FullPath));
        }
        catch (Exception ex)
        {
            LogService.Error("检查快照失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法读取快照：" + ex.Message, "还原快照");
            return;
        }

        if (inspection.IsUnknown)
        {
            await PlatformServices.Dialogs.WarnAsync(
                $"「{entry.FileName}」不是可识别的版本包或模组包，无法还原。", "还原快照");
            return;
        }

        var hasGame = inspection.IsGame;
        var message = hasGame
            ? inspection.Game.Kind == PackGameKind.SingleHtml
                ? $"「{entry.FileName}」里没有找到 {Paths.HtmlName}，只找到一个 HTML。\n" +
                  "该包可能不是可用的 DoL 版本，确定导入吗？"
                : $"「{entry.FileName}」含游戏本体，将作为新版本导入到 versions 目录。继续吗？"
            : $"「{entry.FileName}」只含模组，将导入到版本「{version.DisplayName}」，同名模组会被覆盖。继续吗？";
        if (!await PlatformServices.Dialogs.ConfirmAsync(message, "还原快照", "还原", danger: true))
            return;

        SetBusy(true);
        try
        {
            LogService.Append("正在还原快照：" + entry.FileName);
            var targetPath = hasGame ? null : version.FullPath;
            var result = await Task.Run(() => PackService.Import(
                entry.FullPath, inspection, targetPath,
                msg => LogService.Append("[还原快照] " + msg)));
            await ReloadAsync();

            if (result.NewVersionFolder != null)
            {
                LogService.Append("快照已导入为新版本：" + result.NewVersionFolder);
                var open = await PlatformServices.Dialogs.ConfirmAsync(
                    $"已创建新版本 {result.NewVersionFolder}。\n\n查看这个版本吗？",
                    "还原快照", "查看版本");
                if (open) await OpenVersionAsync(result.NewVersionFolder);
            }
            else
            {
                await PlatformServices.Dialogs.InfoAsync(
                    $"快照已还原到版本「{version.DisplayName}」，共导入 {result.ModCount} 个模组。",
                    "还原快照");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("还原快照失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("还原失败：" + ex.Message, "还原快照");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>导入为新版本后跳到详情页；扫描不到时留在列表并提示。</summary>
    private async Task OpenVersionAsync(string folder)
    {
        VersionInfo? version = null;
        try
        {
            version = (await Task.Run(VersionService.Scan))
                .FirstOrDefault(v => v.Mode == "http" && v.Folder == folder);
        }
        catch (Exception ex)
        {
            LogService.Error("查找导入后的版本失败", ex);
        }

        if (version == null)
        {
            await PlatformServices.Dialogs.InfoAsync(
                "新版本已创建，可在主列表中找到：" + folder, "还原快照");
            return;
        }

        await Navigation.PushAsync(new VersionDetailPage(version));
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: BackupEntry entry } || _busy) return;
        var version = _version;
        if (version == null) return;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"删除备份「{entry.FileName}」？此操作不可恢复。",
                "删除备份", "删除", danger: true))
            return;

        SetBusy(true);
        try
        {
            // 存档在 saves 子目录，快照与模组配置在版本备份目录，按条目类型选择。
            var removed = await Task.Run(() =>
                BackupService.Delete(version.Folder, entry.FileName, entry.IsSaves));
            await ReloadAsync();
            if (!removed)
                await PlatformServices.Dialogs.WarnAsync("文件已经不存在：" + entry.FileName);
        }
        catch (Exception ex)
        {
            LogService.Error("删除备份失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("删除失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- 自动备份设置 ----------

    private void LoadBackupSettings()
    {
        _loadingSettings = true;
        try
        {
            var cfg = AppConfig.Load();
            AutoBackupSwitch.IsToggled = cfg.Backup.Enabled;
            AutoSaveSwitch.IsToggled = cfg.Backup.SaveAuto;
            IntervalStepper.Value = Math.Clamp(cfg.Backup.IntervalHours, 1, 168);
            KeepStepper.Value = Math.Clamp(cfg.Backup.Keep, 1, 20);
            IntervalLabel.Text = (int)IntervalStepper.Value + " 小时";
            KeepLabel.Text = (int)KeepStepper.Value + " 份";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SaveBackupSettings()
    {
        if (_loadingSettings) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.Backup.Enabled = AutoBackupSwitch.IsToggled;
            cfg.Backup.SaveAuto = AutoSaveSwitch.IsToggled;
            cfg.Backup.IntervalHours = (int)IntervalStepper.Value;
            cfg.Backup.Keep = (int)KeepStepper.Value;
            AppConfig.Save(cfg);
        }
        catch (Exception ex)
        {
            LogService.Error("保存备份设置失败", ex);
        }
    }

    private void OnAutoBackupToggled(object? sender, ToggledEventArgs e) => SaveBackupSettings();

    private void OnAutoSaveToggled(object? sender, ToggledEventArgs e) => SaveBackupSettings();

    private void OnIntervalChanged(object? sender, ValueChangedEventArgs e)
    {
        IntervalLabel.Text = (int)Math.Round(e.NewValue) + " 小时";
        SaveBackupSettings();
    }

    private void OnKeepChanged(object? sender, ValueChangedEventArgs e)
    {
        KeepLabel.Text = (int)Math.Round(e.NewValue) + " 份";
        SaveBackupSettings();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyOverlay.IsVisible = busy;
        Refresher.IsEnabled = !busy;
        UpdateActionsEnabled();
    }
}
