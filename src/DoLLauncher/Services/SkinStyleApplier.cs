using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DoLLauncher.Core.Skin;

namespace DoLLauncher.Services;

/// <summary>
/// 把美化包 skin.css 的规则应用到启动器所有窗口与控件，并在卸载时逐项还原原始值。
/// 只改 WPF 属性，不执行脚本；<c>.类名</c> 对应 XAML 里的样式键（如 <c>.FlatButton</c>）。
/// </summary>
public static class SkinStyleApplier
{
    private sealed class Change
    {
        public DependencyProperty Property { get; init; } = null!;
        public object? Original { get; init; }
        public BaseValueSource Source { get; init; }
        public bool IsExpression { get; init; }
    }

    private sealed class ElementState
    {
        public List<Change> Changes { get; } = new();
        public HashSet<Panel> GapPanels { get; } = new();
        public HashSet<Panel> AlignPanels { get; } = new();
        public ImageBrush? BackgroundImage { get; set; }

        /// <summary>命中的基础规则（不含状态伪类），供过渡声明收集使用。</summary>
        public List<SkinStyleRule> BaseRules { get; } = new();

        /// <summary>命中但带状态伪类的规则，只在状态成立时生效。</summary>
        public List<SkinStyleRule> StateRules { get; } = new();

        /// <summary>该元素需要监听的状态集合（hover / pressed / selected / checked / disabled）。</summary>
        public HashSet<string> WatchedStates { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前激活的状态集合，用于避免重复应用。</summary>
        public HashSet<string> ActiveStates { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool StatesApplied { get; set; }

        public bool BaselineCaptured { get; set; }

        /// <summary>状态规则会触及的属性的“基础快照”（基础规则应用后的值）。</summary>
        public Dictionary<(FrameworkElement Target, DependencyProperty Property), Baseline> Baselines { get; } = new();

        /// <summary>基础层的 transform，状态退出时据此还原。</summary>
        public SkinTransformSpec? BaseTransform { get; set; }

        public bool HasBaseTransform { get; set; }

        /// <summary>是否有状态规则声明了 transform。</summary>
        public bool HasStateTransform { get; set; }

        /// <summary>基础层（无状态伪类）的动画，状态退出时重播。</summary>
        public SkinAnimationSpec? BaseAnimation { get; set; }

        public bool HasBaseAnimation { get; set; }

        /// <summary>上一次重算是否实际播放了状态动画，退出时需要停掉。</summary>
        public bool StateAnimationApplied { get; set; }

        public StateHandlers? Handlers { get; set; }
    }

    /// <summary>某个属性在基础层上的值，状态退出时据此还原。</summary>
    private readonly record struct Baseline(object? Original, BaseValueSource Source, bool IsExpression);

    /// <summary>元素上挂载的状态事件处理器，卸载时统一解绑。</summary>
    private sealed class StateHandlers
    {
        public MouseEventHandler? Enter;
        public MouseEventHandler? Leave;
        public MouseEventHandler? CaptureLost;
        public MouseButtonEventHandler? Down;
        public MouseButtonEventHandler? Up;
        public DependencyPropertyChangedEventHandler? EnabledChanged;
        public RoutedEventHandler? Selected;
        public RoutedEventHandler? Unselected;
        public RoutedEventHandler? Checked;
        public RoutedEventHandler? Unchecked;
    }

    /// <summary>面板级布局：间距、主轴方向与交叉轴对齐（WPF 没有真正的 flex）。</summary>
    private sealed class PanelLayout
    {
        public double RowGap;
        public double ColumnGap;
        public bool HasGap;
        public Orientation? Orientation;
        public string Align = string.Empty;
    }

    private static readonly Dictionary<FrameworkElement, ElementState> Applied = new();
    private static readonly Dictionary<Panel, PanelLayout> PanelLayouts = new();
    private static readonly Dictionary<(Type Type, string Name), DependencyProperty?> PropertyLookup = new();
    private static readonly Dictionary<object, string> StyleKeyLookup = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, string> PropertyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["font-size"] = "FontSize",
        ["font-weight"] = "FontWeight",
        ["font-style"] = "FontStyle",
        ["font-stretch"] = "FontStretch",
        ["font-family"] = "FontFamily",
        ["line-height"] = "LineHeight",
        ["text-align"] = "TextAlignment",
        ["text-decoration"] = "TextDecorations",
        ["padding"] = "Padding",
        ["margin"] = "Margin",
        ["opacity"] = "Opacity",
        ["width"] = "Width",
        ["height"] = "Height",
        ["min-width"] = "MinWidth",
        ["max-width"] = "MaxWidth",
        ["min-height"] = "MinHeight",
        ["max-height"] = "MaxHeight",
        ["cursor"] = "Cursor",
        ["visibility"] = "Visibility",
        ["display"] = "Visibility",
        ["horizontal-align"] = "HorizontalAlignment",
        ["vertical-align"] = "VerticalAlignment",
    };
    private static readonly Dictionary<string, string> VariableAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["background"] = "bg",
        ["window"] = "bg",
        ["windowbackground"] = "bg",
        ["page"] = "bg",
        ["foreground"] = "text",
        ["font"] = "text",
        ["secondary"] = "muted",
        ["subtext"] = "muted",
        ["error"] = "danger",
        ["warning"] = "warn",
        ["link"] = "accent",
        ["primary"] = "accent",
        ["sidebar"] = "panel",
        ["surface"] = "card",
        ["selected"] = "cardselected",
        ["hover"] = "cardhover",
        ["check"] = "checkborder",
        ["progress"] = "progresstrack",
    };
    private static readonly Dictionary<string, FontWeight> FontWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["thin"] = System.Windows.FontWeights.Thin,
        ["extralight"] = System.Windows.FontWeights.ExtraLight,
        ["ultralight"] = System.Windows.FontWeights.UltraLight,
        ["light"] = System.Windows.FontWeights.Light,
        ["normal"] = System.Windows.FontWeights.Normal,
        ["regular"] = System.Windows.FontWeights.Normal,
        ["medium"] = System.Windows.FontWeights.Medium,
        ["semibold"] = System.Windows.FontWeights.SemiBold,
        ["demibold"] = System.Windows.FontWeights.DemiBold,
        ["bold"] = System.Windows.FontWeights.Bold,
        ["extrabold"] = System.Windows.FontWeights.ExtraBold,
        ["ultrabold"] = System.Windows.FontWeights.UltraBold,
        ["black"] = System.Windows.FontWeights.Black,
        ["heavy"] = System.Windows.FontWeights.Heavy,
    };
    private static readonly Dictionary<string, FontStretch> FontStretches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ultracondensed"] = System.Windows.FontStretches.UltraCondensed,
        ["extracondensed"] = System.Windows.FontStretches.ExtraCondensed,
        ["condensed"] = System.Windows.FontStretches.Condensed,
        ["semicondensed"] = System.Windows.FontStretches.SemiCondensed,
        ["normal"] = System.Windows.FontStretches.Normal,
        ["medium"] = System.Windows.FontStretches.Medium,
        ["semiexpanded"] = System.Windows.FontStretches.SemiExpanded,
        ["expanded"] = System.Windows.FontStretches.Expanded,
        ["extraexpanded"] = System.Windows.FontStretches.ExtraExpanded,
        ["ultraexpanded"] = System.Windows.FontStretches.UltraExpanded,
    };
    private static readonly Regex ShadowTokenPattern = new(
        @"rgba?\([^)]*\)|#[0-9a-fA-F]{3,8}|-?\d+(?:\.\d+)?(?:px|pt)?|[a-zA-Z]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static Dictionary<string, string>? _paletteKeyLookup;
    private static bool _initialized;
    private static bool _styleIndexBuilt;
    private static SkinStyleSheet? _sheet;
    private static Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private static string _baseDir = string.Empty;

    /// <summary>按下状态（非 ButtonBase 元素只能用事件自己跟踪）。</summary>
    private static readonly HashSet<FrameworkElement> PressedElements = new(ReferenceEqualityComparer.Instance);

    /// <summary>order 重排：面板的原始子元素顺序。</summary>
    private static readonly Dictionary<Panel, List<UIElement>> OrderBaselines = new(ReferenceEqualityComparer.Instance);

    /// <summary>order 重排：元素声明的 order 值。</summary>
    private static readonly Dictionary<FrameworkElement, int> OrderValues = new(ReferenceEqualityComparer.Instance);

    private static readonly HashSet<Panel> OrderWarnedPanels = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<string> WarnedMessages = new(StringComparer.OrdinalIgnoreCase);

    public static bool HasRules => _sheet is { Rules.Count: > 0 };

    /// <summary>皮肤附加样式类（空格分隔），供声明式组件与 <c>.类名</c> 选择器匹配。</summary>
    public static readonly DependencyProperty SkinClassesProperty =
        DependencyProperty.RegisterAttached("SkinClasses", typeof(string), typeof(SkinStyleApplier),
            new PropertyMetadata(string.Empty));

    public static void SetSkinClasses(DependencyObject element, string? value)
        => element.SetValue(SkinClassesProperty, value ?? string.Empty);

    public static string GetSkinClasses(DependencyObject element)
        => element.GetValue(SkinClassesProperty) as string ?? string.Empty;

    /// <summary>元素是否命中某个皮肤类（样式键或附加类）。</summary>
    public static bool HasSkinClass(FrameworkElement element, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var key = StyleKeyOf(element);
        if (key != null && key.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var cls in SplitSkinClasses(GetSkinClasses(element)))
            if (cls.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IEnumerable<string> SplitSkinClasses(string? text)
        => (text ?? string.Empty).Split(new[] { ' ', '\t', '\r', '\n', ',' },
            StringSplitOptions.RemoveEmptyEntries);


    /// <summary>注册全局 Loaded 钩子：新窗口、动态创建的控件与模板内元素都会按规则上色。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded), true);
    }

    public static void Load(SkinStyleSheet? sheet, string baseDir)
    {
        SkinAnimationRunner.StopAll();
        RestoreAll();
        _sheet = sheet;
        _variables = sheet?.VariablesFor(ThemeService.IsDark) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _baseDir = baseDir ?? string.Empty;
        SkinAnimationRunner.ValueResolver = value => ResolveValue(value);
        ApplyAll();
    }

    public static void Unload()
    {
        SkinAnimationRunner.StopAll();
        RestoreAll();
        _sheet = null;
        _variables.Clear();
        _baseDir = string.Empty;
        SkinAnimationRunner.ValueResolver = null;
    }

    public static void Reapply()
    {
        SkinAnimationRunner.StopAll();
        RestoreAll();
        if (_sheet != null) _variables = _sheet.VariablesFor(ThemeService.IsDark);
        ApplyAll();
    }

    /// <summary>主题切换后：带 @media 主题条件的规则或变量需要重算。</summary>
    public static void NotifyThemeChanged()
    {
        if (_sheet == null) return;
        if (!_sheet.Rules.Any(rule => rule.RequiresDark.HasValue)
            && _sheet.DarkVariables.Count == 0
            && _sheet.LightVariables.Count == 0) return;
        Reapply();
    }

    public static void ApplyAll()
    {
        if (!HasRules) return;
        var app = Application.Current;
        if (app == null) return;

        var windows = new List<Window>();
        foreach (Window window in app.Windows) windows.Add(window);
        foreach (var window in windows) ApplyTree(window);
    }

    public static void ApplyToWindow(Window window)
    {
        if (HasRules) ApplyTree(window);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (HasRules) ApplyElement(element);
        SkinService.ApplyButtonSurface(element);
        SkinComponentService.TryApplyHost(element);
    }

    /// <summary>对单个元素应用样式（供声明式组件插入后立即上色）。</summary>
    public static void ApplyToElement(FrameworkElement element) => ApplyElement(element);

    /// <summary>把 <c>var(--xxx)</c> 解析成具体值；没有样式表时原样返回。</summary>
    public static string ResolveSkinValue(string raw) => ResolveValue(raw ?? string.Empty);

    private static void ApplyTree(FrameworkElement root)
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;

            if (node is FrameworkElement element) ApplyElement(element);

            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
            {
                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(node, i));
            }

            if (node is FrameworkElement || node is FrameworkContentElement)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(node))
                    if (child is DependencyObject childObject) stack.Push(childObject);
            }
        }
    }

    private static void ApplyElement(FrameworkElement element)
    {
        var sheet = _sheet;
        if (sheet == null) return;

        // 三层值模型：基础规则（无状态伪类）与状态规则分开收集
        var baseRules = new List<SkinStyleRule>();
        var stateRules = new List<SkinStyleRule>();

        foreach (var rule in sheet.Rules)
        {
            if (rule.RequiresDark.HasValue && ThemeService.IsDark != rule.RequiresDark.Value) continue;

            var hasBase = false;
            var hasState = false;
            foreach (var selector in rule.Selectors)
            {
                if (!Matches(selector, element)) continue;
                if (selector.HasStates) hasState = true;
                else hasBase = true;
            }

            if (hasBase) baseRules.Add(rule);
            if (hasState) stateRules.Add(rule);
        }

        var state = baseRules.Count > 0 || stateRules.Count > 0 ? GetState(element) : null;
        if (state != null)
        {
            var animationBuilder = new AnimationBuilder();
            foreach (var rule in baseRules)
            {
                foreach (var declaration in rule.Declarations)
                    ApplyDeclaration(element, state, declaration, null, animationBuilder);
            }
            AttachAnimation(element, animationBuilder);

            // 基础动画留一份快照：状态动画结束后据此恢复
            state.BaseAnimation = animationBuilder.HasValue ? animationBuilder.ToSpec() : null;
            state.HasBaseAnimation = animationBuilder.HasValue;

            if (stateRules.Count > 0) SetupStates(element, state, baseRules, stateRules);
            else if (state.Handlers != null) ClearStates(element, state);
        }

        // 面板自身的 gap/align 需要作用到子元素；子元素加载时也要回查父面板
        if (element is Panel panel && PanelLayouts.TryGetValue(panel, out var layout))
            ApplyLayoutToChildren(panel, layout);
        ApplyParentLayout(element);
    }

    private static void ApplyDeclaration(FrameworkElement element, ElementState state,
        SkinDeclaration declaration, TransitionMap? transitions, AnimationBuilder animationBuilder)
    {
        var property = declaration.Property.Trim().ToLowerInvariant();
        var value = declaration.Value.Trim();

        switch (property)
        {
            case "corner-radius":
            case "border-radius":
            case "radius":
                if (TryParseCornerRadius(ResolveValue(value), out var radius))
                    ApplyCornerRadius(element, state, radius, transitions);
                break;

            case "background-image":
                if (TryReadUrl(value, out var imagePath)) ApplyBackgroundImage(element, state, imagePath);
                break;

            case "background-size":
                ApplyBackgroundSize(state, ResolveValue(value));
                break;

            case "background-repeat":
                ApplyBackgroundRepeat(state, ResolveValue(value));
                break;

            case "background":
            case "background-color":
            case "bg":
                if (TryReadUrl(value, out var backgroundPath)) ApplyBackgroundImage(element, state, backgroundPath);
                else ApplyBrush(element, state, "Background", value, transitions, "background");
                break;

            case "color":
            case "foreground":
                ApplyBrush(element, state, "Foreground", value, transitions, "color");
                break;

            case "border-color":
                ApplyBrush(element, state, "BorderBrush", value, transitions, "border-color");
                break;

            case "fill":
                ApplyBrush(element, state, "Fill", value, transitions, "fill");
                break;

            case "stroke":
                ApplyBrush(element, state, "Stroke", value, transitions, "stroke");
                break;

            case "border":
            case "border-width":
            case "border-thickness":
                ApplyBorder(element, state, property, value);
                break;

            case "box-shadow":
            case "shadow":
                ApplyShadow(element, state, ResolveValue(value));
                break;

            case "flex-direction":
                ApplyFlexDirection(element, ResolveValue(value));
                break;

            case "gap":
                ApplyGap(element, ResolveValue(value), true, true);
                break;

            case "row-gap":
                ApplyGap(element, ResolveValue(value), true, false);
                break;

            case "column-gap":
                ApplyGap(element, ResolveValue(value), false, true);
                break;

            case "align-items":
                ApplyAlignItems(element, ResolveValue(value));
                break;

            case "opacity":
                ApplyOpacity(element, state, ResolveValue(value), transitions);
                break;

            case "transform":
                ApplyTransform(element, state, value, transitions);
                break;

            case "order":
                ApplyOrder(element, value);
                break;

            case "transition":
            case "transition-property":
            case "transition-duration":
            case "transition-delay":
            case "transition-timing-function":
                // 过渡声明在状态重算时统一收集，这里不需要直接应用
                break;

            case "animation":
                if (SkinAnimationSpec.TryParse(value, out var animationSpec)) animationBuilder.Apply(animationSpec);
                break;

            case "animation-name":
                animationBuilder.Name = value;
                break;

            case "animation-duration":
                if (TryParseTimeMs(value, out var animationDuration))
                    animationBuilder.DurationMs = animationDuration;
                break;

            case "animation-delay":
                if (TryParseTimeMs(value, out var animationDelay))
                    animationBuilder.DelayMs = animationDelay;
                break;

            case "animation-timing-function":
                animationBuilder.Timing = value;
                break;

            case "animation-iteration-count":
                animationBuilder.IterationCount = value.Equals("infinite", StringComparison.OrdinalIgnoreCase)
                    ? double.PositiveInfinity
                    : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var count)
                        ? Math.Max(0, count)
                        : 1;
                break;

            case "animation-direction":
                animationBuilder.Direction = value.ToLowerInvariant();
                break;

            case "animation-fill-mode":
                animationBuilder.FillMode = value.ToLowerInvariant();
                break;

            case "white-space":
                ApplyTextWrapping(element, state, ResolveValue(value), true);
                break;

            case "overflow-wrap":
            case "word-break":
                // WPF 只能在正常断点换行，break-all/anywhere 仅作近似
                ApplyTextWrapping(element, state, ResolveValue(value), false);
                break;

            case "text-overflow":
                ApplyTextTrimming(element, state, ResolveValue(value));
                break;

            case "overflow":
            case "overflow-x":
            case "overflow-y":
                ApplyOverflow(element, state, ResolveValue(value));
                break;

            case "z-index":
                ApplyZIndex(element, state, ResolveValue(value));
                break;

            case "border-left":
            case "border-right":
            case "border-top":
            case "border-bottom":
            case "border-left-width":
            case "border-right-width":
            case "border-top-width":
            case "border-bottom-width":
                ApplySideBorder(element, state, SideOf(property), ResolveValue(value));
                break;

            case "border-left-color":
            case "border-right-color":
            case "border-top-color":
            case "border-bottom-color":
                // WPF 的 Border 只有一种描边颜色，这里按整体边框色处理
                ApplyBrush(element, state, "BorderBrush", value, transitions, "border-color");
                break;

            case "letter-spacing":
                // WPF 的 TextBlock 没有字距属性，忽略即可
                break;

            default:
                ApplySimpleProperty(element, state, property, value);
                break;
        }
    }

    private static void ApplySimpleProperty(FrameworkElement element, ElementState state, string property, string value)
    {
        if (!PropertyAliases.TryGetValue(property, out var clrName)) return;
        var dependencyProperty = FindProperty(element.GetType(), clrName);
        if (dependencyProperty == null) return;
        if (!TryConvertValue(dependencyProperty.PropertyType, ResolveValue(value), out var converted)) return;
        SetLocal(element, state, dependencyProperty, converted);
    }

    // ---------- 面板布局：用 WPF 能做到的方式近似 flex ----------

    private static bool SupportsPanelLayout(Panel panel) => panel is StackPanel or WrapPanel;

    private static PanelLayout GetPanelLayout(Panel panel)
    {
        if (PanelLayouts.TryGetValue(panel, out var layout)) return layout;
        layout = new PanelLayout();
        PanelLayouts[panel] = layout;
        return layout;
    }

    private static void ApplyFlexDirection(FrameworkElement element, string raw)
    {
        if (element is not Panel panel || !SupportsPanelLayout(panel)) return;

        var value = raw.Trim().ToLowerInvariant();
        Orientation? orientation =
            value.StartsWith("row", StringComparison.Ordinal) ? Orientation.Horizontal
            : value.StartsWith("column", StringComparison.Ordinal) ? Orientation.Vertical
            : null;
        if (orientation == null) return;

        var layout = GetPanelLayout(panel);
        layout.Orientation = orientation;

        var property = FindProperty(panel.GetType(), "Orientation");
        if (property != null) SetLocal(panel, GetState(panel), property, orientation.Value);
        ApplyLayoutToChildren(panel, layout);
    }

    private static void ApplyGap(FrameworkElement element, string raw, bool applyRow, bool applyColumn)
    {
        if (element is not Panel panel || !SupportsPanelLayout(panel)) return;

        var tokens = SplitTokens(raw);
        if (tokens.Count == 0 || !TryParseLength(tokens[0], out var first)) return;
        var second = first;
        if (tokens.Count > 1 && !TryParseLength(tokens[1], out second)) return;

        double rowGap = 0, columnGap = 0;
        if (applyRow && applyColumn)
        {
            rowGap = first;
            columnGap = tokens.Count > 1 ? second : first;
        }
        else if (applyRow) rowGap = first;
        else columnGap = first;

        var layout = GetPanelLayout(panel);
        layout.RowGap = rowGap;
        layout.ColumnGap = columnGap;
        layout.HasGap = true;
        ApplyLayoutToChildren(panel, layout);
    }

    private static void ApplyAlignItems(FrameworkElement element, string raw)
    {
        if (element is not Panel panel || !SupportsPanelLayout(panel)) return;

        var layout = GetPanelLayout(panel);
        layout.Align = raw.Trim().ToLowerInvariant();
        ApplyLayoutToChildren(panel, layout);
    }

    private static void ApplyLayoutToChildren(Panel panel, PanelLayout layout)
    {
        var children = GetDirectChildren(panel);
        for (var i = 0; i < children.Count; i++)
            ApplyLayoutToChild(panel, children[i], layout, i == children.Count - 1);
    }

    private static void ApplyLayoutToChild(Panel panel, FrameworkElement child, PanelLayout layout, bool isLast)
    {
        if (layout.HasGap)
        {
            var gapState = GetState(child);
            if (gapState.GapPanels.Add(panel))
            {
                var orientation = layout.Orientation
                                  ?? (panel is StackPanel stack ? stack.Orientation : Orientation.Horizontal);
                var margin = child.Margin;

                if (panel is WrapPanel)
                {
                    // WrapPanel 两个方向都可能换行，直接给每项右侧/底部补间距
                    if (layout.ColumnGap != 0) margin.Right += layout.ColumnGap;
                    if (layout.RowGap != 0) margin.Bottom += layout.RowGap;
                }
                else if (!isLast)
                {
                    if (orientation == Orientation.Horizontal && layout.ColumnGap != 0)
                        margin.Right += layout.ColumnGap;
                    if (orientation == Orientation.Vertical && layout.RowGap != 0)
                        margin.Bottom += layout.RowGap;
                }

                if (!Equals(margin, child.Margin))
                    SetLocal(child, gapState, FrameworkElement.MarginProperty, margin);
            }
        }

        if (layout.Align.Length == 0) return;

        var alignState = GetState(child);
        if (!alignState.AlignPanels.Add(panel)) return;

        var isHorizontalStack = (layout.Orientation
                                 ?? (panel is StackPanel stacked ? stacked.Orientation : Orientation.Horizontal))
                                == Orientation.Horizontal;
        if (isHorizontalStack)
        {
            var property = FindProperty(child.GetType(), "VerticalAlignment");
            if (property != null) SetLocal(child, alignState, property, ToVerticalAlignment(layout.Align));
        }
        else
        {
            var property = FindProperty(child.GetType(), "HorizontalAlignment");
            if (property != null) SetLocal(child, alignState, property, ToHorizontalAlignment(layout.Align));
        }
    }

    private static void ApplyParentLayout(FrameworkElement element)
    {
        var parent = VisualTreeHelper.GetParent(element) as Panel
                     ?? LogicalTreeHelper.GetParent(element) as Panel;
        if (parent == null || !PanelLayouts.TryGetValue(parent, out var layout)) return;

        var children = GetDirectChildren(parent);
        ApplyLayoutToChild(parent, element, layout, children.IndexOf(element) == children.Count - 1);
    }

    private static List<FrameworkElement> GetDirectChildren(Panel panel)
    {
        var list = new List<FrameworkElement>();
        var count = VisualTreeHelper.GetChildrenCount(panel);
        if (count > 0)
        {
            for (var i = 0; i < count; i++)
                if (VisualTreeHelper.GetChild(panel, i) is FrameworkElement child) list.Add(child);
            return list;
        }

        foreach (var item in LogicalTreeHelper.GetChildren(panel))
            if (item is FrameworkElement child) list.Add(child);
        return list;
    }

    private static VerticalAlignment ToVerticalAlignment(string align) => align switch
    {
        "center" => VerticalAlignment.Center,
        "end" or "flex-end" or "bottom" => VerticalAlignment.Bottom,
        "stretch" => VerticalAlignment.Stretch,
        _ => VerticalAlignment.Top,
    };

    private static HorizontalAlignment ToHorizontalAlignment(string align) => align switch
    {
        "center" => HorizontalAlignment.Center,
        "end" or "flex-end" or "right" => HorizontalAlignment.Right,
        "stretch" => HorizontalAlignment.Stretch,
        _ => HorizontalAlignment.Left,
    };

    // ---------- 文本与溢出 ----------

    private static void ApplyTextWrapping(FrameworkElement element, ElementState state, string raw, bool whiteSpace)
    {
        var property = FindProperty(element.GetType(), "TextWrapping");
        if (property == null) return;

        var value = raw.Trim().ToLowerInvariant();
        var wrapping = whiteSpace
            ? (value is "nowrap" or "no-wrap" ? TextWrapping.NoWrap : TextWrapping.Wrap)
            : (value is "keep-all" ? TextWrapping.NoWrap : TextWrapping.Wrap);
        SetLocal(element, state, property, wrapping);
    }

    private static void ApplyTextTrimming(FrameworkElement element, ElementState state, string raw)
    {
        var property = FindProperty(element.GetType(), "TextTrimming");
        if (property == null) return;

        var value = raw.Trim().ToLowerInvariant();
        SetLocal(element, state, property,
            value is "ellipsis" ? TextTrimming.CharacterEllipsis : TextTrimming.None);
    }

    private static void ApplyOverflow(FrameworkElement element, ElementState state, string raw)
    {
        var property = FindProperty(element.GetType(), "ClipToBounds");
        if (property == null) return;

        var hide = raw.Trim().ToLowerInvariant() is "hidden" or "auto" or "scroll";
        SetLocal(element, state, property, hide);
    }

    private static void ApplyZIndex(FrameworkElement element, ElementState state, string raw)
    {
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            SetLocal(element, state, Panel.ZIndexProperty, index);
    }

    // ---------- 单边边框 ----------

    private static string SideOf(string property)
    {
        const int suffixLength = 6; // "-width" / "-color"
        var rest = property[7..];
        if (rest.EndsWith("-width", StringComparison.Ordinal) || rest.EndsWith("-color", StringComparison.Ordinal))
            return rest[..^suffixLength];
        return rest;
    }

    private static void ApplySideBorder(FrameworkElement element, ElementState state, string side, string raw)
    {
        var thicknessProperty = FindProperty(element.GetType(), "BorderThickness");
        var brushProperty = FindProperty(element.GetType(), "BorderBrush");

        double? width = null;
        Color? color = null;
        foreach (var token in SplitTokens(raw))
        {
            if (IsNone(token))
            {
                width = 0;
                continue;
            }
            if (SkinColors.TryParse(token, out var parsed))
            {
                color = parsed;
                continue;
            }
            if (TryParseLength(token, out var length)) width = length;
        }

        if (width.HasValue && thicknessProperty != null && element.GetValue(thicknessProperty) is Thickness current)
        {
            var updated = side switch
            {
                "left" => new Thickness(width.Value, current.Top, current.Right, current.Bottom),
                "right" => new Thickness(current.Left, current.Top, width.Value, current.Bottom),
                "top" => new Thickness(current.Left, width.Value, current.Right, current.Bottom),
                "bottom" => new Thickness(current.Left, current.Top, current.Right, width.Value),
                _ => current,
            };
            SetLocal(element, state, thicknessProperty, updated);
        }

        // WPF 只有一种边框颜色，左右上下会共用
        if (color.HasValue && brushProperty != null)
            SetLocal(element, state, brushProperty, SkinColors.ToBrush(color.Value));
    }

    private static void ApplyBrush(FrameworkElement element, ElementState state, string clrName, string raw,
        TransitionMap? transitions, string cssProperty)
    {
        var property = FindProperty(element.GetType(), clrName);
        if (property == null) return;

        // var(--accent) 这类写法直接挂到调色板资源上，主题切换时仍然跟随
        if (TryGetPaletteReference(raw, out var paletteKey))
        {
            SetResource(element, state, property, paletteKey);
            return;
        }

        var value = ResolveValue(raw);
        if (!SkinColors.TryParse(value, out var color)) return;

        var transition = transitions?.For(cssProperty);
        if (transition is { DurationMs: > 0 })
        {
            Remember(element, state, property);
            var currentBrush = element.GetValue(property) as Brush;
            if (SkinAnimationRunner.Animate(element, property, currentBrush, SkinColors.ToBrush(color), transition))
                return;
        }

        SetLocal(element, state, property, SkinColors.ToBrush(color));
    }

    private static void ApplyBorder(FrameworkElement element, ElementState state, string property, string raw)
    {
        var thicknessProperty = FindProperty(element.GetType(), "BorderThickness");
        var brushProperty = FindProperty(element.GetType(), "BorderBrush");

        if (property != "border")
        {
            if (thicknessProperty != null && TryParseThickness(ResolveValue(raw), out var thickness))
                SetLocal(element, state, thicknessProperty, thickness);
            return;
        }

        // border: 1px solid #RRGGBB —— 只取宽度与颜色，线型 WPF 不支持
        foreach (var token in SplitTokens(ResolveValue(raw)))
        {
            if (IsNone(token))
            {
                if (thicknessProperty != null) SetLocal(element, state, thicknessProperty, new Thickness(0));
                continue;
            }

            if (SkinColors.TryParse(token, out var color))
            {
                if (brushProperty != null) SetLocal(element, state, brushProperty, SkinColors.ToBrush(color));
                continue;
            }

            if (TryParseLength(token, out var width) && thicknessProperty != null)
                SetLocal(element, state, thicknessProperty, new Thickness(width));
        }
    }

    private static void ApplyCornerRadius(FrameworkElement element, ElementState state, CornerRadius radius,
        TransitionMap? transitions)
    {
        if (element is Border border)
        {
            var transition = transitions?.For("corner-radius");
            if (transition is { DurationMs: > 0 })
            {
                Remember(border, state, Border.CornerRadiusProperty);
                if (SkinAnimationRunner.Animate(border, Border.CornerRadiusProperty,
                        border.CornerRadius, radius, transition)) return;
            }

            SetLocal(border, state, Border.CornerRadiusProperty, radius);
            return;
        }

        if (element is not Control control) return;

        control.ApplyTemplate();
        var count = VisualTreeHelper.GetChildrenCount(control);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(control, i) is Border templateBorder
                && ReferenceEquals(templateBorder.TemplatedParent, control))
            {
                // 模板内的边框要记在它自己的状态里，卸载时才能还原到正确的元素
                SetLocal(templateBorder, GetState(templateBorder), Border.CornerRadiusProperty, radius);
            }
        }
    }

    private static void ApplyOpacity(FrameworkElement element, ElementState state, string raw,
        TransitionMap? transitions)
    {
        var text = raw.Trim();
        double opacity;
        if (text.EndsWith('%'))
        {
            if (!double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                return;
            opacity = percent / 100;
        }
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out opacity))
        {
            return;
        }

        opacity = Math.Clamp(opacity, 0, 1);

        var transition = transitions?.For("opacity");
        if (transition is { DurationMs: > 0 })
        {
            Remember(element, state, UIElement.OpacityProperty);
            if (SkinAnimationRunner.Animate(element, UIElement.OpacityProperty,
                    element.Opacity, opacity, transition)) return;
        }

        SetLocal(element, state, UIElement.OpacityProperty, opacity);
    }

    private static void ApplyTransform(FrameworkElement element, ElementState state, string raw,
        TransitionMap? transitions)
    {
        if (!SkinTransformSpec.TryParse(ResolveValue(raw), out var spec))
        {
            WarnOnce("transform:" + raw, $"无法解析 transform：{raw}，已忽略。");
            return;
        }

        // 基础层（无过渡）记录一份，状态退出时回到基础变换
        if (transitions == null)
        {
            state.BaseTransform = spec;
            state.HasBaseTransform = true;
        }

        SkinAnimationRunner.SetTransform(element, spec, transitions?.For("transform"));
    }

    private static void ApplyBackgroundImage(FrameworkElement element, ElementState state, string relativePath)
    {
        var property = FindProperty(element.GetType(), "Background");
        if (property == null) return;

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (!SkinRules.IsSafeRelativePath(normalized)) return;

        try
        {
            var file = Path.Combine(_baseDir, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) return;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            state.BackgroundImage = brush;
            SetLocal(element, state, property, brush);
        }
        catch (Exception ex)
        {
            LogService.Error("美化包背景图加载失败", ex);
        }
    }

    private static void ApplyBackgroundSize(ElementState state, string raw)
    {
        var brush = state.BackgroundImage;
        if (brush == null) return;

        brush.Stretch = raw.Trim().ToLowerInvariant() switch
        {
            "cover" => Stretch.UniformToFill,
            "contain" => Stretch.Uniform,
            "fill" or "stretch" or "100% 100%" => Stretch.Fill,
            "none" or "auto" => Stretch.None,
            _ => brush.Stretch,
        };
    }

    private static void ApplyBackgroundRepeat(ElementState state, string raw)
    {
        var brush = state.BackgroundImage;
        if (brush == null) return;

        brush.TileMode = raw.Trim().ToLowerInvariant() switch
        {
            "no-repeat" => TileMode.None,
            "repeat" or "repeat-x" or "repeat-y" => TileMode.Tile,
            _ => brush.TileMode,
        };
    }

    private static void ApplyShadow(FrameworkElement element, ElementState state, string raw)
    {
        var effectProperty = FindProperty(element.GetType(), "Effect");
        if (effectProperty == null) return;

        var lengths = new List<double>();
        var color = Color.FromArgb(90, 0, 0, 0);
        var hasColor = false;

        foreach (Match match in ShadowTokenPattern.Matches(raw))
        {
            var token = match.Value.Trim();
            if (token.Length == 0) continue;
            if (token.Equals("inset", StringComparison.OrdinalIgnoreCase)) continue;

            if (SkinColors.TryParse(token, out var parsed))
            {
                color = parsed;
                hasColor = true;
                continue;
            }

            if (TryParseLength(token, out var length)) lengths.Add(length);
        }

        if (lengths.Count == 0) return;

        var offsetX = lengths[0];
        var offsetY = lengths.Count > 1 ? lengths[1] : 0;
        var blur = lengths.Count > 2 ? Math.Max(0, lengths[2]) : 8;
        if (!hasColor) color = Color.FromArgb(90, 0, 0, 0);

        var direction = Math.Atan2(offsetY, offsetX) * 180 / Math.PI;
        var effect = new DropShadowEffect
        {
            Color = Color.FromRgb(color.R, color.G, color.B),
            BlurRadius = blur,
            ShadowDepth = Math.Sqrt(offsetX * offsetX + offsetY * offsetY),
            Direction = direction,
            Opacity = color.A / 255.0,
            RenderingBias = RenderingBias.Performance,
        };
        effect.Freeze();
        SetLocal(element, state, effectProperty, effect);
    }

    // ---------- 三层值模型：状态监听与状态重算 ----------

    private static void SetupStates(FrameworkElement element, ElementState state,
        List<SkinStyleRule> baseRules, List<SkinStyleRule> stateRules)
    {
        state.BaseRules.Clear();
        state.BaseRules.AddRange(baseRules);
        state.StateRules.Clear();
        state.StateRules.AddRange(stateRules);

        state.WatchedStates.Clear();
        foreach (var rule in stateRules)
        {
            foreach (var selector in rule.Selectors)
            {
                if (!Matches(selector, element)) continue;
                foreach (var name in selector.States) state.WatchedStates.Add(name);
            }
        }

        if (state.WatchedStates.Count == 0) return;

        state.HasStateTransform = stateRules.Any(rule => rule.Declarations
            .Any(declaration => declaration.Property.Trim().Equals("transform", StringComparison.OrdinalIgnoreCase)));

        EnsureStateHooks(element, state);
        if (!state.BaselineCaptured)
        {
            state.BaselineCaptured = true;
            CaptureStateBaseline(element, state);
        }

        // 基础规则刚重新写入过本地值，强制按当前状态再应用一次
        state.StatesApplied = false;
        RecomputeStates(element);
    }

    private static void ClearStates(FrameworkElement element, ElementState state)
    {
        SkinAnimationRunner.StopTransitions(element);
        SkinAnimationRunner.StopKeyframes(element);
        UnhookState(element, state);
        RestoreStateBaseline(state);
        ResetTransformToBase(element, state, true);

        var baseAnimation = state.BaseAnimation;
        var hasBaseAnimation = state.HasBaseAnimation;

        state.Handlers = null;
        state.BaseRules.Clear();
        state.StateRules.Clear();
        state.WatchedStates.Clear();
        state.Baselines.Clear();
        state.ActiveStates.Clear();
        state.BaseTransform = null;
        state.HasBaseTransform = false;
        state.HasStateTransform = false;
        state.BaseAnimation = null;
        state.HasBaseAnimation = false;
        state.StateAnimationApplied = false;
        state.BaselineCaptured = false;
        state.StatesApplied = false;

        // 回到基础层：有基础动画则重新播放，没有则保持停止
        if (hasBaseAnimation && baseAnimation != null) AttachAnimation(element, baseAnimation);
    }

    /// <summary>状态退出时把 transform 恢复成基础层的值（没有基础声明则还原原始变换）。</summary>
    private static void ResetTransformToBase(FrameworkElement element, ElementState state,
        bool resetWhenNoBase)
    {
        // 必须发生在应用状态声明之前：随后命中的 transform 会重新创建变换组
        if (state.HasBaseTransform && state.BaseTransform != null)
            SkinAnimationRunner.SetTransform(element, state.BaseTransform, null);
        else if (resetWhenNoBase || state.HasStateTransform)
            SkinAnimationRunner.ResetTransform(element);
    }

    private static void EnsureStateHooks(FrameworkElement element, ElementState state)
    {
        var handlers = state.Handlers ??= new StateHandlers();

        if (state.WatchedStates.Contains("hover") || state.WatchedStates.Contains("pressed"))
        {
            if (handlers.Enter == null)
            {
                handlers.Enter = (_, _) => RecomputeStates(element);
                element.MouseEnter += handlers.Enter;
            }
            if (handlers.Leave == null)
            {
                handlers.Leave = (_, _) =>
                {
                    PressedElements.Remove(element);
                    RecomputeStates(element);
                };
                element.MouseLeave += handlers.Leave;
            }
        }

        if (state.WatchedStates.Contains("pressed"))
        {
            if (handlers.Down == null)
            {
                handlers.Down = (_, _) =>
                {
                    PressedElements.Add(element);
                    RecomputeStates(element);
                };
                element.PreviewMouseLeftButtonDown += handlers.Down;
            }
            if (handlers.Up == null)
            {
                handlers.Up = (_, _) =>
                {
                    PressedElements.Remove(element);
                    RecomputeStates(element);
                };
                element.PreviewMouseLeftButtonUp += handlers.Up;
            }
            if (handlers.CaptureLost == null)
            {
                handlers.CaptureLost = (_, _) =>
                {
                    PressedElements.Remove(element);
                    RecomputeStates(element);
                };
                element.LostMouseCapture += handlers.CaptureLost;
            }
        }

        if (state.WatchedStates.Contains("disabled") && handlers.EnabledChanged == null)
        {
            handlers.EnabledChanged = (_, _) => RecomputeStates(element);
            element.IsEnabledChanged += handlers.EnabledChanged;
        }

        if (state.WatchedStates.Contains("selected") && element is ListBoxItem item)
        {
            if (handlers.Selected == null)
            {
                handlers.Selected = (_, _) => RecomputeStates(element);
                item.Selected += handlers.Selected;
            }
            if (handlers.Unselected == null)
            {
                handlers.Unselected = (_, _) => RecomputeStates(element);
                item.Unselected += handlers.Unselected;
            }
        }

        if (state.WatchedStates.Contains("checked") && element is ToggleButton toggle)
        {
            if (handlers.Checked == null)
            {
                handlers.Checked = (_, _) => RecomputeStates(element);
                toggle.Checked += handlers.Checked;
            }
            if (handlers.Unchecked == null)
            {
                handlers.Unchecked = (_, _) => RecomputeStates(element);
                toggle.Unchecked += handlers.Unchecked;
            }
        }
    }

    private static void UnhookState(FrameworkElement element, ElementState state)
    {
        var handlers = state.Handlers;
        if (handlers == null) return;

        if (handlers.Enter != null) element.MouseEnter -= handlers.Enter;
        if (handlers.Leave != null) element.MouseLeave -= handlers.Leave;
        if (handlers.Down != null) element.PreviewMouseLeftButtonDown -= handlers.Down;
        if (handlers.Up != null) element.PreviewMouseLeftButtonUp -= handlers.Up;
        if (handlers.CaptureLost != null) element.LostMouseCapture -= handlers.CaptureLost;
        if (handlers.EnabledChanged != null) element.IsEnabledChanged -= handlers.EnabledChanged;
        if (handlers.Selected != null && element is ListBoxItem item) item.Selected -= handlers.Selected;
        if (handlers.Unselected != null && element is ListBoxItem unselectedItem)
            unselectedItem.Unselected -= handlers.Unselected;
        if (handlers.Checked != null && element is ToggleButton toggle) toggle.Checked -= handlers.Checked;
        if (handlers.Unchecked != null && element is ToggleButton uncheckedToggle)
            uncheckedToggle.Unchecked -= handlers.Unchecked;

        PressedElements.Remove(element);
    }

    /// <summary>记录状态规则会触及属性在“基础层”上的值，状态退出时据此还原。</summary>
    private static void CaptureStateBaseline(FrameworkElement element, ElementState state)
    {
        foreach (var rule in state.StateRules)
        {
            var matched = false;
            foreach (var selector in rule.Selectors)
            {
                if (!Matches(selector, element)) continue;
                matched = true;
                break;
            }
            if (!matched) continue;

            foreach (var declaration in rule.Declarations)
            {
                foreach (var (target, property) in ResolveStateTargets(element, declaration.Property))
                {
                    if (state.Baselines.ContainsKey((target, property))) continue;
                    var original = target.ReadLocalValue(property);
                    state.Baselines[(target, property)] = new Baseline(
                        original,
                        DependencyPropertyHelper.GetValueSource(target, property).BaseValueSource,
                        original is System.Windows.Expression);
                }
            }
        }
    }

    private static void RestoreStateBaseline(ElementState state)
    {
        foreach (var pair in state.Baselines)
        {
            var (target, property) = pair.Key;
            var baseline = pair.Value;

            try
            {
                if (baseline.IsExpression && TryRestoreResourceReference(target, property, baseline.Original))
                    continue;

                if (baseline.Source == BaseValueSource.Local && baseline.Original != DependencyProperty.UnsetValue)
                    target.SetValue(property, baseline.Original);
                else
                    target.ClearValue(property);
            }
            catch
            {
                // 少数属性只读或原始值无法回写时忽略
            }
        }
    }

    /// <summary>状态变化后统一重算：先还原基础快照，再按当前激活状态集合重新应用状态声明。</summary>
    private static void RecomputeStates(FrameworkElement element)
    {
        if (!Applied.TryGetValue(element, out var state)) return;
        if (state.StateRules.Count == 0) return;

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.IsMouseOver) active.Add("hover");
        if (!element.IsEnabled) active.Add("disabled");
        if (IsPressed(element)) active.Add("pressed");
        if (element is ListBoxItem { IsSelected: true }) active.Add("selected");
        if (element is ToggleButton { IsChecked: true }) active.Add("checked");

        if (state.StatesApplied && state.ActiveStates.SetEquals(active)) return;

        state.StatesApplied = true;
        state.ActiveStates.Clear();
        foreach (var name in active) state.ActiveStates.Add(name);

        // 只有本次状态涉及动画、或上次确实播放过状态动画时才动关键帧；
        // 否则保持原有过渡逻辑，避免无状态动画的皮肤被重启动画。
        var animationPath = state.StateAnimationApplied || ActiveStatesDeclareAnimation(state);

        SkinAnimationRunner.StopTransitions(element);
        if (animationPath)
        {
            // 顺序：先停关键帧，再回到基础快照，最后才应用当前状态声明
            SkinAnimationRunner.StopKeyframes(element);
            ResetTransformToBase(element, state, true);
        }
        else
        {
            ResetTransformToBase(element, state, false);
        }
        RestoreStateBaseline(state);

        // 状态声明的动画统一收集到 builder；同一元素命中多条状态动画时，
        // AnimationBuilder 按声明顺序逐条覆盖，因此以最后一条有效声明为准（后声明覆盖先声明）。
        var animationBuilder = new AnimationBuilder();
        var transitions = BuildTransitions(state);
        foreach (var rule in state.StateRules)
        {
            if (!RuleActiveForStates(rule, state.ActiveStates)) continue;
            foreach (var declaration in rule.Declarations)
                ApplyDeclaration(element, state, declaration, transitions, animationBuilder);
        }

        if (!animationPath) return;

        var sheet = _sheet;
        if (sheet == null) return;

        // animation: none 或缺少 @keyframes 都视为无状态动画，回落到基础层
        var stateSpec = animationBuilder.HasValue ? animationBuilder.ToSpec() : null;
        state.StateAnimationApplied = stateSpec != null && sheet.Keyframes.ContainsKey(stateSpec.Name);

        if (state.StateAnimationApplied && stateSpec != null)
        {
            // 状态动画替换基础动画
            SkinAnimationRunner.Attach(element, sheet, stateSpec);
        }
        else if (state.HasBaseAnimation && state.BaseAnimation != null)
        {
            AttachAnimation(element, state.BaseAnimation);
        }
        // 既没有状态动画也没有基础动画：保持停止（StopKeyframes 已完成）
    }

    /// <summary>当前激活的状态规则里是否有 animation 声明（含 <c>animation: none</c>）。</summary>
    private static bool ActiveStatesDeclareAnimation(ElementState state)
    {
        foreach (var rule in state.StateRules)
        {
            if (!RuleActiveForStates(rule, state.ActiveStates)) continue;

            foreach (var declaration in rule.Declarations)
            {
                var property = declaration.Property.Trim().ToLowerInvariant();
                if (property is "animation" or "animation-name" or "animation-duration" or "animation-delay"
                    or "animation-timing-function" or "animation-iteration-count"
                    or "animation-direction" or "animation-fill-mode") return true;
            }
        }
        return false;
    }

    private static bool IsPressed(FrameworkElement element)
        => element is ButtonBase button ? button.IsPressed : PressedElements.Contains(element);

    private static bool RuleActiveForStates(SkinStyleRule rule, HashSet<string> active)
    {
        foreach (var selector in rule.Selectors)
        {
            if (!selector.HasStates) return true;
            if (selector.States.All(active.Contains)) return true;
        }
        return false;
    }

    // ---------- 过渡 ----------

    private sealed class TransitionMap
    {
        private readonly List<SkinTransitionSpec> _specs = new();

        public void Add(SkinTransitionSpec spec)
        {
            if (spec.DurationMs > 0) _specs.Add(spec);
        }

        /// <summary>查找某个 CSS 属性的过渡；<c>all</c> 兜底。</summary>
        public SkinTransitionSpec? For(string property)
        {
            var normalized = NormalizeCssProperty(property);
            foreach (var spec in _specs)
            {
                foreach (var candidate in spec.Properties)
                {
                    var name = NormalizeCssProperty(candidate);
                    if (name == "all" || name == normalized) return spec;
                }
            }
            return null;
        }
    }

    /// <summary>把 transition 长写（property/duration/delay/timing-function）拼成一条过渡。</summary>
    private sealed class TransitionBuilder
    {
        public List<string> Properties { get; } = new();
        public double DurationMs { get; set; }
        public double DelayMs { get; set; }
        public string Timing { get; set; } = "ease";
        public bool HasDuration { get; set; }

        public void TryAdd(TransitionMap map)
        {
            if (!HasDuration || DurationMs <= 0) return;
            map.Add(new SkinTransitionSpec
            {
                Properties = Properties.Count == 0 ? new[] { "all" } : Properties.ToArray(),
                DurationMs = DurationMs,
                DelayMs = DelayMs,
                TimingFunction = Timing,
            });
        }
    }

    private static TransitionMap BuildTransitions(ElementState state)
    {
        var map = new TransitionMap();
        var builder = new TransitionBuilder();

        foreach (var rule in state.BaseRules) CollectTransitionDeclarations(rule, builder, map);
        foreach (var rule in state.StateRules)
        {
            if (!RuleActiveForStates(rule, state.ActiveStates)) continue;
            CollectTransitionDeclarations(rule, builder, map);
        }

        builder.TryAdd(map);
        return map;
    }

    private static void CollectTransitionDeclarations(SkinStyleRule rule, TransitionBuilder builder,
        TransitionMap map)
    {
        foreach (var declaration in rule.Declarations)
        {
            var property = declaration.Property.Trim().ToLowerInvariant();
            var value = declaration.Value.Trim();

            switch (property)
            {
                case "transition":
                    if (SkinTransitionSpec.TryParse(value, out var spec)) map.Add(spec);
                    break;
                case "transition-property":
                    builder.Properties.AddRange(SplitTokens(value));
                    break;
                case "transition-duration":
                    if (TryParseTimeMs(value, out var duration))
                    {
                        builder.DurationMs = duration;
                        builder.HasDuration = true;
                    }
                    break;
                case "transition-delay":
                    if (TryParseTimeMs(value, out var delay)) builder.DelayMs = delay;
                    break;
                case "transition-timing-function":
                    builder.Timing = value;
                    break;
            }
        }
    }

    private static string NormalizeCssProperty(string? property) => (property ?? string.Empty)
        .Trim().ToLowerInvariant() switch
        {
            "background-color" or "bg" => "background",
            "foreground" => "color",
            "border-radius" or "radius" => "corner-radius",
            var text => text,
        };

    // ---------- 顺序重排 ----------

    private static void ApplyOrder(FrameworkElement element, string raw)
    {
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order)) return;

        var panel = ParentOf(element) as Panel;
        if (panel is not (StackPanel or WrapPanel))
        {
            WarnOnce("order-panel", "order 只支持 StackPanel / WrapPanel 的直接子元素，已忽略。");
            return;
        }

        if (HasVirtualizingAncestor(panel))
        {
            if (OrderWarnedPanels.Add(panel))
                LogService.Warn("order：检测到虚拟化列表祖先，已跳过重排（虚拟化会复用容器）。");
            return;
        }

        if (!OrderBaselines.TryGetValue(panel, out var baseline))
        {
            baseline = panel.Children.OfType<UIElement>().ToList();
            OrderBaselines[panel] = baseline;
        }

        OrderValues[element] = order;
        ReorderPanel(panel, baseline);
    }

    private static void ReorderPanel(Panel panel, List<UIElement> baseline)
    {
        var children = panel.Children.OfType<UIElement>().ToList();
        var desired = children
            .OrderBy(child => child is FrameworkElement item && OrderValues.TryGetValue(item, out var order)
                ? order
                : 0)
            .ThenBy(child => baseline.IndexOf(child) is var index && index >= 0 ? index : int.MaxValue)
            .ToList();

        var same = children.Count == desired.Count;
        for (var i = 0; i < children.Count && same; i++)
            if (!ReferenceEquals(children[i], desired[i])) same = false;
        if (same) return;

        try
        {
            panel.Children.Clear();
            foreach (var child in desired) panel.Children.Add(child);
        }
        catch (Exception ex)
        {
            LogService.Warn($"order 重排失败：{ex.Message}");
        }
    }

    private static bool HasVirtualizingAncestor(DependencyObject node)
    {
        if (node is VirtualizingPanel) return true;

        for (var parent = ParentOf(node); parent != null; parent = ParentOf(parent))
        {
            if (parent is VirtualizingPanel) return true;
            if (parent is ItemsControl items && VirtualizingPanel.GetIsVirtualizing(items)) return true;
        }
        return false;
    }

    private static void RestoreOrder()
    {
        foreach (var pair in OrderBaselines.ToArray())
        {
            var panel = pair.Key;
            var baseline = pair.Value;
            var current = panel.Children.OfType<UIElement>().ToList();
            if (current.Count != baseline.Count) continue;

            var currentSet = new HashSet<UIElement>(current, ReferenceEqualityComparer.Instance);
            if (!baseline.All(currentSet.Contains)) continue;

            var same = true;
            for (var i = 0; i < baseline.Count && same; i++)
                if (!ReferenceEquals(panel.Children[i], baseline[i])) same = false;
            if (same) continue;

            try
            {
                panel.Children.Clear();
                foreach (var child in baseline) panel.Children.Add(child);
            }
            catch
            {
                // 面板已卸载时忽略
            }
        }

        OrderBaselines.Clear();
        OrderValues.Clear();
        OrderWarnedPanels.Clear();
    }

    // ---------- 状态规则触及的属性解析 ----------

    private static List<(FrameworkElement Target, DependencyProperty Property)> ResolveStateTargets(
        FrameworkElement element, string cssProperty)
    {
        var result = new List<(FrameworkElement, DependencyProperty)>();
        var property = cssProperty.Trim().ToLowerInvariant();

        switch (property)
        {
            case "background":
            case "background-color":
            case "bg":
            case "background-image":
                AddTarget(result, element, "Background");
                break;
            case "color":
            case "foreground":
                AddTarget(result, element, "Foreground");
                break;
            case "border-color":
            case "border-left-color":
            case "border-right-color":
            case "border-top-color":
            case "border-bottom-color":
                AddTarget(result, element, "BorderBrush");
                break;
            case "fill":
                AddTarget(result, element, "Fill");
                break;
            case "stroke":
                AddTarget(result, element, "Stroke");
                break;
            case "border":
            case "border-width":
            case "border-thickness":
            case "border-left":
            case "border-right":
            case "border-top":
            case "border-bottom":
            case "border-left-width":
            case "border-right-width":
            case "border-top-width":
            case "border-bottom-width":
                AddTarget(result, element, "BorderThickness");
                break;
            case "box-shadow":
            case "shadow":
                AddTarget(result, element, "Effect");
                break;
            case "opacity":
                result.Add((element, UIElement.OpacityProperty));
                break;
            case "transform":
            case "order":
            case "transition":
            case "animation":
                break;
            case "corner-radius":
            case "border-radius":
            case "radius":
                if (element is Border border)
                {
                    result.Add((border, Border.CornerRadiusProperty));
                }
                else if (element is Control control)
                {
                    control.ApplyTemplate();
                    var count = VisualTreeHelper.GetChildrenCount(control);
                    for (var i = 0; i < count; i++)
                    {
                        if (VisualTreeHelper.GetChild(control, i) is Border templateBorder
                            && ReferenceEquals(templateBorder.TemplatedParent, control))
                        {
                            result.Add((templateBorder, Border.CornerRadiusProperty));
                        }
                    }
                }
                break;
            default:
                if (PropertyAliases.TryGetValue(property, out var clrName))
                    AddTarget(result, element, clrName);
                break;
        }

        return result;
    }

    private static void AddTarget(List<(FrameworkElement Target, DependencyProperty Property)> result,
        FrameworkElement element, string clrName)
    {
        var property = FindProperty(element.GetType(), clrName);
        if (property != null) result.Add((element, property));
    }

    // ---------- animation 声明收集 ----------

    private sealed class AnimationBuilder
    {
        public string Name { get; set; } = string.Empty;
        public double DurationMs { get; set; }
        public double DelayMs { get; set; }
        public string Timing { get; set; } = "ease";
        public double IterationCount { get; set; } = 1;
        public string Direction { get; set; } = "normal";
        public string FillMode { get; set; } = "none";

        /// <summary>按声明顺序整体覆盖字段：同一元素命中多条 animation 时以最后一条为准。</summary>
        public void Apply(SkinAnimationSpec spec)
        {
            Name = spec.Name;
            DurationMs = spec.DurationMs;
            DelayMs = spec.DelayMs;
            Timing = spec.TimingFunction;
            IterationCount = spec.IterationCount;
            Direction = spec.Direction;
            FillMode = spec.FillMode;
        }

        public bool HasValue => Name.Length > 0 && DurationMs > 0
                                && !Name.Equals("none", StringComparison.OrdinalIgnoreCase);

        public SkinAnimationSpec ToSpec() => new()
        {
            Name = Name,
            DurationMs = DurationMs,
            DelayMs = DelayMs,
            TimingFunction = Timing,
            IterationCount = IterationCount,
            Direction = Direction,
            FillMode = FillMode,
        };
    }

    private static void AttachAnimation(FrameworkElement element, AnimationBuilder builder)
    {
        var sheet = _sheet;
        if (sheet == null || !builder.HasValue) return;
        SkinAnimationRunner.Attach(element, sheet, builder.ToSpec());
    }

    private static void AttachAnimation(FrameworkElement element, SkinAnimationSpec spec)
    {
        var sheet = _sheet;
        if (sheet == null) return;
        SkinAnimationRunner.Attach(element, sheet, spec);
    }

    private static bool TryParseTimeMs(string text, out double milliseconds)
    {
        milliseconds = 0;
        var raw = text.Trim().ToLowerInvariant();
        if (raw.Length == 0) return false;

        double value;
        if (raw.EndsWith("ms", StringComparison.Ordinal))
        {
            if (!double.TryParse(raw[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
        }
        else if (raw.EndsWith("s", StringComparison.Ordinal))
        {
            if (!double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            value *= 1000;
        }
        else
        {
            return false;
        }

        if (!double.IsFinite(value) || value < 0) return false;
        milliseconds = value;
        return true;
    }

    private static void WarnOnce(string key, string message)
    {
        if (!WarnedMessages.Add(key)) return;
        LogService.Warn(message);
    }

    private static bool Matches(SkinSelector selector, FrameworkElement element)
    {
        if (!MatchesPart(selector, element)) return false;
        if (selector.Ancestors.Count == 0) return true;

        var index = selector.Ancestors.Count - 1;
        var parent = ParentOf(element);
        while (parent != null && index >= 0)
        {
            if (parent is FrameworkElement ancestor && MatchesPart(selector.Ancestors[index], ancestor)) index--;
            parent = ParentOf(parent);
        }
        return index < 0;
    }

    private static bool MatchesPart(SkinSelector selector, FrameworkElement element)
    {
        if (!selector.Universal && selector.Type.Length > 0 && !TypeMatches(element.GetType(), selector.Type))
            return false;

        if (selector.Name.Length > 0 && !string.Equals(element.Name, selector.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (selector.Classes.Count > 0)
        {
            // 样式键（x:Key）与皮肤附加类任一命中即可；.A.B 要求两者都命中
            foreach (var cls in selector.Classes)
                if (!HasSkinClass(element, cls)) return false;
        }

        return true;
    }

    private static bool TypeMatches(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
    {
        try
        {
            var parent = LogicalTreeHelper.GetParent(node);
            if (parent != null) return parent;
        }
        catch
        {
            // 非逻辑树元素时继续尝试视觉父级
        }

        if (node is Visual) return VisualTreeHelper.GetParent(node);
        return null;
    }

    private static string? StyleKeyOf(FrameworkElement element)
    {
        var style = element.Style;
        if (style == null) return null;
        if (!_styleIndexBuilt) BuildStyleIndex();
        return StyleKeyLookup.TryGetValue(style, out var key) ? key : null;
    }

    private static void BuildStyleIndex()
    {
        _styleIndexBuilt = true;
        var app = Application.Current;
        if (app == null) return;

        var stack = new Stack<ResourceDictionary>();
        stack.Push(app.Resources);
        while (stack.Count > 0)
        {
            var dictionary = stack.Pop();
            foreach (var key in dictionary.Keys)
            {
                if (key is string name && dictionary[key] is Style style) StyleKeyLookup[style] = name;
            }
            foreach (var merged in dictionary.MergedDictionaries) stack.Push(merged);
        }
    }

    private static ElementState GetState(FrameworkElement element)
    {
        if (Applied.TryGetValue(element, out var state)) return state;
        state = new ElementState();
        Applied[element] = state;
        return state;
    }

    private static void SetLocal(FrameworkElement element, ElementState state, DependencyProperty? property, object? value)
    {
        if (property == null || value == null) return;
        Remember(element, state, property);
        try
        {
            element.SetValue(property, value);
        }
        catch
        {
            // 少数属性只读或类型不匹配时跳过
        }
    }

    private static void SetResource(FrameworkElement element, ElementState state, DependencyProperty property, string resourceKey)
    {
        Remember(element, state, property);
        try
        {
            element.SetResourceReference(property, resourceKey);
        }
        catch
        {
            // 忽略无法使用动态资源的属性
        }
    }

    private static void Remember(FrameworkElement element, ElementState state, DependencyProperty property)
    {
        foreach (var change in state.Changes)
            if (change.Property == property) return;

        var original = element.ReadLocalValue(property);
        state.Changes.Add(new Change
        {
            Property = property,
            Original = original,
            Source = DependencyPropertyHelper.GetValueSource(element, property).BaseValueSource,
            IsExpression = original is System.Windows.Expression,
        });
    }

    private static void RestoreAll()
    {
        if (Applied.Count == 0 && OrderBaselines.Count == 0)
        {
            PanelLayouts.Clear();
            PressedElements.Clear();
            return;
        }

        foreach (var pair in Applied.ToArray())
        {
            UnhookState(pair.Key, pair.Value);
            try
            {
                SkinAnimationRunner.Detach(pair.Key);
            }
            catch
            {
                // 元素已销毁时忽略
            }
            RestoreState(pair.Key, pair.Value);
        }

        Applied.Clear();
        PanelLayouts.Clear();
        PressedElements.Clear();
        WarnedMessages.Clear();
        RestoreOrder();
    }

    private static void RestoreState(FrameworkElement element, ElementState state)
    {
        for (var i = state.Changes.Count - 1; i >= 0; i--)
        {
            var change = state.Changes[i];

            if (change.IsExpression && TryRestoreResourceReference(element, change.Property, change.Original))
                continue;

            if (change.Source == BaseValueSource.Local && change.Original != DependencyProperty.UnsetValue)
            {
                try
                {
                    element.SetValue(change.Property, change.Original);
                    continue;
                }
                catch
                {
                    // 原始值是表达式等无法直接回写的情况，退回清除本地值
                }
            }

            element.ClearValue(change.Property);
        }
    }

    /// <summary>原始值是 DynamicResource 时，尽量取回资源键并重新挂接，保证卸载后仍跟随主题。</summary>
    private static bool TryRestoreResourceReference(DependencyObject element, DependencyProperty property, object? expression)
    {
        if (expression == null || element is not FrameworkElement frameworkElement) return false;

        try
        {
            var type = expression.GetType();
            var field = type.GetField("_resourceKey", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? type.GetField("ResourceKey", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field?.GetValue(expression) is not { } key) return false;
            frameworkElement.SetResourceReference(property, key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DependencyProperty? FindProperty(Type type, string name)
    {
        var key = (type, name);
        if (PropertyLookup.TryGetValue(key, out var cached)) return cached;

        DependencyProperty? found = null;
        var fieldName = name + "Property";
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            var field = current.GetField(fieldName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field?.GetValue(null) is DependencyProperty dependencyProperty)
            {
                found = dependencyProperty;
                break;
            }
        }

        PropertyLookup[key] = found;
        return found;
    }

    private static bool TryConvertValue(Type target, string raw, out object? value)
    {
        value = null;
        var text = raw.Trim();
        if (text.Length == 0) return false;

        if (target == typeof(Brush))
        {
            if (!SkinColors.TryParse(text, out var color)) return false;
            value = SkinColors.ToBrush(color);
            return true;
        }

        if (target == typeof(Thickness))
        {
            if (!TryParseThickness(text, out var thickness)) return false;
            value = thickness;
            return true;
        }

        if (target == typeof(CornerRadius))
        {
            if (!TryParseCornerRadius(text, out var radius)) return false;
            value = radius;
            return true;
        }

        if (target == typeof(double))
        {
            if (!TryParseLength(text, out var number)) return false;
            value = number;
            return true;
        }

        if (target == typeof(FontWeight))
        {
            if (double.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight))
            {
                value = FontWeight.FromOpenTypeWeight((int)Math.Clamp(weight, 1, 999));
                return true;
            }
            if (!FontWeights.TryGetValue(text, out var fontWeight)) return false;
            value = fontWeight;
            return true;
        }

        if (target == typeof(FontStyle))
        {
            value = text.ToLowerInvariant() switch
            {
                "italic" => System.Windows.FontStyles.Italic,
                "oblique" => System.Windows.FontStyles.Oblique,
                "normal" => System.Windows.FontStyles.Normal,
                _ => (FontStyle?)null,
            };
            return value != null;
        }

        if (target == typeof(FontStretch))
        {
            if (!FontStretches.TryGetValue(text, out var stretch)) return false;
            value = stretch;
            return true;
        }

        if (target == typeof(FontFamily))
        {
            value = new FontFamily(NormalizeFontFamily(text));
            return true;
        }

        if (target == typeof(Cursor))
        {
            value = ParseCursor(text);
            return value != null;
        }

        if (target == typeof(Visibility))
        {
            value = text.ToLowerInvariant() switch
            {
                "hidden" => Visibility.Hidden,
                "collapsed" or "none" => Visibility.Collapsed,
                "visible" or "block" or "flex" or "inline" or "inline-block" => Visibility.Visible,
                _ => (Visibility?)null,
            };
            return value != null;
        }

        if (target == typeof(TextAlignment))
        {
            value = text.ToLowerInvariant() switch
            {
                "left" or "start" => TextAlignment.Left,
                "center" => TextAlignment.Center,
                "right" or "end" => TextAlignment.Right,
                "justify" => TextAlignment.Justify,
                _ => (TextAlignment?)null,
            };
            return value != null;
        }

        if (target == typeof(HorizontalAlignment))
        {
            value = text.ToLowerInvariant() switch
            {
                "left" or "start" => HorizontalAlignment.Left,
                "center" => HorizontalAlignment.Center,
                "right" or "end" => HorizontalAlignment.Right,
                "stretch" or "justify" => HorizontalAlignment.Stretch,
                _ => (HorizontalAlignment?)null,
            };
            return value != null;
        }

        if (target == typeof(VerticalAlignment))
        {
            value = text.ToLowerInvariant() switch
            {
                "top" or "start" => VerticalAlignment.Top,
                "center" => VerticalAlignment.Center,
                "bottom" or "end" => VerticalAlignment.Bottom,
                "stretch" => VerticalAlignment.Stretch,
                _ => (VerticalAlignment?)null,
            };
            return value != null;
        }

        if (target == typeof(TextDecorationCollection))
        {
            value = ParseTextDecorations(text);
            return value != null;
        }

        return false;
    }

    private static Cursor? ParseCursor(string text)
    {
        var cursor = text.Trim().ToLowerInvariant() switch
        {
            "pointer" or "hand" => Cursors.Hand,
            "default" or "auto" or "arrow" => Cursors.Arrow,
            "text" or "ibeam" => Cursors.IBeam,
            "wait" or "progress" => Cursors.Wait,
            "crosshair" => Cursors.Cross,
            "move" => Cursors.SizeAll,
            "not-allowed" or "no-drop" => Cursors.No,
            "help" => Cursors.Help,
            "ew-resize" or "col-resize" => Cursors.SizeWE,
            "ns-resize" or "row-resize" => Cursors.SizeNS,
            _ => null,
        };
        return cursor;
    }

    private static TextDecorationCollection? ParseTextDecorations(string text)
    {
        var collection = new TextDecorationCollection();
        foreach (var token in SplitTokens(text))
        {
            switch (token.ToLowerInvariant())
            {
                case "underline":
                    collection.Add(TextDecorations.Underline[0]);
                    break;
                case "line-through":
                case "linethrough":
                case "strikethrough":
                    collection.Add(TextDecorations.Strikethrough[0]);
                    break;
                case "overline":
                    // WPF 没有原生上划线，忽略
                    break;
                case "none":
                    collection.Clear();
                    break;
            }
        }
        collection.Freeze();
        return collection;
    }

    private static string NormalizeFontFamily(string text)
    {
        var first = text.Split(',')[0].Trim().Trim('"', '\'').Trim();
        return first.ToLowerInvariant() switch
        {
            "sans-serif" => "Segoe UI",
            "serif" => "Times New Roman",
            "monospace" => "Consolas",
            "cursive" => "Segoe Script",
            "fantasy" => "Impact",
            _ => first,
        };
    }

    private static bool TryParseLength(string text, out double value)
    {
        value = 0;
        var raw = text.Trim().ToLowerInvariant();
        if (raw.Length == 0) return false;
        if (raw == "auto") { value = double.NaN; return true; }

        var factor = 1.0;
        if (raw.EndsWith("rem", StringComparison.Ordinal)) { factor = 13; raw = raw[..^3]; }
        else if (raw.EndsWith("px", StringComparison.Ordinal)) raw = raw[..^2];
        else if (raw.EndsWith("pt", StringComparison.Ordinal)) { factor = 96.0 / 72.0; raw = raw[..^2]; }
        else if (raw.EndsWith("em", StringComparison.Ordinal)) { factor = 13; raw = raw[..^2]; }
        else if (raw.EndsWith("%", StringComparison.Ordinal)) return false;

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
        value = number * factor;
        return true;
    }

    private static bool TryParseThickness(string text, out Thickness thickness)
    {
        thickness = default;
        var tokens = SplitTokens(text);
        if (tokens.Count is < 1 or > 4) return false;

        var numbers = new double[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            if (!TryParseLength(tokens[i], out numbers[i])) return false;

        thickness = numbers.Length switch
        {
            1 => new Thickness(numbers[0]),
            2 => new Thickness(numbers[0], numbers[1], numbers[0], numbers[1]),
            3 => new Thickness(numbers[0], numbers[1], numbers[2], numbers[1]),
            _ => new Thickness(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
        return true;
    }

    private static bool TryParseCornerRadius(string text, out CornerRadius radius)
    {
        radius = default;
        var tokens = SplitTokens(text);
        if (tokens.Count is < 1 or > 4) return false;

        var numbers = new double[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            if (!TryParseLength(tokens[i], out numbers[i])) return false;

        radius = numbers.Length switch
        {
            1 => new CornerRadius(numbers[0]),
            2 => new CornerRadius(numbers[0], numbers[1], numbers[0], numbers[1]),
            3 => new CornerRadius(numbers[0], numbers[1], numbers[2], numbers[1]),
            _ => new CornerRadius(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
        return true;
    }

    private static List<string> SplitTokens(string text) =>
        text.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool IsNone(string value) =>
        value.Equals("none", StringComparison.OrdinalIgnoreCase)
        || value.Equals("transparent", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadUrl(string raw, out string path)
    {
        path = string.Empty;
        var index = raw.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;

        var start = index + 4;
        var end = raw.IndexOf(')', start);
        if (end < 0) return false;

        path = raw[start..end].Trim().Trim('"', '\'').Trim();
        return path.Length > 0;
    }

    private static bool TryParseVar(string raw, out string name, out string? fallback)
    {
        name = string.Empty;
        fallback = null;
        if (!raw.StartsWith("var(", StringComparison.OrdinalIgnoreCase)) return false;

        var open = raw.IndexOf('(');
        var close = raw.LastIndexOf(')');
        if (open < 0 || close <= open) return false;

        var body = raw[(open + 1)..close];
        var comma = body.IndexOf(',');
        name = (comma >= 0 ? body[..comma] : body).Trim();
        if (name.StartsWith("--", StringComparison.Ordinal)) name = name[2..].Trim();
        if (comma >= 0) fallback = body[(comma + 1)..].Trim();
        return name.Length > 0;
    }

    private static string ResolveValue(string raw, int depth = 0)
    {
        if (depth > 8) return raw;
        if (!TryParseVar(raw, out var name, out var fallback)) return raw;

        if (_variables.TryGetValue(name, out var variable) && !string.IsNullOrWhiteSpace(variable))
            return ResolveValue(variable, depth + 1);

        return fallback != null ? ResolveValue(fallback, depth + 1) : raw;
    }

    private static bool TryGetPaletteReference(string raw, out string key)
    {
        key = string.Empty;
        return TryParseVar(raw, out var name, out _) && TryMapPaletteKey(name, out key);
    }

    /// <summary>把 <c>--accent</c> / <c>--card-hover</c> 这类变量名映射到主题调色板键。</summary>
    public static bool TryMapPaletteKey(string variable, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(variable)) return false;

        var normalized = NormalizeKey(variable);
        if (VariableAliases.TryGetValue(normalized, out var alias)) normalized = alias;
        if (!PaletteKeys.TryGetValue(normalized, out var mapped)) return false;
        key = mapped;
        return true;
    }

    private static Dictionary<string, string> PaletteKeys => _paletteKeyLookup ??= BuildPaletteKeyLookup();

    private static Dictionary<string, string> BuildPaletteKeyLookup()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ThemeService.PaletteKeys)
        {
            map[NormalizeKey(key)] = key;
            var name = key.EndsWith("Brush", StringComparison.OrdinalIgnoreCase) ? key[..^5] : key;
            map[NormalizeKey(name)] = key;
        }
        return map;
    }

    private static string NormalizeKey(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
