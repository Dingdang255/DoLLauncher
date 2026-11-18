using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DoLLauncher.Models;

namespace DoLLauncher.Views;

public partial class InstallCheckWindow : Window
{
    public InstallCheckWindow()
    {
        InitializeComponent();
    }

    public InstallCheckWindow(string modName, string targetText, InstallCheckResult check) : this()
    {
        DataContext = new InstallCheckDialogVm(modName, targetText, check);
    }

    public IReadOnlyList<MarketMod> SelectedDependencies =>
        (DataContext as InstallCheckDialogVm)?.SelectedDependencies ?? (IReadOnlyList<MarketMod>)Array.Empty<MarketMod>();

    private void OnInstallClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}

/// <summary>安装前检查对话框的数据。</summary>
public sealed class InstallCheckDialogVm : INotifyPropertyChanged
{
    private readonly InstallCheckResult _check;

    public InstallCheckDialogVm(string modName, string targetText, InstallCheckResult check)
    {
        _check = check;
        Title = $"安装「{modName}」";
        Subtitle = targetText;
        FootHint = check.Dependencies.Count > 0
            ? "会先装勾选的前置，再装主模组；同名文件将被覆盖。"
            : "同名文件将被覆盖，已启用的模组不会自动重新排序。";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    public string Subtitle { get; }

    public string FootHint { get; }

    public IReadOnlyList<InstallDependency> Dependencies => _check.Dependencies;

    public IReadOnlyList<string> Satisfied => _check.Satisfied;

    public IReadOnlyList<InstallConflict> Conflicts => _check.Conflicts;

    public IReadOnlyList<string> Notes => _check.Notes;

    public bool HasDependencies => _check.Dependencies.Count > 0;

    public bool HasSatisfied => _check.Satisfied.Count > 0;

    public bool HasConflicts => _check.Conflicts.Count > 0;

    public bool HasNotes => _check.Notes.Count > 0;

    public bool IsEmpty => !_check.HasFindings;

    public List<MarketMod> SelectedDependencies =>
        _check.Dependencies.Where(d => d.Selected).Select(d => d.Mod).ToList();

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
