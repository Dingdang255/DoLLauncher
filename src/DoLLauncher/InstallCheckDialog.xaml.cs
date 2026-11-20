using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher;

/// <summary>批量安装时的一项：模组 + 它的安装前检查结果。</summary>
public sealed record InstallCheckItem(MarketMod Mod, InstallCheckResult Check);

/// <summary>安装前检查结果：列出待补装的前置（可取消勾选）和本地冲突。</summary>
public partial class InstallCheckDialog : Window
{
    private readonly InstallCheckResult? _check;

    public InstallCheckDialog(MarketMod mod, VersionInfo target, InstallCheckResult check)
    {
        InitializeComponent();
        SkinService.ApplyDialogBackground(this);
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

    /// <summary>批量安装：按顺序逐项列出每个模组在单选安装时会显示的内容，用分割线隔开。</summary>
    public InstallCheckDialog(IReadOnlyList<InstallCheckItem> items, VersionInfo target)
    {
        InitializeComponent();
        SkinService.ApplyDialogBackground(this);
        ThemeService.Attach(this);
        AppDialog.AttachDialogChrome(this, Width);
        UiAnimations.AttachWindow(this);

        TitleText.Text = items.Count == 1
            ? $"安装「{items[0].Mod.Name}」"
            : $"批量安装 {items.Count} 个模组";
        var subtitle = $"目标版本 {target.DisplayName}";
        if (target.GameVersion is { Length: > 0 } gameVersion) subtitle += $"（游戏 {gameVersion}）";
        SubtitleText.Text = subtitle;

        DepsSection.Visibility = Visibility.Collapsed;
        SatisfiedSection.Visibility = Visibility.Collapsed;
        ConflictSection.Visibility = Visibility.Collapsed;
        NotesSection.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        MultiPanel.Visibility = Visibility.Visible;

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) MultiPanel.Children.Add(BuildDivider());
            MultiPanel.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {items[i].Mod.Name}",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            BuildSections(items[i].Check);
        }

        FootHint.Text = "会先装各模组勾选的前置，再逐个安装；同名文件将被覆盖。";
    }

    private void BuildSections(InstallCheckResult check)
    {
        if (check.Dependencies.Count > 0)
        {
            MultiPanel.Children.Add(SectionTitle("需要补装的前置", 12));
            MultiPanel.Children.Add(new ItemsControl
            {
                ItemsSource = check.Dependencies,
                ItemTemplate = (DataTemplate)FindResource("InstallDependencyTemplate"),
                Margin = new Thickness(0, 10, 0, 0),
            });
        }

        if (check.Satisfied.Count > 0)
        {
            MultiPanel.Children.Add(SectionTitle("已满足的前置", 12));
            foreach (var text in check.Satisfied) MultiPanel.Children.Add(MutedText(text));
        }

        if (check.Conflicts.Count > 0)
        {
            MultiPanel.Children.Add(SectionTitle("可能冲突（本地已装模组）", 12));
            foreach (var conflict in check.Conflicts)
            {
                MultiPanel.Children.Add(new TextBlock
                {
                    Text = conflict.Title,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = TryFindResource("DangerBrush") as Brush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
                MultiPanel.Children.Add(MutedText(conflict.Detail));
            }
        }

        if (check.Notes.Count > 0)
        {
            MultiPanel.Children.Add(SectionTitle("提示", 12));
            foreach (var text in check.Notes) MultiPanel.Children.Add(MutedText(text));
        }

        if (!check.HasFindings)
            MultiPanel.Children.Add(MutedText("没有发现缺失前置，也没有与本地已装模组冲突。"));
    }

    private TextBlock SectionTitle(string text, double top) => new()
    {
        Text = text,
        Style = TryFindResource("SectionTitle") as Style,
        Margin = new Thickness(0, top, 0, 0),
    };

    private TextBlock MutedText(string text) => new()
    {
        Text = text,
        Style = TryFindResource("MutedText") as Style,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private Border BuildDivider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 14, 0, 14),
        Background = (TryFindResource("DividerBrush") ?? TryFindResource("LineBrush") ??
                      TryFindResource("BorderBrush")) as Brush ?? Brushes.Gray,
    };

    /// <summary>用户在对话框里勾选的前置。</summary>
    public List<MarketMod> SelectedDependencies =>
        _check?.Dependencies.Where(d => d.Selected).Select(d => d.Mod).ToList() ?? new List<MarketMod>();

    private void Install_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
