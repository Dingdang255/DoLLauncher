using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DoLLauncher.Core.Skin;

namespace DoLLauncher.Services;

/// <summary>
/// 把美化包的 <c>@keyframes</c> / <c>animation</c> / <c>transition</c> / <c>transform</c>
/// 转成 WPF Storyboard 播放，并统一管理停止与还原。
/// 所有失败都静默跳过（写一条告警），不影响其它样式。
/// </summary>
public static class SkinAnimationRunner
{
    private sealed class AnimationEntry
    {
        public List<Storyboard> Storyboards { get; } = new();
        public List<Storyboard> Transitions { get; } = new();
        public RoutedEventHandler? UnloadedHandler { get; set; }
        public string Signature { get; set; } = string.Empty;
    }

    private static readonly Dictionary<FrameworkElement, AnimationEntry> Animations =
        new(ReferenceEqualityComparer.Instance);

    private static readonly Dictionary<FrameworkElement, (Transform? Original, Point Origin)> TransformBaselines =
        new(ReferenceEqualityComparer.Instance);

    private static readonly HashSet<TransformGroup> OwnedTransforms =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>关键帧直接改写的画刷属性原始值，卸载时还原。</summary>
    private static readonly Dictionary<FrameworkElement, List<(DependencyProperty Property, object? Original)>>
        BrushBaselines = new(ReferenceEqualityComparer.Instance);

    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    // ---------- 对外 API ----------

    /// <summary>按照 <c>animation</c> 声明播放 <paramref name="spec"/> 对应的关键帧；重复调用是幂等的。</summary>
    public static void Attach(FrameworkElement element, SkinStyleSheet sheet, SkinAnimationSpec spec)
    {
        if (element == null || sheet == null) return;
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        if (spec.DurationMs <= 0) return;

        if (!sheet.Keyframes.TryGetValue(spec.Name, out var frames) || frames.Count == 0)
        {
            WarnOnce("keyframes:" + spec.Name, $"animation {spec.Name} 找不到对应的 @keyframes，已忽略。");
            return;
        }

        var signature = string.Join("|", spec.Name, spec.DurationMs, spec.DelayMs, spec.IterationCount,
            spec.Direction, spec.FillMode, frames.Count);
        if (Animations.TryGetValue(element, out var existing)
            && existing.Signature == signature
            && existing.Storyboards.Count > 0) return;

        // 只替换关键帧动画，保留过渡（状态重算时过渡可能刚刚启动）
        StopKeyframes(element);

        try
        {
            var board = BuildKeyframeStoryboard(element, frames, spec);
            if (board == null) return;

            var entry = GetEntry(element);
            entry.Signature = signature;
            entry.Storyboards.Add(board);
            EnsureUnloadedHook(element, entry);

            board.Begin(element, true);
        }
        catch (Exception ex)
        {
            WarnOnce("attach:" + spec.Name, $"动画 {spec.Name} 播放失败：{ex.Message}");
        }
    }

    /// <summary>只停止该元素的状态过渡动画，保留关键帧动画（状态重算时使用）。</summary>
    public static void StopTransitions(FrameworkElement element)
    {
        if (element == null) return;
        if (!Animations.TryGetValue(element, out var entry)) return;

        foreach (var board in entry.Transitions) StopBoard(board, element);
        entry.Transitions.Clear();
    }

    /// <summary>
    /// 只停止关键帧动画，保留过渡与变换（状态退出/重算时先回到基础层，再由调用方决定后续动画）。
    /// 停止后清除动画签名，保证同一 spec 可以重新播放；同时还原关键帧改写的画刷。
    /// </summary>
    public static void StopKeyframes(FrameworkElement element)
    {
        if (element == null) return;
        if (Animations.TryGetValue(element, out var entry))
        {
            foreach (var board in entry.Storyboards) StopBoard(board, element);
            entry.Storyboards.Clear();
            entry.Signature = string.Empty;
        }

        RestoreBrushBaselines(element);
    }

    /// <summary>停止该元素的全部动画并还原变换与被动画覆盖的画刷。</summary>
    public static void Detach(FrameworkElement element)
    {
        if (element == null) return;

        if (Animations.TryGetValue(element, out var entry))
        {
            ClearStoryboards(element, true);
            if (entry.UnloadedHandler != null) element.Unloaded -= entry.UnloadedHandler;
        }

        RestoreBrushBaselines(element);
        ResetTransform(element);
    }

    /// <summary>停止全部动画并还原所有被接管的变换。</summary>
    public static void StopAll()
    {
        foreach (var pair in Animations.ToArray())
        {
            ClearStoryboards(pair.Key);
            if (pair.Value.UnloadedHandler != null)
            {
                pair.Key.Unloaded -= pair.Value.UnloadedHandler;
                pair.Value.UnloadedHandler = null;
            }
        }
        Animations.Clear();

        foreach (var element in BrushBaselines.Keys.ToArray()) RestoreBrushBaselines(element);
        BrushBaselines.Clear();

        foreach (var element in TransformBaselines.Keys.ToArray()) ResetTransform(element);
        TransformBaselines.Clear();
        OwnedTransforms.Clear();
    }

    /// <summary>
    /// 过渡：把 <paramref name="property"/> 从当前值平滑动画到目标值。
    /// 返回 false 表示不支持（调用方应改为瞬时赋值）。
    /// </summary>
    public static bool Animate(FrameworkElement element, DependencyProperty property,
        object? from, object? to, SkinTransitionSpec? spec)
    {
        if (element == null || property == null || spec == null || spec.DurationMs <= 0) return false;

        try
        {
            var duration = TimeSpan.FromMilliseconds(spec.DurationMs);
            TimeSpan? begin = spec.DelayMs > 0 ? TimeSpan.FromMilliseconds(spec.DelayMs) : null;

            if (property.PropertyType == typeof(double)
                && TryToDouble(from, out var fromDouble) && TryToDouble(to, out var toDouble))
            {
                if (Math.Abs(fromDouble - toDouble) < 0.0001) return true;

                var board = new Storyboard { BeginTime = begin };
                var animation = new DoubleAnimation(fromDouble, toDouble, duration)
                {
                    EasingFunction = CreateEasing(spec.TimingFunction),
                };
                Storyboard.SetTarget(animation, element);
                Storyboard.SetTargetProperty(animation, new PropertyPath(property));
                board.Children.Add(animation);
                Start(element, board);
                return true;
            }

            if (typeof(Brush).IsAssignableFrom(property.PropertyType) && TryToColor(to, out var toColor))
            {
                // 当前画刷缺失或不是纯色时无法插值，直接切成目标画刷
                if (!TryToColor(from, out var fromColor))
                {
                    element.SetValue(property, new SolidColorBrush(toColor));
                    return true;
                }
                if (fromColor == toColor) return true;

                // 冻结画刷 / DynamicResource 画刷不能直接动画，克隆成可动画的 SolidColorBrush
                var brush = new SolidColorBrush(fromColor);
                element.SetValue(property, brush);

                var board = new Storyboard { BeginTime = begin };
                var animation = new ColorAnimation(fromColor, toColor, duration)
                {
                    EasingFunction = CreateEasing(spec.TimingFunction),
                };
                Storyboard.SetTarget(animation, brush);
                Storyboard.SetTargetProperty(animation, new PropertyPath(SolidColorBrush.ColorProperty));
                board.Children.Add(animation);
                Start(element, board);
                return true;
            }

            if (property.PropertyType == typeof(CornerRadius)
                && from is CornerRadius fromRadius && to is CornerRadius toRadius)
            {
                AnimateCornerRadius(element, property, fromRadius, toRadius, spec);
                return true;
            }
        }
        catch (Exception ex)
        {
            WarnOnce("transition", $"过渡动画失败：{ex.Message}");
        }

        return false;
    }

    /// <summary>把 <c>transform</c> 落到 RenderTransform（不参与布局），并记录原始变换用于还原。</summary>
    public static void SetTransform(FrameworkElement element, SkinTransformSpec spec, SkinTransitionSpec? transition)
    {
        if (element == null) return;

        try
        {
            var group = EnsureTransformGroup(element);
            var (scaleX, scaleY, angle, translateX, translateY) = Summarize(spec);
            var scale = (ScaleTransform)group.Children[0];
            var rotate = (RotateTransform)group.Children[1];
            var translate = (TranslateTransform)group.Children[2];

            if (transition is { DurationMs: > 0 })
            {
                AnimateTransformValue(element, scale, ScaleTransform.ScaleXProperty, scaleX, transition);
                AnimateTransformValue(element, scale, ScaleTransform.ScaleYProperty, scaleY, transition);
                AnimateTransformValue(element, rotate, RotateTransform.AngleProperty, angle, transition);
                AnimateTransformValue(element, translate, TranslateTransform.XProperty, translateX, transition);
                AnimateTransformValue(element, translate, TranslateTransform.YProperty, translateY, transition);
                return;
            }

            StopTransformAnimation(group);
            scale.ScaleX = scaleX;
            scale.ScaleY = scaleY;
            rotate.Angle = angle;
            translate.X = translateX;
            translate.Y = translateY;
        }
        catch (Exception ex)
        {
            WarnOnce("transform", $"transform 应用失败：{ex.Message}");
        }
    }

    /// <summary>还原元素原始的 RenderTransform / RenderTransformOrigin。</summary>
    public static void ResetTransform(FrameworkElement element)
    {
        if (element == null) return;
        if (!TransformBaselines.TryGetValue(element, out var baseline)) return;
        TransformBaselines.Remove(element);

        try
        {
            if (element.RenderTransform is TransformGroup group) OwnedTransforms.Remove(group);
            element.RenderTransform = baseline.Original;
            element.RenderTransformOrigin = baseline.Origin;
        }
        catch
        {
            // 元素已脱离可视树时忽略
        }
    }

    /// <summary>由 <see cref="SkinStyleApplier"/> 注入，用于解析关键帧里的 <c>var(--x)</c>。</summary>
    public static Func<string, string>? ValueResolver { get; set; }

    // ---------- 关键帧 → Storyboard ----------

    private static Storyboard? BuildKeyframeStoryboard(FrameworkElement element, List<SkinKeyframe> frames,
        SkinAnimationSpec spec)
    {
        var board = new Storyboard();
        var reverse = spec.Direction.Equals("reverse", StringComparison.OrdinalIgnoreCase)
                      || spec.Direction.Equals("alternate-reverse", StringComparison.OrdinalIgnoreCase);

        var opacityFrames = new List<(double Offset, double Value)>();
        var colorFrames = new Dictionary<DependencyProperty, List<(double Offset, Color Value)>>();
        var cornerFrames = new List<(double Offset, CornerRadius Value)>();
        var transformFrames = new List<(double Offset, SkinTransformSpec Value)>();

        foreach (var frame in frames)
        {
            var offset = Math.Clamp(reverse ? 1 - frame.Offset : frame.Offset, 0, 1);
            foreach (var declaration in frame.Declarations)
            {
                var property = declaration.Property.Trim().ToLowerInvariant();
                var value = Resolve(declaration.Value.Trim());

                switch (property)
                {
                    case "opacity":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var opacity))
                        {
                            opacityFrames.Add((offset, Math.Clamp(opacity, 0, 1)));
                        }
                        break;

                    case "background":
                    case "background-color":
                    case "color":
                    case "foreground":
                    case "border-color":
                    case "border":
                    case "fill":
                        var brushProperty = ResolveBrushProperty(element, property);
                        if (brushProperty == null)
                        {
                            WarnOnce("kfbrush:" + property, $"关键帧属性 {property} 在 {element.GetType().Name} 上不可用，已忽略。");
                            break;
                        }
                        if (!SkinColors.TryParse(value, out var color))
                        {
                            WarnOnce("kfcolor:" + value, $"关键帧颜色无法解析：{value}");
                            break;
                        }
                        if (!colorFrames.TryGetValue(brushProperty, out var colors))
                        {
                            colors = new List<(double, Color)>();
                            colorFrames[brushProperty] = colors;
                        }
                        colors.Add((offset, color));
                        break;

                    case "corner-radius":
                    case "border-radius":
                    case "radius":
                        if (element is Border && TryParseCornerRadius(value, out var radius))
                            cornerFrames.Add((offset, radius));
                        else
                            WarnOnce("kfradius", "关键帧 corner-radius 仅支持 Border，已忽略。");
                        break;

                    case "transform":
                        if (SkinTransformSpec.TryParse(value, out var transform))
                            transformFrames.Add((offset, transform));
                        break;

                    default:
                        WarnOnce("kfprop:" + property, $"关键帧属性暂不支持：{property}（白名单：opacity / transform / 颜色 / corner-radius）。");
                        break;
                }
            }
        }

        if (opacityFrames.Count > 0)
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            foreach (var (offset, value) in opacityFrames.OrderBy(item => item.Offset))
                animation.KeyFrames.Add(EasingDoubleKeyFrame(value, offset, spec));
            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
            board.Children.Add(animation);
        }

        foreach (var pair in colorFrames)
        {
            var brush = new SolidColorBrush(pair.Value.OrderBy(item => item.Offset).First().Value);
            RememberBrushBaseline(element, pair.Key);
            element.SetValue(pair.Key, brush);

            var animation = new ColorAnimationUsingKeyFrames();
            foreach (var (offset, value) in pair.Value.OrderBy(item => item.Offset))
                animation.KeyFrames.Add(EasingColorKeyFrame(value, offset, spec));
            Storyboard.SetTarget(animation, brush);
            Storyboard.SetTargetProperty(animation, new PropertyPath(SolidColorBrush.ColorProperty));
            board.Children.Add(animation);
        }

        if (cornerFrames.Count > 0 && element is Border)
        {
            var animation = new ObjectAnimationUsingKeyFrames();
            var from = ((Border)element).CornerRadius;
            var ordered = cornerFrames.OrderBy(item => item.Offset).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var time = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(spec.DurationMs * ordered[i].Offset));
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(
                    i == 0 && ordered[i].Offset > 0 ? from : ordered[i].Value, time));
            }
            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, new PropertyPath(Border.CornerRadiusProperty));
            board.Children.Add(animation);
        }

        if (transformFrames.Count > 0)
            AppendTransformTimelines(board, element, transformFrames, spec);

        if (board.Children.Count == 0) return null;

        board.BeginTime = spec.DelayMs > 0 ? TimeSpan.FromMilliseconds(spec.DelayMs) : null;
        if (double.IsPositiveInfinity(spec.IterationCount)) board.RepeatBehavior = RepeatBehavior.Forever;
        else if (spec.IterationCount > 1) board.RepeatBehavior = new RepeatBehavior(spec.IterationCount);
        board.AutoReverse = spec.Direction.Equals("alternate", StringComparison.OrdinalIgnoreCase)
                            || spec.Direction.Equals("alternate-reverse", StringComparison.OrdinalIgnoreCase);
        board.FillBehavior = spec.FillMode.Equals("forwards", StringComparison.OrdinalIgnoreCase)
                             || spec.FillMode.Equals("both", StringComparison.OrdinalIgnoreCase)
            ? FillBehavior.HoldEnd
            : FillBehavior.Stop;
        foreach (var child in board.Children)
            if (child is Timeline timeline) timeline.FillBehavior = board.FillBehavior;
        return board;
    }

    private static void AppendTransformTimelines(Storyboard board, FrameworkElement element,
        List<(double Offset, SkinTransformSpec Value)> frames, SkinAnimationSpec spec)
    {
        var group = EnsureTransformGroup(element);
        var scale = (ScaleTransform)group.Children[0];
        var rotate = (RotateTransform)group.Children[1];
        var translate = (TranslateTransform)group.Children[2];

        AddTransformTrack(board, scale, ScaleTransform.ScaleXProperty,
            frames.Select(item => (item.Offset, Summarize(item.Value).ScaleX)).ToList(), spec);
        AddTransformTrack(board, scale, ScaleTransform.ScaleYProperty,
            frames.Select(item => (item.Offset, Summarize(item.Value).ScaleY)).ToList(), spec);
        AddTransformTrack(board, rotate, RotateTransform.AngleProperty,
            frames.Select(item => (item.Offset, Summarize(item.Value).Angle)).ToList(), spec);
        AddTransformTrack(board, translate, TranslateTransform.XProperty,
            frames.Select(item => (item.Offset, Summarize(item.Value).TranslateX)).ToList(), spec);
        AddTransformTrack(board, translate, TranslateTransform.YProperty,
            frames.Select(item => (item.Offset, Summarize(item.Value).TranslateY)).ToList(), spec);
    }

    private static void AddTransformTrack(Storyboard board, DependencyObject target,
        DependencyProperty property, List<(double Offset, double Value)> frames, SkinAnimationSpec spec)
    {
        if (frames.All(item => Math.Abs(item.Value - frames[0].Value) < 0.0001)) return;

        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var (offset, value) in frames.OrderBy(item => item.Offset))
            animation.KeyFrames.Add(EasingDoubleKeyFrame(value, offset, spec));
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        board.Children.Add(animation);
    }

    private static EasingDoubleKeyFrame EasingDoubleKeyFrame(double value, double offset, SkinAnimationSpec spec)
        => new(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(spec.DurationMs * offset)))
        {
            EasingFunction = CreateEasing(spec.TimingFunction),
        };

    private static EasingColorKeyFrame EasingColorKeyFrame(Color value, double offset, SkinAnimationSpec spec)
        => new(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(spec.DurationMs * offset)))
        {
            EasingFunction = CreateEasing(spec.TimingFunction),
        };

    // ---------- transform ----------

    private static TransformGroup EnsureTransformGroup(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup existing
            && existing.Children.Count == 3
            && existing.Children[0] is ScaleTransform
            && existing.Children[1] is RotateTransform
            && existing.Children[2] is TranslateTransform
            && OwnedTransforms.Contains(existing))
        {
            return existing;
        }

        if (!TransformBaselines.ContainsKey(element))
            TransformBaselines[element] = (element.RenderTransform, element.RenderTransformOrigin);

        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var translate = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(translate);

        element.RenderTransform = group;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        OwnedTransforms.Add(group);
        return group;
    }

    private static (double ScaleX, double ScaleY, double Angle, double TranslateX, double TranslateY) Summarize(
        SkinTransformSpec spec)
    {
        double scaleX = 1, scaleY = 1, angle = 0, translateX = 0, translateY = 0;
        foreach (var operation in spec.Operations)
        {
            switch (operation.Kind)
            {
                case SkinTransformKind.Translate:
                    translateX += operation.X;
                    translateY += operation.Y;
                    break;
                case SkinTransformKind.Rotate:
                    angle += operation.Angle;
                    break;
                case SkinTransformKind.Scale:
                    scaleX *= operation.ScaleX;
                    scaleY *= operation.ScaleY;
                    break;
            }
        }
        return (scaleX, scaleY, angle, translateX, translateY);
    }

    private static void AnimateTransformValue(FrameworkElement element, Animatable target,
        DependencyProperty property, double to, SkinTransitionSpec spec)
    {
        var from = property == ScaleTransform.ScaleXProperty ? ((ScaleTransform)target).ScaleX
            : property == ScaleTransform.ScaleYProperty ? ((ScaleTransform)target).ScaleY
            : property == RotateTransform.AngleProperty ? ((RotateTransform)target).Angle
            : property == TranslateTransform.XProperty ? ((TranslateTransform)target).X
            : ((TranslateTransform)target).Y;

        if (Math.Abs(from - to) < 0.0001) return;

        var board = new Storyboard
        {
            BeginTime = spec.DelayMs > 0 ? TimeSpan.FromMilliseconds(spec.DelayMs) : null,
        };
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(spec.DurationMs))
        {
            EasingFunction = CreateEasing(spec.TimingFunction),
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        board.Children.Add(animation);
        Start(element, board);
    }

    private static void StopTransformAnimation(TransformGroup group)
    {
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case ScaleTransform scale:
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    break;
                case RotateTransform rotate:
                    rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                    break;
                case TranslateTransform translate:
                    translate.BeginAnimation(TranslateTransform.XProperty, null);
                    translate.BeginAnimation(TranslateTransform.YProperty, null);
                    break;
            }
        }
    }

    // ---------- 角半径 / 工具 ----------

    private static void AnimateCornerRadius(FrameworkElement element, DependencyProperty property,
        CornerRadius from, CornerRadius to, SkinTransitionSpec spec)
    {
        // WPF 没有 CornerRadiusAnimation，按采样帧离散逼近（每帧位置按缓动计算）
        const int steps = 12;
        var ease = CreateEasing(spec.TimingFunction);
        var animation = new ObjectAnimationUsingKeyFrames();
        for (var i = 0; i <= steps; i++)
        {
            var progress = ease?.Ease((double)i / steps) ?? (double)i / steps;
            var value = new CornerRadius(
                Lerp(from.TopLeft, to.TopLeft, progress),
                Lerp(from.TopRight, to.TopRight, progress),
                Lerp(from.BottomRight, to.BottomRight, progress),
                Lerp(from.BottomLeft, to.BottomLeft, progress));
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(value,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(spec.DurationMs * i / steps))));
        }

        var board = new Storyboard
        {
            BeginTime = spec.DelayMs > 0 ? TimeSpan.FromMilliseconds(spec.DelayMs) : null,
            FillBehavior = FillBehavior.HoldEnd,
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        board.Children.Add(animation);
        Start(element, board);
    }

    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;

    /// <summary>
    /// WPF 没有任意 cubic-bezier：linear 用无缓动，ease* 映射到 Quad/Cubic，
    /// cubic-bezier(...) 与 steps(...) 统一用 CubicEase 近似。
    /// </summary>
    private static EasingFunctionBase? CreateEasing(string? timingFunction)
    {
        var text = (timingFunction ?? "ease").Trim().ToLowerInvariant();
        if (text == "linear") return null;
        if (text.StartsWith("cubic-bezier(", StringComparison.Ordinal)
            || text.StartsWith("steps(", StringComparison.Ordinal)
            || text is "step-start" or "step-end")
        {
            return new CubicEase { EasingMode = EasingMode.EaseInOut };
        }

        return text switch
        {
            "ease-in" => new QuadraticEase { EasingMode = EasingMode.EaseIn },
            "ease-out" => new CubicEase { EasingMode = EasingMode.EaseOut },
            "ease-in-out" => new CubicEase { EasingMode = EasingMode.EaseInOut },
            _ => new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
    }

    private static DependencyProperty? ResolveBrushProperty(FrameworkElement element, string property)
    {
        var name = property switch
        {
            "background" or "background-color" => "Background",
            "color" or "foreground" => "Foreground",
            "border-color" or "border" => "BorderBrush",
            "fill" => "Fill",
            _ => null,
        };
        return name == null ? null : FindProperty(element.GetType(), name);
    }

    private static DependencyProperty? FindProperty(Type type, string name)
    {
        var fieldName = name + "Property";
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            var field = current.GetField(fieldName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.FlattenHierarchy);
            if (field?.GetValue(null) is DependencyProperty dependencyProperty) return dependencyProperty;
        }
        return null;
    }

    private static bool TryParseCornerRadius(string text, out CornerRadius radius)
    {
        radius = default;
        var parts = text.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) return false;

        var numbers = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var raw = parts[i].Trim().ToLowerInvariant();
            if (raw.EndsWith("px", StringComparison.Ordinal)) raw = raw[..^2];
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return false;
        }

        radius = numbers.Length switch
        {
            1 => new CornerRadius(numbers[0]),
            2 => new CornerRadius(numbers[0], numbers[1], numbers[0], numbers[1]),
            3 => new CornerRadius(numbers[0], numbers[1], numbers[2], numbers[1]),
            _ => new CornerRadius(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
        return true;
    }

    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case double number:
                result = number;
                return true;
            case float single:
                result = single;
                return true;
            case int integer:
                result = integer;
                return true;
            case long l:
                result = l;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryToColor(object? value, out Color color)
    {
        switch (value)
        {
            case Color direct:
                color = direct;
                return true;
            case SolidColorBrush { Color: var brushColor }:
                color = brushColor;
                return true;
            default:
                color = Colors.Transparent;
                return false;
        }
    }

    private static string Resolve(string value) => ValueResolver?.Invoke(value) ?? value;

    /// <summary>启动一个过渡 Storyboard，登记在 Transitions 里，状态重算时只停这一批。</summary>
    private static void Start(FrameworkElement element, Storyboard board)
    {
        var entry = GetEntry(element);
        EnsureUnloadedHook(element, entry);
        entry.Transitions.Add(board);
        board.Begin(element, true);
    }

    /// <summary>元素卸载时自动停止动画，避免离屏元素继续占用合成线程。</summary>
    private static void EnsureUnloadedHook(FrameworkElement element, AnimationEntry entry)
    {
        if (entry.UnloadedHandler != null) return;

        var target = element;
        entry.UnloadedHandler = (_, _) =>
        {
            if (!target.IsLoaded) Detach(target);
        };
        element.Unloaded += entry.UnloadedHandler;
    }

    private static AnimationEntry GetEntry(FrameworkElement element)
    {
        if (Animations.TryGetValue(element, out var entry)) return entry;
        entry = new AnimationEntry();
        Animations[element] = entry;
        return entry;
    }

    private static void StopBoard(Storyboard board, FrameworkElement element)
    {
        try
        {
            board.Stop(element);
        }
        catch
        {
            try { board.Stop(); } catch { }
        }
    }

    private static void ClearStoryboards(FrameworkElement element, bool dropEntry = false)
    {
        if (!Animations.TryGetValue(element, out var entry)) return;

        foreach (var board in entry.Storyboards) StopBoard(board, element);
        foreach (var board in entry.Transitions) StopBoard(board, element);
        entry.Storyboards.Clear();
        entry.Transitions.Clear();
        entry.Signature = string.Empty;
        if (dropEntry) Animations.Remove(element);
    }

    private static void RememberBrushBaseline(FrameworkElement element, DependencyProperty property)
    {
        if (!BrushBaselines.TryGetValue(element, out var list))
        {
            list = new List<(DependencyProperty Property, object? Original)>();
            BrushBaselines[element] = list;
        }
        foreach (var item in list)
            if (item.Property == property) return;
        list.Add((property, element.ReadLocalValue(property)));
    }

    private static void RestoreBrushBaselines(FrameworkElement element)
    {
        if (!BrushBaselines.TryGetValue(element, out var list)) return;
        BrushBaselines.Remove(element);

        foreach (var (property, original) in list)
        {
            try
            {
                // 关键帧改写前若是动态资源，重新挂接，避免还原后丢失主题绑定
                if (original is System.Windows.Expression expression
                    && TryReattachResourceReference(element, property, expression)) continue;

                if (original is System.Windows.Expression || original == DependencyProperty.UnsetValue)
                    element.ClearValue(property);
                else
                    element.SetValue(property, original);
            }
            catch
            {
                // 属性只读或原始值无法回写时忽略
            }
        }
    }

    private static bool TryReattachResourceReference(FrameworkElement element, DependencyProperty property,
        object expression)
    {
        try
        {
            var type = expression.GetType();
            var field = type.GetField("_resourceKey",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?? type.GetField("ResourceKey",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Public);
            if (field?.GetValue(expression) is not { } key) return false;

            element.SetResourceReference(property, key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WarnOnce(string key, string message)
    {
        if (!Warned.Add(key)) return;
        LogService.Warn(message);
    }
}
