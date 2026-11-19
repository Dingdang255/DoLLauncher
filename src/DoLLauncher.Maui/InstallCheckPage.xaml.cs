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

    /// <summary>确定后立即返回上一级，安装在后台继续；退出本页不会取消下载。</summary>
    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (_installing) return;
        _installing = true;
        StartButton.IsEnabled = false;

        // 先前置、后本体；用户取消勾选的前置不会安装
        var plan = _check.Dependencies.Where(d => d.Selected)
            .Select(d => d.Mod)
            .Concat(new[] { _mod })
            .ToList();

        await Navigation.PopAsync();
        _ = RunInstallAsync(plan);
    }

    private async Task RunInstallAsync(List<MarketMod> plan)
    {
        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                var item = plan[i];
                var isMain = ReferenceEquals(item, _mod);
                await DownloadService.InstallModAsync(item, _target.FullPath,
                    msg => LogService.Append("[市场] " + msg),
                    _ => { },
                    isMain ? _release?.Tag : null,
                    isMain ? _edition : null,
                    isMain ? _assetName : null);
            }

            LogService.Append($"[市场] 安装完成（{plan.Count} 个）");
            if (Completed != null)
                await MainThread.InvokeOnMainThreadAsync(async () => await Completed(plan.Count));
        }
        catch (Exception ex)
        {
            LogService.Error("安装市场模组失败", ex);
            await MainThread.InvokeOnMainThreadAsync(async () =>
                await PlatformServices.Dialogs.ErrorAsync("安装失败：" + ex.Message, "模组市场"));
        }
        finally
        {
            _installing = false;
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }
}
