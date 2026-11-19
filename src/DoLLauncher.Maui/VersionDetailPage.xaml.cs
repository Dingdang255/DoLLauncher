using DoLLauncher.Models;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace DoLLauncher.Maui;

public partial class VersionDetailPage : ContentPage
{
    private readonly VersionInfo _version;
    private readonly List<ProblemItem> _problems = new();
    private bool _busy;
    private bool _checking;

    public VersionDetailPage(VersionInfo version)
    {
        InitializeComponent();
        _version = version;
        Title = version.DisplayName;
        NameLabel.Text = version.DisplayName;
        SubLabel.Text = version.Folder;
        PathLabel.Text = version.FullPath;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 从游戏页 / 存档助手返回后可能已停服或端口变化，重新取一次状态。
        RefreshStatus();
        _ = CheckProblemsAsync();
    }

    /// <summary>供关页检测等外部动作刷新「运行中 / 未运行」状态。</summary>
    internal void RefreshStatus()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(RefreshStatus);
            return;
        }
        var running = ServerManager.IsRunning(_version.Folder);
        var helperOnly = running && ServerManager.IsHelperOnly(_version.Folder);

        GameVersionLabel.Text = string.IsNullOrWhiteSpace(_version.GameVersion) ? "未知" : _version.GameVersion;
        ModeLabel.Text = _version.IsFileMode ? "file（共享 origin）" : "http（本地服务器）";
        PortLabel.Text = _version.Port > 0 ? $"127.0.0.1:{_version.Port}" : "未分配";

        StatusLabel.Text = running ? (helperOnly ? "运行中（存档助手）" : "运行中") : "未运行";
        StatusLabel.TextColor = running ? Color.FromArgb("#2E7D32") : Color.FromArgb("#808080");

        // 与 WPF 一致：仅当游戏本体已启动时才显示「打开游戏页面」。
        LaunchButton.Text = running && !helperOnly ? "打开游戏页面" : "启动游戏";
        StopButton.IsEnabled = running && !_busy;
        StopButton.Opacity = running ? 1 : 0.4;
        LaunchButton.IsEnabled = !_busy;
        HelperButton.IsEnabled = !_busy;
        ExportButton.IsEnabled = !_busy;
        IntegrityButton.IsEnabled = !_busy && !_version.IsFileMode;
        DeleteButton.IsEnabled = !_busy;
    }

    /// <summary>该详情页是否对应指定版本目录。</summary>
    internal bool MatchesFolder(string folder) =>
        string.Equals(_version.Folder, folder, StringComparison.OrdinalIgnoreCase);

    private async Task CheckProblemsAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var problems = await Task.Run(CollectProblems);
            ApplyProblems(problems);
        }
        catch (Exception ex)
        {
            LogService.Error("检查版本问题失败", ex);
        }
        finally
        {
            _checking = false;
        }
    }

    private void ApplyProblems(List<ProblemItem> problems)
    {
        _problems.Clear();
        _problems.AddRange(problems);

        ProblemButton.Text = problems.Count == 0 ? "未发现问题" : $"可能存在问题 {problems.Count}";
        ProblemButton.IsEnabled = problems.Count > 0 && !_busy;

        if (problems.Count == 0) ProblemPanel.IsVisible = false;
        else if (ProblemPanel.IsVisible) RenderProblems();
    }

    /// <summary>汇总缺前置、版本不匹配（含运行日志）、重复模组，口径与 WPF 一致。</summary>
    private List<ProblemItem> CollectProblems()
    {
        var result = new List<ProblemItem>();
        var mods = ModService.Load(_version.FullPath);
        var cfg = AppConfig.Load();
        var threshold = ProblemService.NormalizeThreshold(cfg.General.VersionGapThreshold);

        // 声明式冲突依赖市场索引；Android 端市场在后续阶段接入，
        // 此处与 WPF「索引尚未加载」时一致，不计入冲突。
        foreach (var missing in ProblemService.MissingDependencies(mods))
            result.Add(new ProblemItem(missing.Title, missing.Detail));

        var mismatches = ProblemService.VersionMismatches(mods, _version.GameVersion, threshold);
        foreach (var issue in RuntimeIssueScanner.Scan(_version.FullPath, mods, _version.GameVersion))
        {
            // boot.json 已报过的模组不再重复列一条。
            var duplicate = issue.ModFile.Length > 0
                ? mismatches.Any(m => string.Equals(m.ModFile, issue.ModFile, StringComparison.OrdinalIgnoreCase))
                : mismatches.Any(m => m.ModFile.Length == 0 &&
                    string.Equals(m.ModName, issue.ModName, StringComparison.OrdinalIgnoreCase));
            if (!duplicate) mismatches.Add(issue);
        }

        var ignored = new HashSet<string>(
            cfg.Versions.FirstOrDefault(v => v.Folder == _version.Folder)?.IgnoredVersionIssues
                ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var issue in mismatches)
        {
            issue.Ignored = ignored.Contains(issue.Key);
            if (!issue.Ignored) result.Add(new ProblemItem(issue.Title, issue.Detail));
        }

        foreach (var duplicate in ProblemService.Duplicates(mods))
            result.Add(new ProblemItem(duplicate.Title, duplicate.Detail));

        return result;
    }

    private void RenderProblems()
    {
        ProblemList.Children.Clear();
        foreach (var item in _problems)
        {
            var detail = new Label
            {
                Text = item.Detail,
                FontSize = 12,
                LineBreakMode = LineBreakMode.CharacterWrap,
            };
            detail.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#606060"), Color.FromArgb("#B0B0B0"));

            var block = new VerticalStackLayout { Spacing = 2 };
            block.Children.Add(new Label
            {
                Text = item.Title,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.CharacterWrap,
            });
            block.Children.Add(detail);
            ProblemList.Children.Add(block);
        }
    }

    private async void OnLaunchClicked(object? sender, EventArgs e) => await LaunchAsync();

    private async Task LaunchAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            if (!File.Exists(_version.HtmlPath))
            {
                await PlatformServices.Dialogs.WarnAsync("这个版本目录里找不到游戏本体 HTML，无法启动。");
                return;
            }

            // file:// 版本：先说明 Android 端限制，用户仍可尝试（WebView 已放开文件访问）。
            if (_version.IsFileMode)
            {
                LogService.Append($"版本 {_version.Folder} 为 file:// 模式，准备用 WebView 直接打开。");
                if (!await PlatformServices.Dialogs.ConfirmAsync(
                        "这个版本是 file:// 模式（直接打开本地 HTML）。\n\n" +
                        "Android 的 WebView 对本地文件限制较多，部分资源可能无法加载。\n" +
                        "若页面空白，请转为 http 版本（导入 zip 版本包或重新下载）。\n\n仍要尝试打开吗？",
                        "文件模式", "仍然打开"))
                    return;

                // file:// 版本不走本地服务器：直接把本地 HTML 交给 WebView。
                var fileUrl = new Uri(_version.HtmlPath).AbsoluteUri;
                LogService.Append($"file:// 版本直接打开：{fileUrl}");
                await Navigation.PushAsync(new GamePage(_version, fileUrl));
                return;
            }

            // 每次启动前重新核对；有问题时由用户决定是否仍然启动。
            var problems = await Task.Run(CollectProblems);
            ApplyProblems(problems);
            if (problems.Count > 0 && !await ConfirmLaunchAsync(problems)) return;

            await TryAutoBackupAsync();

            var server = await Task.Run(() =>
            {
                ModService.Apply(_version.FullPath);
                return ServerManager.EnsureStarted(_version);
            });

            LogService.Append($"已启动 {_version.DisplayName}：{server.GameUrl}");
            LauncherStatusService.Sync();
            RefreshStatus();
            await Navigation.PushAsync(new GamePage(_version, server.GameUrl));
        }
        catch (Exception ex)
        {
            LogService.Error($"启动版本 {_version.Folder} 失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("启动失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>启动前按间隔补一次自动备份；未到间隔时 Core 会直接跳过。</summary>
    private async Task TryAutoBackupAsync()
    {
        if (!AppConfig.Load().Backup.Enabled) return;
        try
        {
            await Task.Run(() => BackupService.AutoBackup(_version,
                msg => LogService.Append("[自动备份] " + msg)));
        }
        catch (Exception ex)
        {
            // 自动备份失败不影响启动。
            LogService.Error("启动前自动备份失败", ex);
        }
    }

    private static async Task<bool> ConfirmLaunchAsync(List<ProblemItem> problems)
    {
        var lines = problems.Take(5).Select(p => "· " + p.Title);
        var message = $"检测到 {problems.Count} 个可能影响运行的问题：\n\n{string.Join("\n", lines)}";
        if (problems.Count > 5) message += $"\n…另有 {problems.Count - 5} 项";
        message += "\n\n仍要启动吗？";

        return await PlatformServices.Dialogs.ConfirmAsync(message, "可能存在问题", "仍然启动");
    }

    private async void OnStopClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        try
        {
            ServerManager.Stop(_version.Folder);
            LauncherStatusService.Sync();
            LogService.Append($"已停止：{_version.DisplayName}");
        }
        catch (Exception ex)
        {
            LogService.Error("停止版本失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("停止失败：" + ex.Message);
        }
        RefreshStatus();
    }

    private void OnProblemClicked(object? sender, EventArgs e)
    {
        if (_problems.Count == 0) return;
        ProblemPanel.IsVisible = !ProblemPanel.IsVisible;
        if (ProblemPanel.IsVisible) RenderProblems();
    }

    /// <summary>与 WPF 的「完整性检查」一致：检查模组并重新生成 modList.json。</summary>
    private async void OnIntegrityClicked(object? sender, EventArgs e)
    {
        if (_busy || _version.IsFileMode) return;
        SetBusy(true);
        try
        {
            var lines = await Task.Run(() =>
            {
                var result = ModService.Check(_version);
                ModService.Apply(_version.FullPath);
                return result;
            });

            foreach (var line in lines) LogService.Append(line);
            LogService.Append("已重新生成 modList.json。");

            var text = lines.Count == 0
                ? "未发现问题，已重新生成 modList.json。"
                : string.Join("\n", lines.Take(20)) +
                  (lines.Count > 20 ? $"\n…共 {lines.Count} 条，详见运行记录。" : string.Empty);
            await PlatformServices.Dialogs.InfoAsync(text, "完整性检查");
        }
        catch (Exception ex)
        {
            LogService.Error("完整性检查失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("完整性检查失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnHelperClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var server = await Task.Run(() => ServerManager.EnsureStarted(_version, saveHelperOnly: true));
            LauncherStatusService.Sync();
            await Navigation.PushAsync(new SaveHelperPage(_version, server.SaveHelperUrl));
        }
        catch (Exception ex)
        {
            LogService.Error("打开存档助手失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开存档助手：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---------- 导出版本包 ----------

    private void OnExportClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        ExportInfoLabel.Text = $"{_version.DisplayName}（{_version.Folder}）· 游戏版本 " +
            (string.IsNullOrWhiteSpace(_version.GameVersion) ? "未知" : _version.GameVersion);
        ExportOverlay.IsVisible = true;
    }

    private void OnExportCancelClicked(object? sender, EventArgs e) => ExportOverlay.IsVisible = false;

    private async void OnExportConfirmClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        var includeGame = ExportIncludeGameCheck.IsChecked;
        ExportOverlay.IsVisible = false;

        var note = await DisplayPromptAsync("导出备注",
            "可留空；备注会写进版本包 manifest，导入时能看见。",
            "确定", "跳过", "例如：带几个常用模组的整合包", maxLength: 200);
        note ??= string.Empty;

        var dir = Path.Combine(FileSystem.CacheDirectory, "export");
        var name = string.IsNullOrWhiteSpace(_version.Folder) ? _version.DisplayName : _version.Folder;
        var path = Path.Combine(dir, name + ".dolpack");

        SetBusy(true);
        try
        {
            Directory.CreateDirectory(dir);
            LogService.Append($"[导出] 开始导出 {_version.DisplayName} → {path}");
            await Task.Run(() => PackService.Export(_version, includeGame, path, note,
                msg => LogService.Append("[导出] " + msg)));
            LogService.Append("[导出] 完成：" + path);

            // 优先用系统「保存到文件」；不支持时退回收起面板让用户另存。
            var result = await SaveFileHelper.SaveAsync(path, Path.GetFileName(path));
            switch (result)
            {
                case SaveFileResult.Saved:
                    await PlatformServices.Dialogs.InfoAsync("版本包已保存。", "导出版本包");
                    break;
                case SaveFileResult.Canceled:
                    await PlatformServices.Dialogs.InfoAsync(
                        "已取消保存。\n\n导出的缓存副本仍保留在：\n" + path, "导出版本包");
                    break;
                default:
                    await Share.RequestAsync(new ShareFileRequest
                    {
                        Title = "导出版本包",
                        File = new ShareFile(path),
                    });
                    await PlatformServices.Dialogs.InfoAsync(
                        "已打开系统分享面板，可另存为文件。\n\n缓存位置：\n" + path, "导出版本包");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("导出版本包失败", ex);
            TryDeleteExport(path);
            await PlatformServices.Dialogs.ErrorAsync("导出失败：" + ex.Message, "导出版本包");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>导出失败时清掉半成品，避免下次导出读到残缺包。</summary>
    private static void TryDeleteExport(string path)
    {
        foreach (var candidate in new[] { path, path + ".tmp" })
        {
            try { if (File.Exists(candidate)) File.Delete(candidate); } catch { }
        }
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (_busy) return;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"确定删除版本「{_version.DisplayName}」吗？\n\n目录：{_version.FullPath}\n\n" +
                "该目录下的游戏本体和全部模组都会被永久删除。",
                "删除版本", "删除", danger: true))
            return;

        SetBusy(true);
        try
        {
            ServerManager.Stop(_version.Folder);
            LauncherStatusService.Sync();
            try
            {
                Directory.Delete(_version.FullPath, recursive: true);
            }
            catch (Exception ex)
            {
                // 目录被占用或没有权限时保留配置条目，方便用户处理后重试。
                LogService.Error("删除版本目录失败", ex);
                await PlatformServices.Dialogs.ErrorAsync(
                    "删除失败：目录可能被占用或没有权限。\n\n" + ex.Message);
                return;
            }

            VersionService.RemoveEntry(_version.Folder);
            LogService.Append("已删除：" + _version.FullPath);
            await Navigation.PopAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnCopyPathClicked(object? sender, EventArgs e)
    {
        try
        {
            await Clipboard.SetTextAsync(_version.FullPath);
            await PlatformServices.Dialogs.InfoAsync("已复制版本目录路径。");
        }
        catch (Exception ex)
        {
            LogService.Error("复制版本目录失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("复制失败：" + ex.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyOverlay.IsVisible = busy;
        RefreshStatus();
        if (!busy) ApplyProblems(_problems);
        else ProblemButton.IsEnabled = false;
    }

    private sealed record ProblemItem(string Title, string Detail);
}
