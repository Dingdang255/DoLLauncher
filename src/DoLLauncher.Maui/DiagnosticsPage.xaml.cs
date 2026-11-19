using System.Text;
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

    public DiagnosticsPage(VersionInfo version)
    {
        InitializeComponent();
        _version = version;
        _preferredFolder = version.Folder;
        HeaderLabel.Text = version.DisplayName;
    }

    /// <summary>主界面切换到本面板时调用。</summary>
    public void Activate() => _ = ActivateAsync();

    private async Task ActivateAsync()
    {
        await LoadVersionsAsync();
        if (!_loaded) await LoadAsync();
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
        _ = LoadAsync();
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
            GameLogTitle.Text = $"{Path.GetFileName(path)}（末尾内容）";
            GameLogTitle.IsVisible = true;
            GameLogContent.IsVisible = true;
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
        SetBusy(true);
        try
        {
            var dir = Path.Combine(FileSystem.CacheDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var output = Path.Combine(dir, $"diagnostics-{SafeName(_version.Folder)}-{stamp}.zip");

            var result = await Task.Run(() => DiagnosticService.ExportPackage(_version, output,
                msg => LogService.Append("[诊断包] " + msg)));

            LogService.Append("[诊断包] 已导出：" + result);
            await Share.RequestAsync(new ShareFileRequest
            {
                Title = "分享诊断包",
                File = new ShareFile(result),
            });
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
        if (_analyzing) return;
        _analyzing = true;
        SetBusy(true);
        AnalysisView.IsVisible = true;
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

    private void SetBusy(bool busy)
    {
        BusyOverlay.IsVisible = busy;
        ExportButton.IsEnabled = !busy;
        AnalyzeButton.IsEnabled = !busy;
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
