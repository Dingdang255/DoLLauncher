using DoLLauncher.Models;
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

    private void RefreshStatus()
    {
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
        DeleteButton.IsEnabled = !_busy;
    }

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

            // 每次启动前重新核对；有问题时由用户决定是否仍然启动。
            var problems = await Task.Run(CollectProblems);
            ApplyProblems(problems);
            if (problems.Count > 0 && !await ConfirmLaunchAsync(problems)) return;

            var server = await Task.Run(() =>
            {
                ModService.Apply(_version.FullPath);
                return ServerManager.EnsureStarted(_version);
            });

            LogService.Append($"已启动 {_version.DisplayName}：{server.GameUrl}");
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

    private async void OnHelperClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var server = await Task.Run(() => ServerManager.EnsureStarted(_version, saveHelperOnly: true));
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
