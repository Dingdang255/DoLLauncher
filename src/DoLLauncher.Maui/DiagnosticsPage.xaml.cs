using System.Text;
using DoLLauncher.Core.Data;
using DoLLauncher.Maui.Controls;
using DoLLauncher.Maui.Platform;
using Microsoft.Maui.Dispatching;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class DiagnosticsPage : ContentView
{
    /// <summary>日志只展示末尾的行数与字符数，避免大文件拖慢页面。</summary>
    private const int TailLines = 200;
    private const int TailChars = 20000;

    private readonly List<VersionInfo> _versions = new();
    private readonly string? _preferredFolder;
    private VersionInfo _version;
    private bool _fillingVersion;
    private bool _loading;
    private bool _loaded;
    private bool _exporting;
    private bool _analyzing;
    private bool _active;
    private bool _liveMode;
    private bool _liveRefreshing;
    private IDispatcherTimer? _liveTimer;
    private string _gameLogPath = string.Empty;

    public DiagnosticsPage(VersionInfo version)
    {
        InitializeComponent();
        ModalShield.BlockInput(BusyOverlay);
        _version = version;
        _preferredFolder = version.Folder;
        HeaderLabel.Text = version.DisplayName;
        ModePicker.SelectedIndex = 0;
    }

    /// <summary>主界面切换到本面板时调用。</summary>
    public void Activate()
    {
        _active = true;
        _ = ActivateAsync();
    }

    /// <summary>主界面切换到其它面板时停止实时诊断轮询。</summary>
    public void Deactivate()
    {
        _active = false;
        StopLiveTimer();
    }

    private async Task ActivateAsync()
    {
        await LoadVersionsAsync();
        if (!_loaded) await LoadAsync();
        if (_liveMode) StartLiveTimer();
    }

    // ---------- 诊断模式（手动 / 实时） ----------

    private void OnModeChanged(object? sender, EventArgs e)
    {
        var live = ModePicker.SelectedIndex == 1;
        if (live == _liveMode) return;
        _liveMode = live;

        AnalyzeButton.Text = live ? "立即刷新实时诊断" : "分析最近日志";
        ModeHintLabel.Text = live
            ? "实时诊断：每 2 秒统计当前游戏实例，不读取原始日志。"
            : "手动诊断：分析最近一次游戏日志；实时诊断：每 2 秒统计当前游戏实例并给出诊断。";

        if (live)
        {
            if (_active) StartLiveTimer();
            _ = RefreshLiveAsync();
        }
        else
        {
            StopLiveTimer();
        }
    }

    private void StartLiveTimer()
    {
        if (!_liveMode || !_active) return;
        _liveTimer ??= CreateLiveTimer();
        _liveTimer.Start();
    }

    private IDispatcherTimer CreateLiveTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) => _ = RefreshLiveAsync();
        return timer;
    }

    private void StopLiveTimer() => _liveTimer?.Stop();

    private async Task RefreshLiveAsync()
    {
        if (!_liveMode || _liveRefreshing) return;
        _liveRefreshing = true;
        try
        {
            var text = await Task.Run(BuildLiveDiagnostics);
            if (!_liveMode) return;
            AnalysisView.Text = text;
            AnalysisPanel.IsVisible = true;
            LogStatusLabel.Text = GameLogService.StatusSummary(_version.FullPath);
        }
        catch (Exception ex)
        {
            LogService.Error("实时诊断失败", ex);
        }
        finally
        {
            _liveRefreshing = false;
        }
    }

    /// <summary>实时诊断内容：与 WPF 端一致，统计当前浏览器实例并列出异常分组。</summary>
    private string BuildLiveDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 实时诊断 ==");
        sb.AppendLine($"版本：{_version.DisplayName}（{_version.Folder}）");
        sb.AppendLine();

        if (!ServerManager.IsRunning(_version.Folder))
        {
            sb.AppendLine("状态：游戏未运行");
            sb.AppendLine();
            sb.AppendLine("启动该版本后，这里会自动统计当前浏览器实例的日志（每 2 秒刷新），");
            sb.AppendLine("并针对 warning / error / fatal 级别的条目生成诊断分析。");
            return sb.ToString();
        }

        var live = GameLogService.GetLive(_version.FullPath);
        sb.AppendLine("状态：游戏运行中（只统计当前浏览器实例，不展示原始日志）");
        sb.AppendLine();
        if (live == null)
        {
            sb.AppendLine("当前浏览器实例还没有日志上报：游戏页刚打开或控制台暂无输出。");
            return sb.ToString();
        }

        sb.AppendLine($"日志文件：{live.File}");
        sb.AppendLine($"最近写入：{live.LastWriteAt?.ToString("HH:mm:ss") ?? "—"}");
        if (live.Stopped) sb.AppendLine("注意：浏览器端已停止日志上报，统计可能不再增长。");
        sb.AppendLine();
        sb.AppendLine("== 统计 ==");
        sb.AppendLine($"Information：{live.Information} 条");
        sb.AppendLine($"Warning：{live.Warning} 条");
        sb.AppendLine($"Error：{live.Error} 条");
        sb.AppendLine($"Fatal：{live.Fatal} 条");
        sb.AppendLine();
        sb.AppendLine("== 诊断分析 ==");
        if (live.Findings.Count == 0)
        {
            sb.AppendLine("当前实例没有 warning / error / fatal 级别的异常条目。");
            return sb.ToString();
        }

        var index = 0;
        foreach (var finding in live.Findings)
        {
            index++;
            sb.AppendLine($"[{index}] {finding.Level} × {finding.Count}");
            sb.AppendLine($"    首次：{finding.First:HH:mm:ss}　最近：{finding.Last:HH:mm:ss}");
            foreach (var hint in finding.Hints) sb.AppendLine("    · " + hint);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task LoadVersionsAsync()
    {
        List<VersionInfo> list;
        try
        {
            list = await Task.Run(VersionService.Scan);
        }
        catch (Exception ex)
        {
            LogService.Error("扫描版本失败", ex);
            return;
        }

        list = list.Where(v => v.Mode == "http").ToList();
        var same = list.Count == _versions.Count &&
                   list.Select(v => v.Folder).SequenceEqual(_versions.Select(v => v.Folder),
                       StringComparer.OrdinalIgnoreCase);
        if (same && VersionPicker.ItemsSource != null) return;

        _versions.Clear();
        _versions.AddRange(list);

        _fillingVersion = true;
        try
        {
            var keep = (VersionPicker.SelectedItem as VersionInfo)?.Folder ?? _version.Folder;
            VersionPicker.ItemsSource = _versions;
            var index = _versions.FindIndex(v =>
                string.Equals(v.Folder, keep, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = _versions.FindIndex(v =>
                string.Equals(v.Folder, _preferredFolder, StringComparison.OrdinalIgnoreCase));
            VersionPicker.SelectedIndex = index >= 0 ? index : (_versions.Count > 0 ? 0 : -1);
        }
        finally
        {
            _fillingVersion = false;
        }
    }

    private void OnVersionChanged(object? sender, EventArgs e)
    {
        if (_fillingVersion || VersionPicker.SelectedItem is not VersionInfo version) return;
        if (string.Equals(version.Folder, _version.Folder, StringComparison.OrdinalIgnoreCase)) return;

        _version = version;
        HeaderLabel.Text = version.DisplayName;
        _loaded = false;
        EnvList.Children.Clear();
        CompatList.Children.Clear();
        GameLogList.Children.Clear();
        GameLogPanel.IsVisible = false;
        _gameLogPath = string.Empty;
        _ = LoadAsync();
        if (_liveMode) _ = RefreshLiveAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var env = await Task.Run(BuildEnvironment);
            EnvList.Children.Clear();
            foreach (var line in env) EnvList.Children.Add(MakeLine(line));

            var compat = await Task.Run(() => DiagnosticService.Compatibility(_version));
            CompatList.Children.Clear();
            if (compat.Count == 0) CompatList.Children.Add(MakeLine("未发现问题"));
            else foreach (var line in compat) CompatList.Children.Add(MakeLine(line));

            var logs = await Task.Run(() => DiagnosticService.RecentGameLogs(_version.FullPath, 5));
            GameLogList.Children.Clear();
            if (logs.Count == 0) GameLogList.Children.Add(MakeHint("暂无游戏日志"));
            else foreach (var path in logs) GameLogList.Children.Add(MakeLogButton(path));

            // 本次会话的日志上报状态（内存快照，不读日志文件）
            LogStatusLabel.Text = await Task.Run(() => GameLogService.StatusSummary(_version.FullPath));

            await LoadLauncherLogAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("加载诊断信息失败", ex);
            EnvList.Children.Clear();
            EnvList.Children.Add(MakeLine("读取失败：" + ex.Message));
            await PlatformServices.Dialogs.ErrorAsync("加载诊断信息失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
            _loaded = true;
        }
    }

    /// <summary>运行环境摘要（在后台线程收集）。</summary>
    private List<string> BuildEnvironment()
    {
        var modLoader = DiagnosticService.DetectModLoaderVersion(_version.Folder);
        var running = ServerManager.IsRunning(_version.Folder);
        return new List<string>
        {
            $"应用版本：{AppInfo.Current.VersionString}",
            $"启动器构建：{BuildInfo.Display}",
            $"启动器根目录：{Paths.Root}",
            $"ModLoader 版本：{modLoader ?? "未知"}",
            $"WebView 版本：{RuntimeEnvironment.WebViewVersion ?? "未知"}",
            $"服务器：{(running ? "运行中" : "未运行")}（端口 {_version.Port}，模式 {_version.Mode}）",
            $"游戏版本：{_version.GameVersion ?? "未知"}",
        };
    }

    private async Task LoadLauncherLogAsync()
    {
        try
        {
            var path = LogService.FilePath;
            var exists = File.Exists(path);
            LauncherLogHint.Text = exists ? $"末尾内容（{path}）" : "日志文件尚未生成。";
            var text = await Task.Run(() => ReadTail(path));
            LauncherLogView.Text = string.IsNullOrWhiteSpace(text) ? "暂无启动器日志。" : text;
        }
        catch (Exception ex)
        {
            LogService.Error("读取启动器日志失败", ex);
            LauncherLogView.Text = "读取启动器日志失败：" + ex.Message;
            await PlatformServices.Dialogs.ErrorAsync("读取启动器日志失败：" + ex.Message);
        }
    }

    private async Task ShowGameLogAsync(string path)
    {
        try
        {
            _gameLogPath = path;
            GameLogTitle.Text = $"{Path.GetFileName(path)}（末尾内容）";
            GameLogTitle.IsVisible = true;
            GameLogPanel.IsVisible = true;
            GameLogContent.Text = "读取中…";

            var text = await Task.Run(() => ReadTail(path));
            GameLogContent.Text = string.IsNullOrWhiteSpace(text) ? "（日志为空）" : text;
        }
        catch (Exception ex)
        {
            LogService.Error("读取游戏日志失败", ex);
            GameLogContent.Text = "读取失败：" + ex.Message;
            await PlatformServices.Dialogs.ErrorAsync("读取游戏日志失败：" + ex.Message);
        }
    }

    private async void OnShareLogClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!File.Exists(LogService.FilePath))
            {
                await PlatformServices.Dialogs.InfoAsync("还没有启动器日志。", "分享日志");
                return;
            }

            // 启动器日志位于应用私有 files 目录，MAUI 的 FileProvider 只映射缓存目录，
            // 先复制到缓存再分享，避免 "Failed to find configured root"。
            var dir = Path.Combine(FileSystem.CacheDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            var copyPath = Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            await Task.Run(() =>
            {
                using var source = new FileStream(LogService.FilePath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = File.Create(copyPath);
                source.CopyTo(target);
            });

            await Share.RequestAsync(new ShareFileRequest
            {
                Title = "分享日志",
                File = new ShareFile(copyPath),
            });
        }
        catch (Exception ex)
        {
            LogService.Error("分享日志失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("分享日志失败：" + ex.Message);
        }
    }

    private async void OnExportClicked(object? sender, EventArgs e)
    {
        if (_exporting) return;
        _exporting = true;
        SetBusy(true, "正在导出诊断包…");
        try
        {
            var dir = Path.Combine(FileSystem.CacheDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var output = Path.Combine(dir, $"diagnostics-{SafeName(_version.Folder)}-{stamp}.zip");

            var result = await Task.Run(() => DiagnosticService.ExportPackage(_version, output,
                msg => LogService.Append("[诊断包] " + msg)));

            LogService.Append("[诊断包] 已导出：" + result);

            // 优先用系统「保存到文件」（SAF）落到用户选的位置；不支持时再退回分享面板。
            var save = await SaveFileHelper.SaveAsync(result, Path.GetFileName(result));
            switch (save)
            {
                case SaveFileResult.Saved:
                    await PlatformServices.Dialogs.InfoAsync("诊断包已保存。", "导出诊断包");
                    break;
                case SaveFileResult.Canceled:
                    await PlatformServices.Dialogs.InfoAsync(
                        "已取消保存。\n\n导出的缓存副本仍保留在：\n" + result, "导出诊断包");
                    break;
                default:
                    await Share.RequestAsync(new ShareFileRequest
                    {
                        Title = "分享诊断包",
                        File = new ShareFile(result),
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("导出诊断包失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导出诊断包失败：" + ex.Message);
        }
        finally
        {
            _exporting = false;
            SetBusy(false);
        }
    }

    private async void OnAnalyzeClicked(object? sender, EventArgs e)
    {
        if (_liveMode)
        {
            await RefreshLiveAsync();
            return;
        }
        if (_analyzing) return;
        _analyzing = true;
        SetBusy(true, "正在分析最近日志…");
        AnalysisPanel.IsVisible = true;
        AnalysisView.Text = "正在分析最近日志…";
        try
        {
            var text = await Task.Run(() => GameLogAnalysis.Analyze(_version, 5));
            AnalysisView.Text = string.IsNullOrWhiteSpace(text) ? "没有可分析的内容。" : text;
        }
        catch (Exception ex)
        {
            LogService.Error("分析游戏日志失败", ex);
            AnalysisView.Text = "分析失败：" + ex.Message;
            await PlatformServices.Dialogs.ErrorAsync("分析日志失败：" + ex.Message);
        }
        finally
        {
            _analyzing = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string message = "处理中…")
    {
        BusyLabel.Text = message;
        BusyOverlay.IsVisible = busy;
        ExportButton.IsEnabled = !busy;
        AnalyzeButton.IsEnabled = !busy;
    }

    private async void OnOpenGameLogViewer(object? sender, TappedEventArgs e)
    {
        var title = string.IsNullOrWhiteSpace(_gameLogPath)
            ? "游戏日志"
            : Path.GetFileName(_gameLogPath);
        await TextViewerPage.ShowAsync(title, GameLogContent.Text, _gameLogPath);
    }

    private async void OnOpenAnalysisViewer(object? sender, TappedEventArgs e) =>
        await TextViewerPage.ShowAsync(_liveMode ? "实时诊断" : "日志分析", AnalysisView.Text);

    private async void OnOpenLauncherLogViewer(object? sender, TappedEventArgs e) =>
        await TextViewerPage.ShowAsync("启动器日志", LauncherLogView.Text, LogService.FilePath);

    /// <summary>清空 Mod 缓存（保留存档库）：与 WPF 诊断页的「存储维护」一致。</summary>
    private async void OnClearModCacheClicked(object? sender, EventArgs e)
    {
        if (_version.Mode != "http")
        {
            await PlatformServices.Dialogs.WarnAsync("file 版本不支持清空 Mod 缓存。", "清空 Mod 缓存");
            return;
        }

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                "将删除该版本除存档库（saves/details）以外的所有 IndexedDB：\n\n" +
                "- Mod 图片缓存、翻译缓存、模组包缓存\n- Mod 的本地设置与缓存\n\n" +
                "存档不会被删除，但此操作不可撤销。\n\n继续？",
                "清空 Mod 缓存（第 1 次确认）", "继续", danger: true))
            return;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                "再次确认：确定清空 Mod 缓存吗？\n\n清空会在下次启动该版本时自动执行，并自动重新加载一次页面。",
                "清空 Mod 缓存（第 2 次确认）", "确认清空", danger: true))
            return;

        try
        {
            SlotSaveStore.RequestCacheClear(_version.FullPath);
            LogService.Append("已登记清空 Mod 缓存（保留存档库）：" + _version.Folder);
            await PlatformServices.Dialogs.InfoAsync(
                "已登记。\n\n下次启动该版本时会先清空除存档库以外的 IndexedDB，然后自动重新加载页面。\n" +
                "如果游戏正在运行，请先关闭再重新启动。",
                "清空 Mod 缓存");
        }
        catch (Exception ex)
        {
            LogService.Error("登记清空 Mod 缓存失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("登记失败：" + ex.Message, "清空 Mod 缓存");
        }
    }

    /// <summary>读取文件末尾内容：限制行数与字符数，并允许日志被同时写入。</summary>
    private static string ReadTail(string path, int maxLines = TailLines, int maxChars = TailChars)
    {
        if (!File.Exists(path)) return string.Empty;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length == 0) return string.Empty;

        // UTF-8 中文最多 3~4 字节，按 6 倍字符上限估算需要回读的字节窗口。
        var window = (int)Math.Min(length, Math.Max(maxChars * 6L, 64 * 1024));
        stream.Position = length - window;
        var buffer = new byte[window];
        var read = stream.Read(buffer, 0, window);

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // 回读窗口从文件中间开始时，首行多半是半个字符/半行，直接丢弃。
        if (window < length && lines.Length > 1) lines = lines[1..];
        if (lines.Length > maxLines) lines = lines[^maxLines..];

        var result = string.Join("\n", lines).TrimEnd();
        return result.Length > maxChars ? "…" + result[^maxChars..] : result;
    }

    private static Label MakeLine(string text) => new()
    {
        Text = text,
        FontSize = 12,
        LineBreakMode = LineBreakMode.CharacterWrap,
    };

    private static Label MakeHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextColor = Color.FromArgb("#808080"),
    };

    private Button MakeLogButton(string path)
    {
        var info = new FileInfo(path);
        var button = new Button
        {
            Text = $"{info.Name}   {info.LastWriteTime:MM-dd HH:mm}   {FormatSize(info.Length)}",
            FontSize = 12,
            HeightRequest = 40,
            Padding = new Thickness(10, 0),
            CornerRadius = 8,
            HorizontalOptions = LayoutOptions.Fill,
        };
        button.SetAppThemeColor(Button.BackgroundColorProperty,
            Color.FromArgb("#E0E0E0"), Color.FromArgb("#3C3C3C"));
        button.SetAppThemeColor(Button.TextColorProperty,
            Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));
        button.Clicked += (_, _) => _ = ShowGameLogAsync(path);
        return button;
    }

    private static string FormatSize(long bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes < 1024 * 1024
            ? $"{bytes / 1024.0:F0} KB"
            : $"{bytes / 1024.0 / 1024.0:F1} MB";

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "version" : name;
    }
}
