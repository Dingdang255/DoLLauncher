using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DoLLauncher.ViewModels;

namespace DoLLauncher.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnSubtitleLostFocus(object? sender, RoutedEventArgs e) => ViewModel?.SaveSubtitle();

    private void OnBrowserExeLostFocus(object? sender, RoutedEventArgs e) => ViewModel?.SaveBrowserExecutable();

    private void OnBrowserArgsLostFocus(object? sender, RoutedEventArgs e) => ViewModel?.SaveBrowserArguments();

    private void OnBrowseBrowserExeClick(object? sender, RoutedEventArgs e) => ViewModel?.PickBrowserExecutable();

    private void OnAccentSliderCommit(object? sender, PointerCaptureLostEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;
        if (ReferenceEquals(sender, AccentSaturationSlider)) vm.CommitAccentSaturation();
        else vm.CommitAccentColor();
    }

    private void OnTimingSliderCommit(object? sender, PointerCaptureLostEventArgs e) => ViewModel?.SaveTiming();
}
