using DoLLauncher.Core.Skin;
using DoLLauncher.Maui.Controls;
using DoLLauncher.Services;
using Microsoft.Maui.Controls.Shapes;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 安卓端的 <c>@keyframes</c> / <c>animation</c> / <c>transition</c> / <c>transform</c> 播放器。
/// MAUI 没有 Storyboard，这里用共享的 16ms 定时器逐帧计算并写入控件属性；
/// 停止时还原首次播放前记录的基础值。所有失败只写一条告警，不影响其它样式。
/// </summary>
public static class MauiSkinAnimationRunner
{
    private enum Slot
    {
        Opacity,
        Background,
        Foreground,
        BorderColor,
        Radius,
        Scale,
        Rotation,
        TranslationX,
        TranslationY,
    }

    private sealed class KeyframeEntry
    {
        public VisualElement Element { get; init; } = null!;
        public string Signature { get; init; } = string.Empty;
        public SkinAnimationSpec Spec { get; init; } = new();
        public List<(double Offset, double Value)> Opacity { get; } = new();
        public List<(double Offset, Color Value)> Background { get; } = new();
        public List<(double Offset, Color Value)> Foreground { get; } = new();
        public List<(double Offset, Color Value)> BorderColor { get; } = new();
        public List<(double Offset, double Value)> Radius { get; } = new();
        public List<(double Offset, double Scale, double Rotation, double X, double Y)> Transform { get; } = new();
        public Dictionary<Slot, object?> Baseline { get; } = new();
        public double ElapsedMs;
    }

    private sealed class TweenEntry
    {
        public VisualElement Element { get; init; } = null!;
        public Action<double> Apply { get; init; } = null!;
        public double From { get; init; }
        public double To { get; init; }
        public double DurationMs { get; init; }
        public double DelayMs { get; init; }
        public string Timing { get; init; } = "ease";
        public double ElapsedMs;
    }

    private sealed record TransformBaseline(double Scale, double Rotation, double X, double Y);

    private static readonly Dictionary<VisualElement, KeyframeEntry> Keyframes =
        new(ReferenceEqualityComparer.Instance);

    private static readonly List<TweenEntry> Tweens = new();

    private static readonly Dictionary<VisualElement, TransformBaseline> TransformBaselines =
        new(ReferenceEqualityComparer.Instance);

    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    private static IDispatcherTimer? _timer;
    private static DateTime _lastTick = DateTime.UtcNow;

    // ---------- 对外 API ----------

    /// <summary>按 <c>animation</c> 声明播放关键帧；相同签名重复调用是幂等的。</summary>
    public static void Attach(VisualElement element, SkinStyleSheet sheet, SkinAnimationSpec spec)
    {
        if (element == null || sheet == null) return;
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        if (spec.DurationMs <= 0) return;
        if (!UiMotion.Enabled) return;

        if (!sheet.Keyframes.TryGetValue(spec.Name, out var frames) || frames.Count == 0)
        {
            WarnOnce("keyframes:" + spec.Name, $"animation {spec.Name} 找不到对应的 @keyframes，已忽略。");
            return;
        }

        var signature = string.Join("|", spec.Name, spec.DurationMs, spec.DelayMs, spec.IterationCount,
            spec.Direction, spec.FillMode, frames.Count);
        if (Keyframes.TryGetValue(element, out var existing) && existing.Signature == signature) return;

        StopKeyframes(element);

        var entry = BuildEntry(element, frames, spec, signature);
        if (entry == null) return;

        Keyframes[element] = entry;
        ApplyProgress(entry, 0);
        StartTimer();
    }

    /// <summary>只停止该元素的状态过渡，保留关键帧动画。</summary>
    public static void StopTransitions(VisualElement element)
    {
        if (element == null) return;
        Tweens.RemoveAll(tween => ReferenceEquals(tween.Element, element));
    }

    /// <summary>停止关键帧动画并还原首次播放前的基础值。</summary>
    public static void StopKeyframes(VisualElement element)
    {
        if (element == null) return;
        if (!Keyframes.TryGetValue(element, out var entry)) return;
        Keyframes.Remove(element);
        RestoreBaseline(entry);
    }

    /// <summary>停止该元素的全部动画并还原变换。</summary>
    public static void Detach(VisualElement element)
    {
        if (element == null) return;
        StopTransitions(element);
        StopKeyframes(element);
        ResetTransform(element);
    }

    /// <summary>停止全部动画（卸载美化包时调用）。</summary>
    public static void StopAll()
    {
        foreach (var entry in Keyframes.Values.ToArray()) RestoreBaseline(entry);
        Keyframes.Clear();
        Tweens.Clear();

        foreach (var element in TransformBaselines.Keys.ToArray()) ResetTransform(element);
        TransformBaselines.Clear();
    }

    /// <summary>数值过渡；不支持时返回 false（调用方应直接赋值）。</summary>
    public static bool AnimateDouble(VisualElement element, Action<double> apply, double from, double to,
        SkinTransitionSpec? spec)
    {
        if (element == null || apply == null || spec == null || spec.DurationMs <= 0) return false;
        if (!UiMotion.Enabled) return false;
        if (Math.Abs(from - to) < 0.0001) return false;

        AddTween(new TweenEntry
        {
            Element = element,
            Apply = apply,
            From = from,
            To = to,
            DurationMs = spec.DurationMs,
            DelayMs = spec.DelayMs,
            Timing = spec.TimingFunction,
        });
        return true;
    }

    /// <summary>颜色过渡；不支持时返回 false（调用方应直接赋值）。</summary>
    public static bool AnimateColor(VisualElement element, Action<Color> apply, Color from, Color to,
        SkinTransitionSpec? spec)
    {
        if (element == null || apply == null || spec == null || spec.DurationMs <= 0) return false;
        if (!UiMotion.Enabled) return false;
        if (from == to) return false;

        AddTween(new TweenEntry
        {
            Element = element,
            Apply = progress => apply(LerpColor(from, to, progress)),
            From = 0,
            To = 1,
            DurationMs = spec.DurationMs,
            DelayMs = spec.DelayMs,
            Timing = spec.TimingFunction,
        });
        return true;
    }

    /// <summary>把 <c>transform</c> 落到 Scale / Rotation / Translation 上，并记录原始值。</summary>
    public static void SetTransform(VisualElement element, SkinTransformSpec spec, SkinTransitionSpec? transition)
    {
        if (element == null) return;

        var (scaleX, _, rotation, x, y) = Summarize(spec);
        if (!TransformBaselines.ContainsKey(element))
            TransformBaselines[element] = new TransformBaseline(element.Scale, element.Rotation,
                element.TranslationX, element.TranslationY);

        if (transition is { DurationMs: > 0 })
        {
            AnimateDouble(element, value => element.Scale = value, element.Scale, scaleX, transition);
            AnimateDouble(element, value => element.Rotation = value, element.Rotation, rotation, transition);
            AnimateDouble(element, value => element.TranslationX = value, element.TranslationX, x, transition);
            AnimateDouble(element, value => element.TranslationY = value, element.TranslationY, y, transition);
            return;
        }

        element.Scale = scaleX;
        element.Rotation = rotation;
        element.TranslationX = x;
        element.TranslationY = y;
    }

    /// <summary>还原元素原始的缩放 / 旋转 / 位移。</summary>
    public static void ResetTransform(VisualElement element)
    {
        if (element == null) return;
        if (!TransformBaselines.TryGetValue(element, out var baseline)) return;
        TransformBaselines.Remove(element);

        try
        {
            element.Scale = baseline.Scale;
            element.Rotation = baseline.Rotation;
            element.TranslationX = baseline.X;
            element.TranslationY = baseline.Y;
        }
        catch
        {
            // 元素已脱离可视树时忽略
        }
    }

    // ---------- 关键帧解析 ----------

    private static KeyframeEntry? BuildEntry(VisualElement element, List<SkinKeyframe> frames,
        SkinAnimationSpec spec, string signature)
    {
        var entry = new KeyframeEntry { Element = element, Spec = spec, Signature = signature };
        var reverse = spec.Direction.Equals("reverse", StringComparison.OrdinalIgnoreCase)
                      || spec.Direction.Equals("alternate-reverse", StringComparison.OrdinalIgnoreCase);

        foreach (var frame in frames)
        {
            var offset = Math.Clamp(reverse ? 1 - frame.Offset : frame.Offset, 0, 1);
            foreach (var declaration in frame.Declarations)
            {
                var property = declaration.Property.Trim().ToLowerInvariant();
                var value = MauiSkinStyler.ResolveSkinValue(declaration.Value.Trim());

                switch (property)
                {
                    case "opacity":
                        if (TryDouble(value, out var opacity))
                            entry.Opacity.Add((offset, Math.Clamp(opacity, 0, 1)));
                        break;

                    case "background":
                    case "background-color":
                        if (MauiSkinStyler.TryParseMauiColor(value, out var background))
                            entry.Background.Add((offset, background));
                        break;

                    case "color":
                    case "foreground":
                        if (MauiSkinStyler.TryParseMauiColor(value, out var foreground))
                            entry.Foreground.Add((offset, foreground));
                        break;

                    case "border-color":
                    case "border":
                    case "stroke":
                        if (MauiSkinStyler.TryParseMauiColor(value, out var border))
                            entry.BorderColor.Add((offset, border));
                        break;

                    case "corner-radius":
                    case "border-radius":
                    case "radius":
                        if (MauiSkinStyler.TryParseLength(value, out var radius))
                            entry.Radius.Add((offset, Math.Max(0, radius)));
                        break;

                    case "transform":
                        if (SkinTransformSpec.TryParse(value, out var transform))
                        {
                            var (sx, _, rotate, x, y) = Summarize(transform);
                            entry.Transform.Add((offset, sx, rotate, x, y));
                        }
                        break;

                    default:
                        WarnOnce("kfprop:" + property,
                            $"关键帧属性暂不支持：{property}（白名单：opacity / transform / 颜色 / corner-radius）。");
                        break;
                }
            }
        }

        var hasAny = entry.Opacity.Count > 0 || entry.Background.Count > 0 || entry.Foreground.Count > 0
                     || entry.BorderColor.Count > 0 || entry.Radius.Count > 0 || entry.Transform.Count > 0;
        return hasAny ? entry : null;
    }

    // ---------- 逐帧推进 ----------

    private static void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var delta = Math.Clamp((now - _lastTick).TotalMilliseconds, 0, 100);
        _lastTick = now;

        if (Keyframes.Count > 0)
        {
            foreach (var pair in Keyframes.ToArray())
            {
                var entry = pair.Value;
                var alive = Advance(entry, delta);
                if (!alive) Keyframes.Remove(pair.Key);
            }
        }

        if (Tweens.Count > 0)
        {
            for (var i = Tweens.Count - 1; i >= 0; i--)
            {
                var tween = Tweens[i];
                tween.ElapsedMs += delta;
                var time = tween.ElapsedMs - tween.DelayMs;
                if (time < 0) continue;

                var progress = tween.DurationMs <= 0 ? 1 : Math.Clamp(time / tween.DurationMs, 0, 1);
                try
                {
                    tween.Apply(tween.From + (tween.To - tween.From) * Ease(tween.Timing, progress));
                }
                catch
                {
                    // 单个过渡失败不影响其它动画
                }

                if (progress >= 1) Tweens.RemoveAt(i);
            }
        }

        if (Keyframes.Count == 0 && Tweens.Count == 0) _timer?.Stop();
    }

    /// <summary>推进一帧；返回 false 表示动画已结束（fill 需要时才保留最后一帧）。</summary>
    private static bool Advance(KeyframeEntry entry, double deltaMs)
    {
        entry.ElapsedMs += deltaMs;
        var delay = entry.Spec.DelayMs;
        if (entry.ElapsedMs < delay) return true;

        var time = entry.ElapsedMs - delay;
        var duration = entry.Spec.DurationMs;
        if (duration <= 0) return false;

        var iterations = entry.Spec.IterationCount;
        var infinite = double.IsPositiveInfinity(iterations);
        var index = (int)Math.Floor(time / duration);
        var local = (time % duration) / duration;

        if (!infinite && index >= iterations)
        {
            local = 1;
            ApplyProgress(entry, local);
            var hold = entry.Spec.FillMode.Equals("forwards", StringComparison.OrdinalIgnoreCase)
                       || entry.Spec.FillMode.Equals("both", StringComparison.OrdinalIgnoreCase);
            if (!hold) RestoreBaseline(entry);
            return false;
        }

        var direction = entry.Spec.Direction.ToLowerInvariant();
        if (direction is "alternate" or "alternate-reverse")
        {
            var odd = index % 2 == 1;
            if (direction == "alternate-reverse") odd = !odd;
            if (odd) local = 1 - local;
        }

        ApplyProgress(entry, Math.Clamp(local, 0, 1));
        return true;
    }

    private static void ApplyProgress(KeyframeEntry entry, double progress)
    {
        var element = entry.Element;

        if (entry.Opacity.Count > 0)
            SetDouble(entry, Slot.Opacity, () => element.Opacity,
                value => element.Opacity = value, Lerp(entry.Opacity, progress));

        if (entry.Background.Count > 0)
            SetColor(entry, Slot.Background, () => element.BackgroundColor,
                value => element.BackgroundColor = value, LerpColor(entry.Background, progress));

        if (entry.Foreground.Count > 0)
            SetColor(entry, Slot.Foreground, () => CurrentTextColor(element),
                value => ApplyTextColor(element, value), LerpColor(entry.Foreground, progress));

        if (entry.BorderColor.Count > 0)
            SetColor(entry, Slot.BorderColor, () => CurrentBorderColor(element),
                value => ApplyBorderColor(element, value), LerpColor(entry.BorderColor, progress));

        if (entry.Radius.Count > 0)
            SetDouble(entry, Slot.Radius, () => CurrentRadius(element),
                value => ApplyRadius(element, value), Lerp(entry.Radius, progress));

        if (entry.Transform.Count > 0)
        {
            var (scale, rotation, x, y) = LerpTransform(entry.Transform, progress);
            SetDouble(entry, Slot.Scale, () => element.Scale, value => element.Scale = value, scale);
            SetDouble(entry, Slot.Rotation, () => element.Rotation, value => element.Rotation = value, rotation);
            SetDouble(entry, Slot.TranslationX, () => element.TranslationX,
                value => element.TranslationX = value, x);
            SetDouble(entry, Slot.TranslationY, () => element.TranslationY,
                value => element.TranslationY = value, y);
        }
    }

    private static void SetDouble(KeyframeEntry entry, Slot slot, Func<object?> read, Action<double> apply,
        double value)
    {
        if (!entry.Baseline.ContainsKey(slot)) entry.Baseline[slot] = read();
        apply(value);
    }

    private static void SetColor(KeyframeEntry entry, Slot slot, Func<object?> read, Action<Color> apply,
        Color value)
    {
        if (!entry.Baseline.ContainsKey(slot)) entry.Baseline[slot] = read();
        apply(value);
    }

    private static void RestoreBaseline(KeyframeEntry entry)
    {
        var element = entry.Element;
        foreach (var pair in entry.Baseline)
        {
            try
            {
                switch (pair.Key)
                {
                    case Slot.Opacity:
                        if (pair.Value is double opacity) element.Opacity = opacity;
                        break;
                    case Slot.Background:
                        if (pair.Value is Color background) element.BackgroundColor = background;
                        break;
                    case Slot.Foreground:
                        if (pair.Value is Color foreground) ApplyTextColor(element, foreground);
                        break;
                    case Slot.BorderColor:
                        if (pair.Value is Color border) ApplyBorderColor(element, border);
                        break;
                    case Slot.Radius:
                        if (pair.Value is double radius) ApplyRadius(element, radius);
                        break;
                    case Slot.Scale:
                        if (pair.Value is double scale) element.Scale = scale;
                        break;
                    case Slot.Rotation:
                        if (pair.Value is double rotation) element.Rotation = rotation;
                        break;
                    case Slot.TranslationX:
                        if (pair.Value is double x) element.TranslationX = x;
                        break;
                    case Slot.TranslationY:
                        if (pair.Value is double y) element.TranslationY = y;
                        break;
                }
            }
            catch
            {
                // 元素已销毁时忽略
            }
        }
        entry.Baseline.Clear();
    }

    private static void AddTween(TweenEntry tween)
    {
        Tweens.Add(tween);
        StartTimer();
    }

    private static void StartTimer()
    {
        if (_timer != null)
        {
            if (!_timer.IsRunning) _timer.Start();
            _lastTick = DateTime.UtcNow;
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    // ---------- 属性读写 ----------

    private static Color CurrentTextColor(VisualElement element) => element switch
    {
        Label label => label.TextColor ?? Colors.Transparent,
        Button button => button.TextColor ?? Colors.Transparent,
        Entry entry => entry.TextColor ?? Colors.Transparent,
        Editor editor => editor.TextColor ?? Colors.Transparent,
        Picker picker => picker.TextColor ?? Colors.Transparent,
        _ => Colors.Transparent,
    };

    private static void ApplyTextColor(VisualElement element, Color color)
    {
        switch (element)
        {
            case Label label: label.TextColor = color; break;
            case Button button: button.TextColor = color; break;
            case Entry entry: entry.TextColor = color; break;
            case Editor editor: editor.TextColor = color; break;
            case Picker picker: picker.TextColor = color; break;
        }
    }

    private static Color CurrentBorderColor(VisualElement element) => element switch
    {
        Border { Stroke: SolidColorBrush brush } => brush.Color,
        Button button => button.BorderColor ?? Colors.Transparent,
        _ => Colors.Transparent,
    };

    private static void ApplyBorderColor(VisualElement element, Color color)
    {
        switch (element)
        {
            case Border border:
                border.Stroke = new SolidColorBrush(color);
                break;
            case Button button:
                button.BorderColor = color;
                break;
        }
    }

    private static double CurrentRadius(VisualElement element) => element switch
    {
        Border { StrokeShape: RoundRectangle round } => round.CornerRadius.TopLeft,
        Button button => button.CornerRadius,
        _ => 0,
    };

    private static void ApplyRadius(VisualElement element, double radius)
    {
        switch (element)
        {
            case Border border:
                border.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(radius) };
                break;
            case Button button:
                button.CornerRadius = (int)Math.Round(radius);
                break;
        }
    }

    // ---------- 数值工具 ----------

    private static double Lerp(List<(double Offset, double Value)> frames, double progress)
    {
        if (frames.Count == 0) return 0;
        var ordered = frames.OrderBy(item => item.Offset).ToList();
        if (progress <= ordered[0].Offset) return ordered[0].Value;

        for (var i = 1; i < ordered.Count; i++)
        {
            if (progress > ordered[i].Offset) continue;
            var from = ordered[i - 1];
            var to = ordered[i];
            var span = to.Offset - from.Offset;
            var t = span <= 0.0001 ? 1 : (progress - from.Offset) / span;
            return from.Value + (to.Value - from.Value) * t;
        }
        return ordered[^1].Value;
    }

    private static Color LerpColor(List<(double Offset, Color Value)> frames, double progress)
    {
        if (frames.Count == 0) return Colors.Transparent;
        var ordered = frames.OrderBy(item => item.Offset).ToList();
        if (progress <= ordered[0].Offset) return ordered[0].Value;

        for (var i = 1; i < ordered.Count; i++)
        {
            if (progress > ordered[i].Offset) continue;
            var from = ordered[i - 1];
            var to = ordered[i];
            var span = to.Offset - from.Offset;
            var t = span <= 0.0001 ? 1 : (progress - from.Offset) / span;
            return LerpColor(from.Value, to.Value, t);
        }
        return ordered[^1].Value;
    }

    private static Color LerpColor(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgba(
            (float)(from.Red + (to.Red - from.Red) * t),
            (float)(from.Green + (to.Green - from.Green) * t),
            (float)(from.Blue + (to.Blue - from.Blue) * t),
            (float)(from.Alpha + (to.Alpha - from.Alpha) * t));
    }

    private static (double Scale, double Rotation, double X, double Y) LerpTransform(
        List<(double Offset, double Scale, double Rotation, double X, double Y)> frames, double progress)
    {
        var ordered = frames.OrderBy(item => item.Offset).ToList();
        if (ordered.Count == 0) return (1, 0, 0, 0);
        if (progress <= ordered[0].Offset)
        {
            var first = ordered[0];
            return (first.Scale, first.Rotation, first.X, first.Y);
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (progress > ordered[i].Offset) continue;
            var from = ordered[i - 1];
            var to = ordered[i];
            var span = to.Offset - from.Offset;
            var t = span <= 0.0001 ? 1 : (progress - from.Offset) / span;
            return (from.Scale + (to.Scale - from.Scale) * t,
                from.Rotation + (to.Rotation - from.Rotation) * t,
                from.X + (to.X - from.X) * t,
                from.Y + (to.Y - from.Y) * t);
        }

        var last = ordered[^1];
        return (last.Scale, last.Rotation, last.X, last.Y);
    }

    private static (double ScaleX, double ScaleY, double Rotation, double X, double Y) Summarize(
        SkinTransformSpec spec)
    {
        double scaleX = 1, scaleY = 1, rotation = 0, x = 0, y = 0;
        foreach (var operation in spec.Operations)
        {
            switch (operation.Kind)
            {
                case SkinTransformKind.Translate:
                    x += operation.X;
                    y += operation.Y;
                    break;
                case SkinTransformKind.Rotate:
                    rotation += operation.Angle;
                    break;
                case SkinTransformKind.Scale:
                    scaleX *= operation.ScaleX;
                    scaleY *= operation.ScaleY;
                    break;
            }
        }
        return (scaleX, scaleY, rotation, x, y);
    }

    /// <summary>MAUI 只有单一 Scale，非等比缩放取 X 分量（文档中标注）。</summary>
    private static double Ease(string? timing, double t)
    {
        t = Math.Clamp(t, 0, 1);
        switch ((timing ?? "ease").Trim().ToLowerInvariant())
        {
            case "linear": return t;
            case "ease-in": return t * t;
            case "ease-out": return 1 - (1 - t) * (1 - t);
            case "ease-in-out": return t < 0.5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t);
            default: return 1 - (1 - t) * (1 - t);
        }
    }

    private static bool TryDouble(string raw, out double value)
    {
        value = 0;
        var text = (raw ?? string.Empty).Trim();
        var percent = text.EndsWith('%');
        if (percent) text = text[..^1];
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value)) return false;
        if (percent) value /= 100;
        return true;
    }

    private static void WarnOnce(string key, string message)
    {
        if (!Warned.Add(key)) return;
        LogService.Warn(message);
    }
}
