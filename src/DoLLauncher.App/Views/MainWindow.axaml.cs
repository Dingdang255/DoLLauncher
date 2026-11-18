using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DoLLauncher.Services;
using DoLLauncher.ViewModels;

namespace DoLLauncher.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private bool _closeConfirmed;
    private bool _pageAnimating;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Opened += (_, _) => _viewModel.Reload();
        Closing += OnClosing;
        Closed += OnClosed;

        ModsList.SelectionChanged += OnModsSelectionChanged;
        ModsList.AddHandler(InputElement.PointerPressedEvent, OnModsPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Navigated += OnNavigated;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;
        if (!_viewModel.HasRunningServers) return;

        // 先取消本次关闭，等用户在异步弹窗里确认后再真正关闭
        e.Cancel = true;
        if (!await _viewModel.ConfirmStopAllAsync()) return;

        _closeConfirmed = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Navigated -= OnNavigated;
        _viewModel.Dispose();
    }

    /// <summary>页面切换时做一次短促的淡入，避免内容瞬间跳变。</summary>
    private async void OnNavigated()
    {
        var enabled = AppConfig.Load().General.ShowAnimations;
        if (!enabled)
        {
            _pageAnimating = false;
            PageHost.RenderTransform = null;
            PageHost.Opacity = 1;
            return;
        }
        if (_pageAnimating) return;
        _pageAnimating = true;
        try
        {
            PageHost.Opacity = 1;
            var transform = new TranslateTransform(16, 0);
            PageHost.RenderTransform = transform;
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters = { new Setter(TranslateTransform.XProperty, 16d) },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters = { new Setter(TranslateTransform.XProperty, 0d) },
                    },
                },
            };
            await animation.RunAsync(transform);
            transform.X = 0;
        }
        catch
        {
            PageHost.RenderTransform = null;
        }
        finally
        {
            _pageAnimating = false;
        }
    }

    private void OnModsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _viewModel.SetSelectedMods(ModsList.SelectedItems?.OfType<ModItemVm>() ?? Array.Empty<ModItemVm>());
    }

    /// <summary>勾选框只负责启用 / 停用，不参与列表多选，交互与 WPF 版一致。</summary>
    private void OnModsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        for (var visual = e.Source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is not CheckBox checkBox) continue;
            if (checkBox.DataContext is ModItemVm vm)
            {
                vm.Enabled = !vm.Enabled;
                e.Handled = true;
            }
            return;
        }
    }

    // Avalonia 11.3 的 DataTransfer 新 API 尚未覆盖全部桌面后端，这里继续用稳定的 Data 接口
#pragma warning disable CS0618
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files == null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
        if (paths.Count > 0) _ = _viewModel.ImportPathsAsync(paths);
        e.Handled = true;
    }
#pragma warning restore CS0618

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.LogText)) return;
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }
}
