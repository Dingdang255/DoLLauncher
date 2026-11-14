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
    // ---------- 备份 / 诊断 ----------

    private void SyncBackupTarget()
    {
        var current = (BackupTargetBox.SelectedItem as VersionVm)?.Info.Folder;
        if (current != null && _targets.Any(v => v.Info.Folder == current)) return;
        var preferred = _selected?.Info.Folder;
        BackupTargetBox.SelectedItem =
            _targets.FirstOrDefault(v => v.Info.Folder == preferred) ?? _targets.FirstOrDefault();
        RefreshBackups();
    }

    private VersionInfo? BackupVersion()
    {
        var vm = BackupTargetBox.SelectedItem as VersionVm;
        return vm == null || vm.Info.IsFileMode ? null : vm.Info;
    }

    private void BackupTarget_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshBackups();

    private void SyncDiagTarget()
    {
        var current = (DiagTargetBox.SelectedItem as VersionVm)?.Info.Folder;
        if (current != null && _targets.Any(v => v.Info.Folder == current)) return;
        var preferred = _selected?.Info.Folder;
        DiagTargetBox.SelectedItem =
            _targets.FirstOrDefault(v => v.Info.Folder == preferred) ?? _targets.FirstOrDefault();
    }

    private VersionInfo? DiagVersion()
    {
        var vm = DiagTargetBox.SelectedItem as VersionVm;
        return vm == null || vm.Info.IsFileMode ? null : vm.Info;
    }

    /// <summary>诊断页上的「本次会话日志上报状态」，逻辑在 GameLogService，这里只展示。</summary>
    private void RefreshGameLogStatus()
    {
        if (GameLogStatusText == null) return;
        try
        {
            var version = DiagVersion();
            GameLogStatusText.Text = version == null
                ? "最近一次日志：选择 http 版本后显示上报状态。"
                : GameLogService.StatusSummary(version.FullPath);
        }
        catch (Exception ex)
        {
            GameLogStatusText.Text = "日志上报状态读取失败：" + ex.Message;
        }
    }

    private void LoadBackupSettings()
    {
        _loadingBackupSettings = true;
        try
        {
            var cfg = AppConfig.Load();
            AutoBackupCheck.IsChecked = cfg.Backup.Enabled;
            AutoSaveCheck.IsChecked = cfg.Backup.SaveAuto;

            var keep = Math.Max(1, cfg.Backup.Keep);
            foreach (var item in BackupKeepBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Content?.ToString() == keep.ToString())
                {
                    BackupKeepBox.SelectedItem = item;
                    break;
                }
            }
            if (BackupKeepBox.SelectedItem == null) BackupKeepBox.SelectedIndex = 1;

            var logKeep = Math.Max(1, cfg.General.GameLogKeep);
            foreach (var item in GameLogKeepBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Content?.ToString() == logKeep.ToString())
                {
                    GameLogKeepBox.SelectedItem = item;
                    break;
                }
            }
            if (GameLogKeepBox.SelectedItem == null) GameLogKeepBox.SelectedIndex = 2;

            var maxMb = Math.Max(0, cfg.General.GameLogMaxMb);
            GameLogMaxBox.SelectedIndex = maxMb <= 0 ? 0 : maxMb <= 10 ? 1 : maxMb <= 30 ? 2 : 3;
            GameLogOverflowBox.SelectedIndex =
                string.Equals(cfg.General.GameLogOverflow, "stop", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            LogQueueSlider.Value = Math.Clamp(cfg.General.LogQueueKb, 1, 64);
            LogFlushSlider.Value = Math.Clamp(cfg.General.LogFlushMs, 200, 5000);
            UpdateLogUploadTexts();
        }
        finally
        {
            _loadingBackupSettings = false;
        }
    }

    private void SaveBackupSettings()
    {
        if (_loadingBackupSettings) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.Backup.Enabled = AutoBackupCheck.IsChecked == true;
            cfg.Backup.SaveAuto = AutoSaveCheck.IsChecked == true;
            var text = (BackupKeepBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "5";
            if (int.TryParse(text, out var keep)) cfg.Backup.Keep = Math.Max(1, keep);

            var logKeepText = (GameLogKeepBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "20";
            if (int.TryParse(logKeepText, out var logKeep)) cfg.General.GameLogKeep = Math.Max(1, logKeep);

            cfg.General.GameLogMaxMb = GameLogMaxBox.SelectedIndex switch
            {
                0 => 0,
                1 => 10,
                2 => 30,
                3 => 100,
                _ => cfg.General.GameLogMaxMb,
            };
            cfg.General.GameLogOverflow = GameLogOverflowBox.SelectedIndex == 1 ? "stop" : "newfile";
            AppConfig.Save(cfg);
        }
        catch (Exception ex)
        {
            Log("保存备份设置失败：" + ex.Message);
        }
    }

    private void AutoBackup_Checked(object sender, RoutedEventArgs e) => SaveBackupSettings();

    private void AutoSave_Checked(object sender, RoutedEventArgs e) => SaveBackupSettings();

    private void BackupKeep_Changed(object sender, SelectionChangedEventArgs e) => SaveBackupSettings();

    private void GameLogKeep_Changed(object sender, SelectionChangedEventArgs e) => SaveBackupSettings();

    // ---------- 设置：日志上报队列与合并间隔 ----------

    private bool LogUploadControlsReady =>
        LogQueueSlider != null && LogFlushSlider != null && LogQueueText != null && LogFlushText != null;

    private void LogUpload_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingBackupSettings || !LogUploadControlsReady) return;
        UpdateLogUploadTexts();
        if (LogQueueSlider.IsMouseCaptureWithin || LogFlushSlider.IsMouseCaptureWithin) return;
        SaveLogUpload();
    }

    private void LogUpload_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_loadingBackupSettings || !LogUploadControlsReady) return;
        SaveLogUpload();
    }

    private void UpdateLogUploadTexts()
    {
        if (!LogUploadControlsReady) return;
        LogQueueText.Text = (int)Math.Round(LogQueueSlider.Value) + " KB";
        LogFlushText.Text = (int)Math.Round(LogFlushSlider.Value) + " 毫秒";
    }

    private void SaveLogUpload()
    {
        if (!LogUploadControlsReady) return;
        try
        {
            var cfg = AppConfig.Load();
            var queue = (int)Math.Round(LogQueueSlider.Value);
            var flush = (int)Math.Round(LogFlushSlider.Value);
            if (cfg.General.LogQueueKb == queue && cfg.General.LogFlushMs == flush) return;
            cfg.General.LogQueueKb = queue;
            cfg.General.LogFlushMs = flush;
            AppConfig.Save(cfg);
            Log($"设置：日志上报队列 {queue} KB / 合并间隔 {flush} 毫秒");
        }
        catch (Exception ex)
        {
            Log("保存日志上报设置失败：" + ex.Message);
        }
    }

    private void GameLogMax_Changed(object sender, SelectionChangedEventArgs e) => SaveBackupSettings();

    private void GameLogOverflow_Changed(object sender, SelectionChangedEventArgs e) => SaveBackupSettings();

    private async void AnalyzeLog_Click(object sender, RoutedEventArgs e)
    {
        if (_analyzingLogs) return;
        var version = DiagVersion();
        if (version == null) return;
        RefreshGameLogStatus();

        _analyzingLogs = true;
        DiagBox.Clear();
        try
        {
            // 流式追加：先出头部，再一组一组补上，避免一次性塞入超长文本卡住界面
            await GameLogAnalysis.AnalyzeStreamAsync(version, block =>
            {
                DiagBox.AppendText(block + Environment.NewLine);
                DiagBox.ScrollToEnd();
            });
            Log("游戏日志分析完成。");
        }
        catch (Exception ex)
        {
            DiagBox.AppendText("分析失败：" + ex.Message);
        }
        finally
        {
            _analyzingLogs = false;
        }
    }

    private bool _analyzingLogs;

    private void RefreshBackups()
    {
        var version = BackupVersion();
        _backupEntries.Clear();
        if (version == null)
        {
            BackupCountText.Text = "请选择一个磁盘版本";
            if (BackupMirrorText != null) BackupMirrorText.Text = string.Empty;
            return;
        }

        foreach (var entry in BackupService.List(version.Folder)) _backupEntries.Add(entry);
        BackupCountText.Text = $"{_backupEntries.Count} 项";
        if (BackupMirrorText != null) BackupMirrorText.Text = BackupService.DescribeMirror(version.Folder);
        RestoreBackupBtn.IsEnabled = false;
        DeleteBackupBtn.IsEnabled = false;
    }

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var has = BackupList.SelectedItem is BackupEntry;
        RestoreBackupBtn.IsEnabled = has;
        DeleteBackupBtn.IsEnabled = has;
    }

    private void RefreshBackup_Click(object sender, RoutedEventArgs e) => RefreshBackups();

    private void BackupOpenSaves_Click(object sender, RoutedEventArgs e) => OpenSavesPage(false);

    /// <summary>静默备份：直接使用页面写回的磁盘镜像，不打开任何浏览器窗口。</summary>
    private void BackupSavesNow_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        try
        {
            var name = BackupService.BackupSavesFromMirror(version.Folder, out var reason);
            if (name == null)
            {
                AppDialog.Info(reason, "立即备份存档");
                return;
            }
            Log("已静默备份存档：" + name);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            Log("静默备份存档失败：" + ex.Message);
            AppDialog.Error("备份失败：" + ex.Message, "立即备份存档");
        }
    }

    private void OpenSavesPage(bool autoBackup)
    {
        var version = BackupVersion();
        if (version == null) return;
        try
        {
            var server = ServerManager.EnsureStarted(version, saveHelperOnly: true);
            RefreshListStates();
            BrowserService.OpenHelper(version,
                autoBackup ? server.SaveHelperUrl + "?auto=1" : server.SaveHelperUrl);
            Log(autoBackup ? "已打开自动存档备份页，完成后可关闭。" : "已打开存档助手。");
        }
        catch (Exception ex)
        {
            AppDialog.Error("打开存档助手失败：" + ex.Message);
        }
    }

    private void ExportModsConfig_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        try
        {
            var path = BackupService.ExportModsConfig(version);
            Log("已导出模组配置：" + path);
            RefreshBackups();
            AppDialog.Info("模组顺序与启停已保存：\n" + path, "导出模组配置");
        }
        catch (Exception ex)
        {
            AppDialog.Error("导出失败：" + ex.Message, "导出模组配置");
        }
    }

    private void RestoreModsConfig_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择模组配置",
            Filter = "模组配置 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        if (!AppDialog.Confirm("将用这个文件里的顺序与启停覆盖当前模组列表，继续吗？",
                "还原模组配置", "覆盖")) return;

        try
        {
            BackupService.RestoreModsConfig(version.FullPath, dialog.FileName, Log);
            LoadMods(version);
            RefreshMarketStatus();
        }
        catch (Exception ex)
        {
            AppDialog.Error("还原失败：" + ex.Message, "还原模组配置");
        }
    }

    private async void CreateSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;

        var includeGame = SnapshotGameCheck.IsChecked == true;
        if (!AppDialog.Confirm(
                $"为版本「{version.DisplayName}」创建快照？\n" +
                (includeGame ? "包含游戏本体，体积较大。\n" : "只包含模组与配置，体积较小。\n") +
                "快照保存在启动器的 backups 目录。",
                "创建快照", "创建")) return;

        CreateSnapshotBtn.IsEnabled = false;
        try
        {
            Log("正在创建快照…");
            var path = await Task.Run(() => BackupService.CreateSnapshot(version, includeGame, Log));
            Log("快照完成：" + path);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            Log("创建快照失败：" + ex.Message);
            AppDialog.Error("创建快照失败：" + ex.Message, "快照");
        }
        finally
        {
            CreateSnapshotBtn.IsEnabled = true;
        }
    }

    private void BackupList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RestoreBackup_Click(sender, e);

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        if (BackupList.SelectedItem is not BackupEntry entry) return;

        if (entry.IsSaves)
        {
            if (!AppDialog.Confirm(
                    $"会用「{entry.FileName}」覆盖该版本下次打开游戏时的存档库。\n\n" +
                    "不会立即打开浏览器；下次启动游戏页时自动导入。继续吗？",
                    "还原存档", "还原")) return;
            try
            {
                BackupService.QueueSavesRestore(version.Folder, entry.FullPath);
                Log("已排队还原存档：" + entry.FileName);
                AppDialog.Info("已加入还原队列：下次启动这个版本时会自动导入存档。", "还原存档");
            }
            catch (Exception ex)
            {
                Log("还原存档失败：" + ex.Message);
                AppDialog.Error("还原失败：" + ex.Message, "还原存档");
            }
            return;
        }

        if (entry.IsModsConfig)
        {
            if (!AppDialog.Confirm($"将按「{entry.FileName}」还原模组顺序与启停，继续吗？",
                    "还原模组配置", "覆盖")) return;
            try
            {
                BackupService.RestoreModsConfig(version.FullPath, entry.FullPath, Log);
                LoadMods(version);
                RefreshMarketStatus();
            }
            catch (Exception ex)
            {
                AppDialog.Error("还原失败：" + ex.Message, "还原模组配置");
            }
            return;
        }

        if (!entry.IsSnapshot) return;

        var hasGame = PackService.PeekHasGame(entry.FullPath);
        var message = hasGame
            ? $"「{entry.FileName}」含游戏本体，将作为新版本导入到 versions 目录。继续吗？"
            : $"「{entry.FileName}」只含模组，将导入到版本「{version.DisplayName}」，同名模组会被覆盖。继续吗？";
        if (!AppDialog.Confirm(message, "还原快照", "还原")) return;

        try
        {
            Log("正在还原快照：" + entry.FileName);
            var targetPath = hasGame ? null : version.FullPath;
            var result = await Task.Run(() => PackService.Import(entry.FullPath, targetPath, Log));
            if (result.NewVersionFolder != null)
            {
                ReloadVersions(result.NewVersionFolder);
                Log($"快照已导入为新版本：{result.NewVersionFolder}");
            }
            else
            {
                LoadMods(version);
                RefreshMarketStatus();
                RefreshBackups();
                Log($"快照已还原到版本「{version.DisplayName}」。");
            }
        }
        catch (Exception ex)
        {
            Log("还原快照失败：" + ex.Message);
            AppDialog.Error("还原失败：" + ex.Message, "还原快照");
        }
    }

    private void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        if (BackupList.SelectedItem is not BackupEntry entry) return;

        if (!AppDialog.Confirm($"确定删除备份「{entry.FileName}」吗？",
                "删除备份", "删除", danger: true)) return;
        try
        {
            BackupService.Delete(version.Folder, entry.FileName, entry.IsSaves);
            Log("已删除备份：" + entry.FileName);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            AppDialog.Error("删除失败：" + ex.Message, "删除备份");
        }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        var dir = BackupService.VersionDir(version.Folder);
        Directory.CreateDirectory(dir);
        ServerManager.OpenFolder(dir);
    }

    private void CompatCheck_Click(object sender, RoutedEventArgs e)
    {
        var version = DiagVersion();
        if (version == null) return;
        try
        {
            DiagBox.Text = string.Join(Environment.NewLine, DiagnosticService.Compatibility(version));
            DiagBox.ScrollToHome();
            Log("兼容性检查完成。");
        }
        catch (Exception ex)
        {
            DiagBox.Text = "检查失败：" + ex.Message;
        }
    }

    private async void ExportDiag_Click(object sender, RoutedEventArgs e)
    {
        var version = DiagVersion();
        if (version == null) return;

        var dialog = new SaveFileDialog
        {
            Title = "保存诊断包",
            Filter = "Zip 压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"dol-diagnostic-{version.Folder}-{DateTime.Now:yyyyMMdd-HHmm}.zip",
        };
        if (dialog.ShowDialog(this) != true) return;

        ExportDiagBtn.IsEnabled = false;
        try
        {
            var path = await Task.Run(() => DiagnosticService.ExportPackage(version, dialog.FileName, Log));
            Log("诊断包已导出：" + path);
            AppDialog.Info("诊断包已导出：\n" + path, "导出诊断包");
        }
        catch (Exception ex)
        {
            Log("导出诊断包失败：" + ex.Message);
            AppDialog.Error("导出失败：" + ex.Message, "导出诊断包");
        }
        finally
        {
            ExportDiagBtn.IsEnabled = true;
        }
    }

    private void ViewLauncherLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(LogService.FilePath))
            {
                AppDialog.Info("还没有启动器日志文件。", "启动器日志");
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogService.FilePath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppDialog.Error("打开日志失败：" + ex.Message, "启动器日志");
        }
    }

    private void OpenGameLog_Click(object sender, RoutedEventArgs e)
    {
        var version = DiagVersion();
        if (version == null) return;
        try
        {
            var log = DiagnosticService.FindLatestLog(version.FullPath);
            if (log == null)
            {
                AppDialog.Info("还没有游戏日志。\n启动一次游戏后，日志会写到版本目录的 logs\\game-日期.log。", "游戏日志");
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(log)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppDialog.Error("打开游戏日志失败：" + ex.Message, "游戏日志");
        }
    }

}
