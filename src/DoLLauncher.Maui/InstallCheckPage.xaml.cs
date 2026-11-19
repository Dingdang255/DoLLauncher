using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

/// <summary>安装前检查与安装进度页：先前置、后本体逐个下载安装。</summary>
public partial class InstallCheckPage : ContentPage
{
    private readonly MarketMod _mod;
    private readonly VersionInfo _target;
    private readonly InstallCheckResult _check;
    private readonly GameRelease? _release;
    private readonly MarketEdition? _edition;
    private readonly string? _assetName;
    private CancellationTokenSource _cts = new();

    private bool _installing;

    public InstallCheckPage(MarketMod mod, VersionInfo target, InstallCheckResult check,
        GameRelease? release, MarketEdition? edition, string? assetName)
    {
        InitializeComponent();
        _mod = mod;
        _target = target;
        _check = check;
        _release = release;
        _edition = edition;
        _assetName = assetName;

        TitleLabel.Text = $"安装「{mod.Name}」";
        var subtitle = $"目标版本 {target.DisplayName}";
        if (target.GameVersion is { Length: > 0 } gameVersion) subtitle += $"（游戏 {gameVersion}）";
        SubtitleLabel.Text = subtitle;

        BindableLayout.SetItemsSource(DepsList, check.Dependencies);
        DepsSection.IsVisible = check.Dependencies.Count > 0;

        BindableLayout.SetItemsSource(SatisfiedList, check.Satisfied);
        SatisfiedSection.IsVisible = check.Satisfied.Count > 0;

        BindableLayout.SetItemsSource(ConflictList, check.Conflicts);
        ConflictSection.IsVisible = check.Conflicts.Count > 0;

        BindableLayout.SetItemsSource(NotesList, check.Notes);
        NotesSection.IsVisible = check.Notes.Count > 0;

        EmptyLabel.IsVisible = !check.HasFindings;
        FootHint.Text = check.Dependencies.Count > 0
            ? "会先装勾选的前置，再装主模组；同名文件将被覆盖。"
            : "同名文件将被覆盖，已启用的模组不会自动重新排序。";
    }

    /// <summary>安装成功后回传实际安装的模组数量。</summary>
    public event Func<int, Task>? Completed;

    protected override void OnDisappearing()
    {
        // 页面销毁（返回 / 安装完成弹出）时取消未完成的下载。
        CancelInstall();
        base.OnDisappearing();
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (_installing) return;
        _installing = true;
        // 取消后重试时换一个新的令牌源。
        if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        ProgressPanel.IsVisible = true;
        InstallProgress.Progress = 0;
        ProgressTitle.Text = "准备安装…";
        ProgressText.Text = "";

        // 先前置、后本体；用户取消勾选的前置不会安装
        var plan = _check.Dependencies.Where(d => d.Selected)
            .Select(d => d.Mod)
            .Concat(new[] { _mod })
            .ToList();

        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var item = plan[i];
                var isMain = ReferenceEquals(item, _mod);
                ProgressTitle.Text = $"正在安装 {i + 1}/{plan.Count}";

                await DownloadService.InstallModAsync(item, _target.FullPath,
                    msg => LogService.Append("[市场] " + msg),
                    p =>
                    {
                        var overall = (i + Math.Clamp(p, 0, 1)) / Math.Max(1, plan.Count);
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            InstallProgress.Progress = overall;
                            ProgressText.Text = $"{item.Name}　{overall:P0}";
                        });
                    },
                    isMain ? _release?.Tag : null,
                    isMain ? _edition : null,
                    isMain ? _assetName : null);
            }

            InstallProgress.Progress = 1;
            LogService.Append($"[市场] 安装完成（{plan.Count} 个）");
            await Navigation.PopAsync();
            if (Completed != null) await Completed(plan.Count);
        }
        catch (OperationCanceledException)
        {
            LogService.Append("[市场] 模组安装已取消。");
            ProgressTitle.Text = "已取消";
            ProgressText.Text = "";
        }
        catch (Exception ex)
        {
            LogService.Error("安装市场模组失败", ex);
            ProgressTitle.Text = "安装失败";
            ProgressText.Text = ex.Message;
            await PlatformServices.Dialogs.ErrorAsync("安装失败：" + ex.Message, "模组市场");
        }
        finally
        {
            _installing = false;
            StartButton.IsEnabled = true;
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (_installing)
        {
            // 安装过程中取消：停掉当前下载，留在本页方便重试。
            CancelInstall();
            ProgressTitle.Text = "正在取消…";
            return;
        }
        await Navigation.PopAsync();
    }

    /// <summary>取消本页的 CTS，并停掉正在进行的模组下载任务。</summary>
    private void CancelInstall()
    {
        try { _cts.Cancel(); } catch { }
        foreach (var task in DownloadService.DownloadTasks.Where(t => t.CanCancel).ToList())
            task.Cancel();
    }
}
