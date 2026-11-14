using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher;

/// <summary>「可能存在问题」的处理窗口：按冲突 / 版本不匹配 / 缺少前置 / 模组重复分页展示。</summary>
public partial class ConflictDialog : Window
{
    private readonly ObservableCollection<ModConflict> _active = new();
    private readonly ObservableCollection<ModConflict> _ignored = new();
    private readonly ObservableCollection<ModVersionMismatch> _mismatches = new();
    private readonly ObservableCollection<ModMissingDependency> _missing = new();
    private readonly ObservableCollection<ModDuplicate> _duplicates = new();
    private readonly Func<string, bool> _disableByFile;
    private readonly Func<string, Task<bool>> _enableByFile;
    private readonly Action<ModConflict, bool> _setIgnored;
    private readonly Action<string> _locateDependency;
    private readonly Func<(IEnumerable<ModConflict> Conflicts,
        IEnumerable<ModMissingDependency> Missing,
        IEnumerable<ModVersionMismatch> Mismatches,
        IEnumerable<ModDuplicate> Duplicates)> _loadProblems;
    private readonly bool _launchMode;

    public ConflictDialog(IEnumerable<ModConflict> conflicts,
        IEnumerable<ModMissingDependency> missing,
        IEnumerable<ModVersionMismatch> mismatches,
        IEnumerable<ModDuplicate> duplicates,
        bool launchMode,
        Func<string, bool> disableByFile,
        Func<string, Task<bool>> enableByFile,
        Action<ModConflict, bool> setIgnored,
        Action<string> locateDependency,
        Func<(IEnumerable<ModConflict> Conflicts,
            IEnumerable<ModMissingDependency> Missing,
            IEnumerable<ModVersionMismatch> Mismatches,
            IEnumerable<ModDuplicate> Duplicates)> loadProblems)
    {
        InitializeComponent();
        _launchMode = launchMode;
        _disableByFile = disableByFile;
        _enableByFile = enableByFile;
        _setIgnored = setIgnored;
        _locateDependency = locateDependency;
        _loadProblems = loadProblems;

        ActiveList.ItemsSource = _active;
        IgnoredList.ItemsSource = _ignored;
        VersionList.ItemsSource = _mismatches;
        DependencyList.ItemsSource = _missing;
        DuplicateList.ItemsSource = _duplicates;

        foreach (var conflict in conflicts)
            (conflict.Ignored ? _ignored : _active).Add(conflict);
        foreach (var issue in mismatches) _mismatches.Add(issue);
        foreach (var issue in missing) _missing.Add(issue);
        foreach (var issue in duplicates) _duplicates.Add(issue);

        ThemeService.Attach(this);
        AppDialog.AttachDialogChrome(this, Width);
        UiAnimations.AttachWindow(this);
        RefreshUi();
    }

    /// <summary>主窗口里的模组状态变了（停用 / 启用前置）后，按最新检查结果重建各页内容。</summary>
    private void ReloadFromSource()
    {
        var (conflicts, missing, mismatches, duplicates) = _loadProblems();
        _active.Clear();
        _ignored.Clear();
        _mismatches.Clear();
        _missing.Clear();
        _duplicates.Clear();
        foreach (var conflict in conflicts)
            (conflict.Ignored ? _ignored : _active).Add(conflict);
        foreach (var issue in mismatches) _mismatches.Add(issue);
        foreach (var issue in missing) _missing.Add(issue);
        foreach (var issue in duplicates) _duplicates.Add(issue);
        RefreshUi();
    }

    private int TotalProblems => _active.Count + _mismatches.Count + _missing.Count + _duplicates.Count;

    private bool HasRed =>
        _active.Count > 0 || _missing.Count > 0 || _duplicates.Count > 0 ||
        _mismatches.Any(m => m.Severity == ProblemSeverity.Red);

    private void RefreshUi()
    {
        ActiveSection.Visibility = _active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IgnoredSection.Visibility = _ignored.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _active.Count == 0 && _ignored.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        VersionEmpty.Visibility = _mismatches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DependencyEmpty.Visibility = _missing.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DuplicateEmpty.Visibility = _duplicates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ConflictTab.Header = _active.Count > 0 ? $"模组冲突 ({_active.Count})" : "模组冲突";
        VersionTab.Header = _mismatches.Count > 0 ? $"模组版本不匹配 ({_mismatches.Count})" : "模组版本不匹配";
        DependencyTab.Header = _missing.Count > 0 ? $"模组前置缺失 ({_missing.Count})" : "模组前置缺失";
        DuplicateTab.Header = _duplicates.Count > 0 ? $"模组重复 ({_duplicates.Count})" : "模组重复";

        var clean = TotalProblems == 0;
        HeaderText.Text = clean ? "暂未检测到问题" : "可能存在问题";
        HeaderText.Foreground = (Brush)FindResource(
            clean ? "TextBrush" : HasRed ? "DangerBrush" : "WarnBrush");
        Title = HeaderText.Text;
        UpdateIntro();

        HintText.Visibility = clean ? Visibility.Collapsed : Visibility.Visible;
        IgnoreAllBtn.Visibility = _active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LaunchBtn.Visibility = _launchMode ? Visibility.Visible : Visibility.Collapsed;
        // 问题全部修完后按钮回到正常文案，不再显示「仍然启动」
        LaunchBtn.Content = clean ? "启动游戏" : "仍然启动（忽略并启动）";
        CloseBtn.Content = _launchMode ? "取消" : "关闭";
    }

    /// <summary>标题下方的说明随当前分页变化。</summary>
    private void UpdateIntro()
    {
        if (TotalProblems == 0)
        {
            IntroText.Text = "已经检查当前版本所有已启用模组：没有发现冲突、重复、版本差异过大、运行日志补丁失败或缺少前置。模组有变动时会自动重新检查。";
            return;
        }

        IntroText.Text = ProblemTabs.SelectedItem == VersionTab
            ? "按模组声明的目标游戏版本与当前版本的第三段（z）比较：相差等于设置里的阈值只作橙色提醒，超过阈值则按红色警告；同时扫描最近几份游戏日志，把补丁匹配失败、界面区域不存在这类运行期不兼容也列在这一页；只检查已启用的模组。"
            : ProblemTabs.SelectedItem == DependencyTab
                ? "缺少前置来自 boot.json 的 dependencies：完全未安装、已安装但被停用、版本不满足都按红色问题列出，可在下方启用前置或去下载中心补装。"
                : ProblemTabs.SelectedItem == DuplicateTab
                    ? "名字相同（忽略大小写与符号）的多个模组同时启用时会被 ModLoader 重复加载，容易导致脚本覆盖或事件重复触发。建议只保留最新一个，或逐个停用。"
                    : "冲突来自模组简介或 README 的声明；重复、版本不匹配与缺少前置按模组名、目标游戏版本和 boot.json 的依赖判断。这里只做提示，不会阻止启动。";
    }

    private void ProblemTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntroText != null) UpdateIntro();
    }

    private void DisableLeft_Click(object sender, RoutedEventArgs e) => DisableConflict(sender, true);

    private void DisableRight_Click(object sender, RoutedEventArgs e) => DisableConflict(sender, false);

    private void DisableConflict(object sender, bool left)
    {
        if ((sender as FrameworkElement)?.Tag is not ModConflict conflict) return;
        var file = left ? conflict.LeftFile : conflict.RightFile;
        if (file.Length == 0) return;
        if (!_disableByFile(file)) return;
        ReloadFromSource();
    }

    private void DisableVersion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModVersionMismatch issue) return;
        if (!_disableByFile(issue.ModFile)) return;
        ReloadFromSource();
    }

    private void DisableDependency_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModMissingDependency issue) return;
        if (!_disableByFile(issue.ModFile)) return;
        ReloadFromSource();
    }

    private void DisableDuplicateMember_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModDuplicateMember member) return;
        if (!_disableByFile(member.FileName)) return;
        ReloadFromSource();
    }

    /// <summary>一组重复里保留版本最新的一个，其余全部停用。</summary>
    private void KeepNewestDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModDuplicate duplicate) return;
        var changed = false;
        foreach (var member in duplicate.Members)
        {
            if (string.Equals(member.FileName, duplicate.NewestFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (_disableByFile(member.FileName)) changed = true;
        }
        if (changed) ReloadFromSource();
    }

    /// <summary>前置只是被停用时，可以直接在窗口里重新启用，再按最新状态刷新。</summary>
    private async void EnableDependency_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModMissingDependency issue) return;
        if (!await _enableByFile(issue.DependencyName)) return;
        ReloadFromSource();
    }

    private void LocateDependency_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModMissingDependency issue) return;
        _locateDependency(issue.DependencyName);
        DialogResult = false;
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModConflict conflict) return;
        _setIgnored(conflict, true);
        _active.Remove(conflict);
        _ignored.Add(conflict);
        RefreshUi();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModConflict conflict) return;
        _setIgnored(conflict, false);
        _ignored.Remove(conflict);
        _active.Add(conflict);
        RefreshUi();
    }

    private void IgnoreAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var conflict in _active.ToList())
        {
            _setIgnored(conflict, true);
            _active.Remove(conflict);
            _ignored.Add(conflict);
        }
        RefreshUi();
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        // 按约定：仍然启动等价于忽略当前所有冲突，之后不再为这些冲突弹窗
        foreach (var conflict in _active.ToList()) _setIgnored(conflict, true);
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
