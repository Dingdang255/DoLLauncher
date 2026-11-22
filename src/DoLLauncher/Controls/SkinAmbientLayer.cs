using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DoLLauncher.Core.Skin;
using DoLLauncher.Services;

namespace DoLLauncher.Controls;

/// <summary>
/// 美化包环境层：渐变天空、缓慢漂移的光带与沿四边分布的粒子。
/// 不拦截鼠标、不参与布局；<see cref="Rebuild"/> 会先停止并清空上一轮动画与元素。
/// </summary>
public sealed class SkinAmbientLayer : Grid
{
    /// <summary>粒子数量硬上限（UI 推荐 0/8/16/24）。</summary>
    public const int MaxParticles = 24;

    private sealed class Particle
    {
        public required Ellipse Shape { get; init; }
        public double Fx { get; init; }
        public double Fy { get; init; }
        public double Size { get; init; }
    }

    private readonly List<Storyboard> _storyboards = new();
    private readonly List<Particle> _particles = new();
    private Canvas? _particleCanvas;

    /// <summary>只画天空 / 光带，不画粒子（用于内容下层）。</summary>
    public bool BackgroundOnly { get; set; }

    /// <summary>只画粒子，不画天空 / 光带（用于内容上层）。</summary>
    public bool ParticlesOnly { get; set; }

    /// <summary>当前粒子数量（诊断用）。</summary>
    public int ParticleCount => _particles.Count;

    /// <summary>第一个粒子的当前 X 偏移（诊断用）。</summary>
    public double SampleParticleX =>
        _particles.Count > 0 && _particles[0].Shape.RenderTransform is TranslateTransform transform
            ? transform.X
            : 0;

    /// <summary>第一个粒子的当前透明度（诊断用）。</summary>
    public double SampleParticleOpacity => _particles.Count > 0 ? _particles[0].Shape.Opacity : 0;

    public SkinAmbientLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Panel.SetZIndex(this, -1);
    }

    /// <summary>按当前配置重建天空 / 光带 / 粒子；<paramref name="motionEnabled"/> 为 false 时不创建动画。</summary>
    public void Rebuild(SkinAmbientOptions options, SkinTheme? theme, bool motionEnabled)
    {
        Stop();
        Children.Clear();
        _particles.Clear();
        _particleCanvas = null;

        options ??= new SkinAmbientOptions();
        Opacity = Math.Clamp(options.Opacity, 0, 1);

        var accent = ResolveColor(theme, new[] { "--accent", "accent", "AccentBrush" },
            Color.FromRgb(0x4C, 0x9A, 0xFF));
        var bg = ResolveColor(theme, new[] { "--bg", "bg", "BgBrush" },
            Color.FromRgb(0x10, 0x14, 0x1A));
        var bgSoft = ResolveColor(theme, new[] { "--bg-soft", "--bgSoft", "bgSoft", "BgSoftBrush" }, bg);

        var animate = motionEnabled && UiAnimations.Active;

        if (!ParticlesOnly && options.Sky)
        {
            Children.Add(new Rectangle
            {
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                Fill = CreateSkyBrush(bg, bgSoft, accent),
            });
        }

        if (!ParticlesOnly && options.Veil)
        {
            var veil = new Rectangle
            {
                // 比视口大一圈，漂移时不会露出边缘
                Margin = new Thickness(-240),
                IsHitTestVisible = false,
                Fill = CreateVeilBrush(accent),
            };
            var drift = new TranslateTransform();
            veil.RenderTransform = drift;
            Children.Add(veil);

            if (animate)
            {
                var duration = TimeSpan.FromSeconds(28);
                var storyboard = new Storyboard();
                AddDouble(storyboard, drift, TranslateTransform.XProperty, -90, 90, duration);
                AddDouble(storyboard, drift, TranslateTransform.YProperty, -50, 50, duration);
                Begin(storyboard);
            }
        }

        if (BackgroundOnly)
        {
            Visibility = Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        var count = Math.Max(0, options.Particles);
        if (count > MaxParticles)
        {
            LogService.Append($"美化包环境粒子数 {count} 超过上限 {MaxParticles}，已截断。");
            count = MaxParticles;
        }

        if (count > 0)
        {
            var canvas = new Canvas { IsHitTestVisible = false };
            canvas.SizeChanged += (_, _) => LayoutParticles();
            _particleCanvas = canvas;
            Children.Add(canvas);

            var particleColor = BlendWithWhite(accent, 0.45);
            var brush = new SolidColorBrush(WithAlpha(particleColor, 245));
            brush.Freeze();
            var random = new Random(0x51A7);

            for (var i = 0; i < count; i++)
            {
                var size = 4 + random.NextDouble() * 5;
                // 铺满整个窗口，而不是只贴在四边
                var fx = 0.04 + random.NextDouble() * 0.92;
                var fy = 0.04 + random.NextDouble() * 0.92;

                var ellipse = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = brush,
                    Opacity = 0.45,
                    IsHitTestVisible = false,
                };
                var transform = new TranslateTransform();
                ellipse.RenderTransform = transform;
                canvas.Children.Add(ellipse);
                _particles.Add(new Particle { Shape = ellipse, Fx = fx, Fy = fy, Size = size });

                if (!animate) continue;

                var duration = TimeSpan.FromSeconds(4 + (i % 5) * 0.8);
                var dx = (random.NextDouble() * 2 - 1) * 140;
                var dy = (random.NextDouble() * 2 - 1) * 200;
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                var ax = new DoubleAnimation(0, dx, duration)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = ease,
                };
                var ay = new DoubleAnimation(0, dy, duration)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = ease,
                };
                transform.BeginAnimation(TranslateTransform.XProperty, ax);
                transform.BeginAnimation(TranslateTransform.YProperty, ay);

                // 闪烁独立于漂移，避免透明度变化也过慢
                var twinkle = new DoubleAnimation(0.35, 1.0, TimeSpan.FromSeconds(1.8 + (i % 4) * 0.4))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = ease,
                };
                ellipse.BeginAnimation(UIElement.OpacityProperty, twinkle);
            }

            LayoutParticles();
        }

        Visibility = Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LogService.Append($"环境层重建：粒子层={ParticlesOnly}，背景层={BackgroundOnly}，"
                          + $"粒子数={_particles.Count}，子元素={Children.Count}，可见={Visibility}");
    }

    /// <summary>停止全部动画并隐藏。</summary>
    public void Stop()
    {
        foreach (var particle in _particles)
        {
            try
            {
                if (particle.Shape.RenderTransform is TranslateTransform transform)
                {
                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                }
                particle.Shape.BeginAnimation(UIElement.OpacityProperty, null);
            }
            catch
            {
                // 目标已卸载，忽略
            }
        }

        foreach (var storyboard in _storyboards)
        {
            try
            {
                storyboard.Stop();
            }
            catch
            {
                // 动画已结束或目标已卸载，忽略
            }
        }
        _storyboards.Clear();
        Visibility = Visibility.Collapsed;
    }

    private void LayoutParticles()
    {
        var canvas = _particleCanvas;
        if (canvas == null) return;

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        foreach (var particle in _particles)
        {
            Canvas.SetLeft(particle.Shape, particle.Fx * width - particle.Size / 2);
            Canvas.SetTop(particle.Shape, particle.Fy * height - particle.Size / 2);
        }
    }

    private void AddDouble(
        Storyboard storyboard, DependencyObject target, DependencyProperty property,
        double from, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation(from, to, duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }

    private void Begin(Storyboard storyboard)
    {
        storyboard.Begin();
        _storyboards.Add(storyboard);
    }

    private static Brush CreateSkyBrush(Color bg, Color bgSoft, Color accent)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.7, 1),
        };
        brush.GradientStops.Add(new GradientStop(bg, 0));
        brush.GradientStops.Add(new GradientStop(bgSoft, 0.55));
        brush.GradientStops.Add(new GradientStop(WithAlpha(accent, 70), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateVeilBrush(Color accent)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.1),
            EndPoint = new Point(1, 0.9),
        };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(accent, 46), 0.45));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }

    private static Color ResolveColor(SkinTheme? theme, string[] keys, Color fallback)
    {
        if (theme?.Variables != null)
        {
            foreach (var key in keys)
            {
                if (theme.Variables.TryGetValue(key, out var value)
                    && SkinColors.TryParse(value, out var color))
                {
                    return color;
                }
            }
        }

        foreach (var key in keys)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
                return brush.Color;
        }
        return fallback;
    }

    private static Color BlendWithWhite(Color color, double amount)
    {
        byte Mix(byte c) => (byte)Math.Clamp(c + (255 - c) * amount, 0, 255);
        return Color.FromArgb(color.A, Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
