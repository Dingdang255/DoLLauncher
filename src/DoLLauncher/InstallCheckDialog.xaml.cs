using System.Windows;
using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher;

/// <summary>安装前检查结果：列出待补装的前置（可取消勾选）和本地冲突。</summary>
public partial class InstallCheckDialog : Window
{
    private readonly InstallCheckResult _check;

    public InstallCheckDialog(MarketMod mod, VersionInfo target, InstallCheckResult check)
    {
        InitializeComponent();
        _check = check;
        ThemeService.Attach(this);
        AppDialog.AttachDialogChrome(this, Width);
        UiAnimations.AttachWindow(this);

        TitleText.Text = $"安装「{mod.Name}」";
        var subtitle = $"目标版本 {target.DisplayName}";
        if (target.GameVersion is { Length: > 0 } gameVersion) subtitle += $"（游戏 {gameVersion}）";
        SubtitleText.Text = subtitle;

        DepsList.ItemsSource = check.Dependencies;
        DepsSection.Visibility = check.Dependencies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SatisfiedList.ItemsSource = check.Satisfied;
        SatisfiedSection.Visibility = check.Satisfied.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ConflictList.ItemsSource = check.Conflicts;
        ConflictSection.Visibility = check.Conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        NotesList.ItemsSource = check.Notes;
        NotesSection.Visibility = check.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        EmptyText.Visibility = check.HasFindings ? Visibility.Collapsed : Visibility.Visible;
        FootHint.Text = check.Dependencies.Count > 0
            ? "会先装勾选的前置，再装主模组；同名文件将被覆盖。"
            : "同名文件将被覆盖，已启用的模组不会自动重新排序。";
    }

    /// <summary>用户在对话框里勾选的前置。</summary>
    public List<MarketMod> SelectedDependencies =>
        _check.Dependencies.Where(d => d.Selected).Select(d => d.Mod).ToList();

    private void Install_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
