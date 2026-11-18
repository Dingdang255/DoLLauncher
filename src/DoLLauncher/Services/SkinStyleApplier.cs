using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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

    public static bool HasRules => _sheet is { Rules.Count: > 0 };

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
        RestoreAll();
        _sheet = sheet;
        _variables = sheet?.VariablesFor(ThemeService.IsDark) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _baseDir = baseDir ?? string.Empty;
        ApplyAll();
    }

    public static void Unload()
    {
        RestoreAll();
        _sheet = null;
        _variables.Clear();
        _baseDir = string.Empty;
    }

    public static void Reapply()
    {
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
        if (!HasRules) return;
        if (sender is FrameworkElement element) ApplyElement(element);
    }

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

        ElementState? state = null;
        foreach (var rule in sheet.Rules)
        {
            if (!RuleMatches(rule, element)) continue;
            state ??= GetState(element);
            foreach (var declaration in rule.Declarations)
            {
                var property = declaration.Property.Trim().ToLowerInvariant();
                var value = declaration.Value.Trim();

                switch (property)
                {
                    case "corner-radius":
                    case "border-radius":
                    case "radius":
                        if (TryParseCornerRadius(ResolveValue(value), out var radius))
                            ApplyCornerRadius(element, state, radius);
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
                        else ApplyBrush(element, state, "Background", value);
                        break;

                    case "color":
                    case "foreground":
                        ApplyBrush(element, state, "Foreground", value);
                        break;

                    case "border-color":
                        ApplyBrush(element, state, "BorderBrush", value);
                        break;

                    case "fill":
                        ApplyBrush(element, state, "Fill", value);
                        break;

                    case "stroke":
                        ApplyBrush(element, state, "Stroke", value);
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
                        ApplyBrush(element, state, "BorderBrush", value);
                        break;

                    case "letter-spacing":
                        // WPF 的 TextBlock 没有字距属性，忽略即可
                        break;

                    default:
                        ApplySimpleProperty(element, state, property, value);
                        break;
                }
            }
        }

        // 面板自身的 gap/align 需要作用到子元素；子元素加载时也要回查父面板
        if (element is Panel panel && PanelLayouts.TryGetValue(panel, out var layout))
            ApplyLayoutToChildren(panel, layout);
        ApplyParentLayout(element);
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

    private static void ApplyBrush(FrameworkElement element, ElementState state, string clrName, string raw)
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
        if (IsNone(value))
        {
            SetLocal(element, state, property, Brushes.Transparent);
            return;
        }

        if (SkinColors.TryParse(value, out var color)) SetLocal(element, state, property, SkinColors.ToBrush(color));
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

    private static void ApplyCornerRadius(FrameworkElement element, ElementState state, CornerRadius radius)
    {
        if (element is Border border)
        {
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

    private static bool RuleMatches(SkinStyleRule rule, FrameworkElement element)
    {
        if (rule.RequiresDark.HasValue && ThemeService.IsDark != rule.RequiresDark.Value) return false;
        foreach (var selector in rule.Selectors)
            if (Matches(selector, element)) return true;
        return false;
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
            var key = StyleKeyOf(element);
            if (key == null) return false;
            foreach (var cls in selector.Classes)
                if (!key.Equals(cls, StringComparison.OrdinalIgnoreCase)) return false;
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
        if (Applied.Count == 0)
        {
            PanelLayouts.Clear();
            return;
        }
        foreach (var pair in Applied.ToArray()) RestoreState(pair.Key, pair.Value);
        Applied.Clear();
        PanelLayouts.Clear();
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
