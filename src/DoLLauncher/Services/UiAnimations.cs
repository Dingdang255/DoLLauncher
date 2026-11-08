using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DoLLauncher.Services;

/// <summary>
/// 全局过渡动画：页面切换、按钮悬停、列表刷新、窗口淡入与平滑滚动。
/// 可在设置里关闭；系统关闭「UI 动画」时也会自动减弱。
/// </summary>
public static class UiAnimations
{
    private static bool _initialized;

    /// <summary>设置里的动画开关。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>实际是否播放动画：开关打开且系统允许动画。</summary>
    public static bool Active => Enabled && SystemAnimationsAllowed();

    private static bool? _systemAllowed;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        EventManager.RegisterClassHandler(typeof(Button), Mouse.MouseEnterEvent,
            new MouseEventHandler(OnButtonEnter));
        EventManager.RegisterClassHandler(typeof(Button), Mouse.MouseLeaveEvent,
            new MouseEventHandler(OnButtonLeave));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewWheel));
        // 弹窗（有 Owner 的模态窗口）出现时，给主窗口罩一层半透明黑
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    private static bool SystemAnimationsAllowed()
    {
        if (_systemAllowed.HasValue) return _systemAllowed.Value;
        try
        {
            _systemAllowed = SystemParameters.ClientAreaAnimation;
        }
        catch
        {
            _systemAllowed = true;
        }
        return _systemAllowed.Value;
    }

    /// <summary>窗口打开时淡入，并轻微上移。</summary>
    public static void AttachWindow(Window window)
    {
        void Play()
        {
            if (window.Content is FrameworkElement root)
                FadeSlide(root, 0, 1, 12, 200);
        }

        if (window.IsLoaded) Play();
        else window.Loaded += (_, _) => Play();
    }

    /// <summary>切换页面时淡入并轻微位移。</summary>
    public static void ShowPage(FrameworkElement page)
    {
        if (page == null) return;
        FadeSlide(page, 0, 1, 10, 180);
    }

    /// <summary>只做透明度过渡（例如列表增删后的刷新）。</summary>
    public static void Fade(FrameworkElement element, double from, double to, int milliseconds)
    {
        if (element == null) return;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!Active)
        {
            element.Opacity = to;
            return;
        }

        element.Opacity = from;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void FadeSlide(FrameworkElement element, double from, double to,
        double offsetY, int milliseconds)
    {
        if (element == null) return;

        var transform = element.RenderTransform as TranslateTransform;
        if (transform == null || element.RenderTransform is not TranslateTransform)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);

        if (!Active)
        {
            element.Opacity = to;
            transform.Y = 0;
            return;
        }

        element.Opacity = from;
        transform.Y = offsetY;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = ease,
            });
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(offsetY, 0, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = ease,
            });
    }

    private sealed class DimState
    {
        public Adorner Adorner = null!;
        public int Depth;
    }

    private static readonly Dictionary<Window, DimState> _dims = new();

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window win) return;

        // 有 Owner 的用 Owner，没有的（少数直接 ShowDialog 的窗口）就罩住主窗口
        var target = win.Owner;
        if (target == null)
        {
            var main = Application.Current?.MainWindow;
            if (main == null || main == win) return;
            target = main;
        }
        if (target == win) return;

        DimWindow(target);
        win.Closed += (_, _) => UndimWindow(target);
    }

    /// <summary>弹窗打开时在底窗上加一层半透明黑遮罩，动画开启时淡入。</summary>
    private static void DimWindow(Window target)
    {
        if (_dims.TryGetValue(target, out var state))
        {
            state.Depth++;
            return;
        }

        if (target.Content is not UIElement root) return;
        var layer = AdornerLayer.GetAdornerLayer(root);
        if (layer == null) return;

        var dim = new DimAdorner(root);
        layer.Add(dim);
        _dims[target] = new DimState { Adorner = dim, Depth = 1 };
        Fade(dim, 0, 1, 220);
    }

    /// <summary>弹窗关闭后移除遮罩，动画开启时淡出；多个弹窗时等最后一个关闭。</summary>
    private static void UndimWindow(Window target)
    {
        if (!_dims.TryGetValue(target, out var state)) return;
        if (state.Depth > 1)
        {
            state.Depth--;
            return;
        }
        _dims.Remove(target);

        var dim = state.Adorner;
        var layer = AdornerLayer.GetAdornerLayer(dim.AdornedElement);
        if (layer == null) return;

        if (!Active)
        {
            layer.Remove(dim);
            return;
        }

        var animation = new DoubleAnimation(dim.Opacity, 0, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        animation.Completed += (_, _) =>
        {
            try { layer.Remove(dim); } catch { }
        };
        dim.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void OnButtonEnter(object sender, MouseEventArgs e)
    {
        if (!Active || sender is not Button button || !button.IsEnabled) return;
        AnimateScale(button, 1.025, 110);
    }

    private static void OnButtonLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Button button) return;
        AnimateScale(button, 1.0, 140);
    }

    private static void AnimateScale(Button button, double target, int milliseconds)
    {
        var transform = button.RenderTransform as ScaleTransform;
        if (transform == null)
        {
            transform = new ScaleTransform(1, 1);
            button.RenderTransform = transform;
            button.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (!Active)
        {
            transform.ScaleX = target;
            transform.ScaleY = target;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        transform.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(transform.ScaleX, target, duration) { EasingFunction = ease });
        transform.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(transform.ScaleY, target, duration) { EasingFunction = ease });
    }

    private static void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Active || e.Handled || e.Delta == 0) return;

        var viewer = FindScrollViewer(e.OriginalSource as DependencyObject, e.Delta > 0);
        if (viewer == null) return;

        var motion = FindMotion(viewer);
        if (motion == null)
        {
            motion = new ScrollMotion
            {
                Viewer = viewer,
                Current = viewer.VerticalOffset,
                Target = viewer.VerticalOffset,
            };
            _motions.Add(motion);
        }

        var max = Math.Max(0, viewer.ScrollableHeight);
        motion.Target = Math.Clamp(motion.Target - e.Delta * WheelStep, 0, max);
        if (Math.Abs(motion.Target - motion.Current) < 0.5) return;

        HookRenderFrame();
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? source, bool upward)
    {
        while (source != null)
        {
            if (source is ScrollViewer viewer && viewer.ScrollableHeight > 0)
            {
                var canMove = upward
                    ? viewer.VerticalOffset > 0
                    : viewer.VerticalOffset < viewer.ScrollableHeight;
                if (canMove) return viewer;
            }

            try
            {
                source = VisualTreeHelper.GetParent(source);
            }
            catch
            {
                break;
            }
        }
        return null;
    }

    /// <summary>当前正在滚动的容器及其目标位置（每格滚轮累加，不打断上一段动画）。</summary>
    private sealed class ScrollMotion
    {
        public ScrollViewer Viewer = null!;
        public double Target;
        public double Current;
    }

    /// <summary>每格滚轮对应的位移系数。</summary>
    private const double WheelStep = 0.4;

    /// <summary>指数衰减系数：越大越跟手，滚动停得越快。</summary>
    private const double ScrollDamping = 10.6;

    private static readonly List<ScrollMotion> _motions = new();
    private static bool _renderingHooked;
    private static DateTime _lastFrame;

    private static ScrollMotion? FindMotion(ScrollViewer viewer)
    {
        foreach (var motion in _motions)
        {
            if (motion.Viewer == viewer) return motion;
        }
        return null;
    }

    private static void HookRenderFrame()
    {
        if (_renderingHooked) return;
        _renderingHooked = true;
        _lastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRenderFrame;
    }

    /// <summary>
    /// 逐帧把当前位置按指数曲线逼近目标，位移是连续的小数像素，滚轮连击也不会抖动。
    /// </summary>
    private static void OnRenderFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0) return;
        if (dt > 0.05) dt = 0.05;

        var step = 1 - Math.Exp(-ScrollDamping * dt);

        for (var i = _motions.Count - 1; i >= 0; i--)
        {
            var motion = _motions[i];
            var max = Math.Max(0, motion.Viewer.ScrollableHeight);
            if (motion.Target > max) motion.Target = max;

            motion.Current += (motion.Target - motion.Current) * step;
            if (Math.Abs(motion.Target - motion.Current) < 0.4)
            {
                motion.Current = motion.Target;
                motion.Viewer.ScrollToVerticalOffset(motion.Current);
                _motions.RemoveAt(i);
                continue;
            }

            motion.Viewer.ScrollToVerticalOffset(motion.Current);
        }

        if (_motions.Count == 0)
        {
            _renderingHooked = false;
            CompositionTarget.Rendering -= OnRenderFrame;
        }
    }

    /// <summary>盖住主窗口的半透明黑色遮罩层，不参与布局。</summary>
    private sealed class DimAdorner : Adorner
    {
        private readonly Border _fill;

        public DimAdorner(UIElement adorned) : base(adorned)
        {
            _fill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            };
            AddVisualChild(_fill);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) =>
            index == 0 ? _fill : throw new ArgumentOutOfRangeException(nameof(index));

        protected override Size MeasureOverride(Size constraint)
        {
            _fill.Measure(constraint);
            return constraint;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _fill.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }
}
