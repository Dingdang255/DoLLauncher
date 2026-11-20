using System.Globalization;
using System.Reflection;
using DoLLauncher.Core.Skin;
using DoLLauncher.Services;
using Microsoft.Maui.Controls.Shapes;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 把美化包 skin.css 的规则套用到 Android 端 MAUI 控件，并在卸载 / 重载时逐项还原。
/// 只处理白名单属性，其余声明跳过并做聚合告警；不执行脚本、不发起网络请求。
/// </summary>
public static class MauiSkinStyler
{
    private sealed class ChangeMark
    {
        public BindableProperty Property { get; init; } = null!;
        public object? Original { get; init; }
        public bool WasSet { get; init; }
        public string? DynamicKey { get; init; }
        public BindingBase? Binding { get; init; }
    }

    private sealed class ElementState
    {
        public List<ChangeMark> Changes { get; } = new();
    }

    private static readonly Dictionary<VisualElement, ElementState> Applied =
        new(ReferenceEqualityComparer.Instance);

    private static readonly Dictionary<(Type Type, string Name), BindableProperty?> PropertyCache = new();
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<Element> Attached = new(ReferenceEqualityComparer.Instance);
    private static readonly List<string> Warnings = new();

    private static readonly FieldInfo? DynamicResourcesField =
        typeof(BindableObject).GetField("_dynamicResources", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? PropertiesField =
        typeof(BindableObject).GetField("_properties", BindingFlags.Instance | BindingFlags.NonPublic);

    private static SkinStyleSheet? _sheet;
    private static string _baseDir = string.Empty;
    private static Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private static bool _dark;

    public static bool HasRules => _sheet is { Rules.Count: > 0 };

    public static int RuleCount => _sheet?.Rules.Count ?? 0;

    public static IReadOnlyList<string> LoadWarnings => Warnings;

    /// <summary>加载（或重新加载）样式表；会先还原上一轮修改，再按当前可视树应用一次。</summary>
    public static void Load(SkinStyleSheet? sheet, string baseDir, IReadOnlyDictionary<string, string> variables,
        bool dark)
    {
        RestoreAll();
        _sheet = sheet;
        _baseDir = baseDir ?? string.Empty;
        _variables = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
        _dark = dark;

        Warnings.Clear();
        Warned.Clear();
        if (sheet != null) Warnings.AddRange(sheet.Warnings);

        ApplyAll();
    }

    /// <summary>卸载：还原全部被修改的属性并清空样式表。</summary>
    public static void Unload()
    {
        RestoreAll();
        _sheet = null;
        _baseDir = string.Empty;
        _variables.Clear();
        Warnings.Clear();
        Warned.Clear();
        _dark = false;
    }

    /// <summary>对当前所有窗口页面应用规则（首次加载 / 主题切换后调用）。</summary>
    public static void ApplyAll()
    {
        if (!HasRules) return;
        foreach (var page in RootPages())
        {
            Attach(page);
            ApplyTree(page);
        }
    }

    /// <summary>挂上新页面：后续动态添加的控件也会自动套用样式。</summary>
    public static void Attach(Page? page)
    {
        if (!HasRules || page == null) return;
        if (Attached.Add(page)) page.DescendantAdded += OnDescendantAdded;
        ApplyTree(page);
    }

    /// <summary>单独给某个子树应用样式（例如导航返回后的页面）。</summary>
    public static void ApplyTree(Element? root)
    {
        if (!HasRules || root == null) return;

        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Element>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;

            if (node is VisualElement visual) ApplyElement(visual);

            foreach (var child in VisualChildren(node)) stack.Push(child);
        }
    }

    // ---------- 匹配与应用 ----------

    private static void OnDescendantAdded(object? sender, ElementEventArgs e)
    {
        if (!HasRules || e.Element == null) return;
        ApplyTree(e.Element);
    }

    private static void ApplyElement(VisualElement element)
    {
        var sheet = _sheet;
        if (sheet == null) return;

        foreach (var rule in sheet.Rules)
        {
            if (rule.Source.Length > 200) continue;
            if (rule.RequiresDark.HasValue && rule.RequiresDark.Value != _dark) continue;

            var matched = false;
            foreach (var selector in rule.Selectors)
            {
                if (!MauiSkinSelector.Matches(selector, element)) continue;
                matched = true;
                break;
            }
            if (!matched) continue;

            foreach (var declaration in rule.Declarations)
                ApplyDeclaration(element, declaration);
        }
    }

    private static void ApplyDeclaration(VisualElement element, SkinDeclaration declaration)
    {
        var property = declaration.Property.Trim().ToLowerInvariant();
        var value = declaration.Value.Trim();

        switch (property)
        {
            case "color":
            case "foreground":
                if (TryParseColor(ResolveValue(value), out var textColor)) ApplyTextColor(element, textColor);
                break;

            case "background":
            case "background-color":
            case "bg":
                if (TryReadUrl(value, out var backgroundPath)) ApplyBackgroundImage(element, backgroundPath);
                else if (TryParseColor(ResolveValue(value), out var background))
                    Set(element, VisualElement.BackgroundColorProperty, background);
                break;

            case "background-image":
                if (TryReadUrl(value, out var imagePath)) ApplyBackgroundImage(element, imagePath);
                break;

            case "border-color":
                if (TryParseColor(ResolveValue(value), out var borderColor)) ApplyBorderColor(element, borderColor);
                break;

            case "stroke":
                if (TryParseColor(ResolveValue(value), out var strokeColor))
                    SetProperty(element, "Stroke", new SolidColorBrush(strokeColor));
                break;

            case "border-width":
                if (TryParseLength(ResolveValue(value), out var borderWidth))
                    ApplyBorderWidth(element, borderWidth);
                break;

            case "border-radius":
            case "corner-radius":
            case "radius":
                if (TryParseRadius(ResolveValue(value), out var radius)) ApplyCornerRadius(element, radius);
                break;

            case "font-size":
                if (TryParseLength(ResolveValue(value), out var fontSize))
                    SetProperty(element, "FontSize", fontSize);
                break;

            case "font-family":
                SetProperty(element, "FontFamily", NormalizeFontFamily(ResolveValue(value)));
                break;

            case "font-weight":
                ApplyFontWeight(element, ResolveValue(value));
                break;

            case "font-style":
                ApplyFontStyle(element, ResolveValue(value));
                break;

            case "padding":
                if (TryParseThickness(ResolveValue(value), out var padding))
                    SetProperty(element, "Padding", padding);
                break;

            case "margin":
                if (TryParseThickness(ResolveValue(value), out var margin))
                    SetProperty(element, "Margin", margin);
                break;

            case "opacity":
                if (TryParseLength(ResolveValue(value), out var opacity))
                    SetProperty(element, "Opacity", Math.Clamp(opacity, 0, 1));
                break;

            case "width":
                if (TryParseLength(ResolveValue(value), out var width))
                    Set(element, VisualElement.WidthRequestProperty, width);
                break;

            case "height":
                if (TryParseLength(ResolveValue(value), out var height))
                    Set(element, VisualElement.HeightRequestProperty, height);
                break;

            case "min-width":
                if (TryParseLength(ResolveValue(value), out var minWidth))
                    Set(element, VisualElement.MinimumWidthRequestProperty, minWidth);
                break;

            case "max-width":
                if (TryParseLength(ResolveValue(value), out var maxWidth))
                    Set(element, VisualElement.MaximumWidthRequestProperty, maxWidth);
                break;

            case "min-height":
                if (TryParseLength(ResolveValue(value), out var minHeight))
                    Set(element, VisualElement.MinimumHeightRequestProperty, minHeight);
                break;

            case "max-height":
                if (TryParseLength(ResolveValue(value), out var maxHeight))
                    Set(element, VisualElement.MaximumHeightRequestProperty, maxHeight);
                break;

            case "gap":
                ApplyGap(element, ResolveValue(value));
                break;

            case "row-gap":
                if (element is Grid rowGrid && TryParseLength(ResolveValue(value), out var rowGap))
                    SetProperty(rowGrid, "RowSpacing", rowGap);
                break;

            case "column-gap":
                if (element is Grid columnGrid && TryParseLength(ResolveValue(value), out var columnGap))
                    SetProperty(columnGrid, "ColumnSpacing", columnGap);
                break;

            case "text-align":
                SetProperty(element, "HorizontalTextAlignment", ParseTextAlignment(ResolveValue(value)));
                break;

            case "letter-spacing":
                if (TryParseLength(ResolveValue(value), out var spacing))
                    SetProperty(element, "CharacterSpacing", spacing);
                break;

            default:
                Warn($"暂不支持样式属性：{property}");
                break;
        }
    }

    private static void ApplyTextColor(VisualElement element, Color color)
    {
        if (element is Label label) Set(label, Label.TextColorProperty, color);
        else if (element is Button button) Set(button, Button.TextColorProperty, color);
        else if (element is Entry entry) Set(entry, Entry.TextColorProperty, color);
        else if (element is Editor editor) Set(editor, Editor.TextColorProperty, color);
        else if (element is Picker picker) Set(picker, Picker.TextColorProperty, color);
        else if (element is SearchBar searchBar) Set(searchBar, SearchBar.TextColorProperty, color);
        else if (element is DatePicker datePicker) Set(datePicker, DatePicker.TextColorProperty, color);
        else if (element is TimePicker timePicker) Set(timePicker, TimePicker.TextColorProperty, color);
    }

    private static void ApplyBorderColor(VisualElement element, Color color)
    {
        if (element is Border border) Set(border, Border.StrokeProperty, new SolidColorBrush(color));
        else if (element is Button button) Set(button, Button.BorderColorProperty, color);
    }

    private static void ApplyBorderWidth(VisualElement element, double width)
    {
        if (element is Border border) Set(border, Border.StrokeThicknessProperty, width);
        else if (element is Button button) Set(button, Button.BorderWidthProperty, width);
    }

    private static void ApplyCornerRadius(VisualElement element, double radius)
    {
        if (element is Border border)
        {
            Set(border, Border.StrokeShapeProperty,
                new RoundRectangle { CornerRadius = new CornerRadius(radius) });
        }
        else if (element is Button button)
        {
            Set(button, Button.CornerRadiusProperty, (int)Math.Round(radius));
        }
    }

    private static void ApplyFontWeight(VisualElement element, string raw)
    {
        var lower = raw.Trim().ToLowerInvariant();
        var bold = lower.Contains("bold", StringComparison.Ordinal) || lower is "600" or "700" or "800" or "900";
        var current = GetFontAttributes(element);
        var next = bold
            ? (current | FontAttributes.Bold)
            : (current & ~FontAttributes.Bold);
        SetProperty(element, "FontAttributes", next);
    }

    private static void ApplyFontStyle(VisualElement element, string raw)
    {
        var italic = raw.Contains("italic", StringComparison.OrdinalIgnoreCase) ||
                     raw.Contains("oblique", StringComparison.OrdinalIgnoreCase);
        var current = GetFontAttributes(element);
        var next = italic
            ? (current | FontAttributes.Italic)
            : (current & ~FontAttributes.Italic);
        SetProperty(element, "FontAttributes", next);
    }

    private static FontAttributes GetFontAttributes(VisualElement element) => element switch
    {
        Label label => label.FontAttributes,
        Button button => button.FontAttributes,
        Entry entry => entry.FontAttributes,
        Editor editor => editor.FontAttributes,
        _ => FontAttributes.None,
    };

    private static void ApplyGap(VisualElement element, string raw)
    {
        var tokens = SplitTokens(raw);
        if (tokens.Count == 0 || !TryParseLength(tokens[0], out var first)) return;
        var second = first;
        if (tokens.Count > 1 && !TryParseLength(tokens[1], out second)) return;

        // 不直接引用 StackBase 类型，按类型名判断可以兼容所有 StackLayout 变体
        var typeName = element.GetType().Name;
        if (typeName.Contains("StackLayout", StringComparison.Ordinal))
        {
            SetProperty(element, "Spacing", first);
        }
        else if (element is Grid grid)
        {
            SetProperty(grid, "RowSpacing", first);
            SetProperty(grid, "ColumnSpacing", second);
        }
    }

    private static void ApplyBackgroundImage(VisualElement element, string rawPath)
    {
        var normalized = (rawPath ?? string.Empty).Trim();
        if (normalized.Length == 0) return;
        if (normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            Warn("background-image 只允许美化包内的相对路径。");
            return;
        }
        if (!SkinRules.IsSafeRelativePath(normalized))
        {
            Warn("background-image 路径不安全（必须为包内相对路径）。");
            return;
        }

        var file = System.IO.Path.Combine(_baseDir,
            normalized.Replace('\\', '/').Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(file)) return;

        // ImageBrush 的可见性随 MAUI 版本变化，用反射创建，避免直接引用导致编译失败
        var brush = CreateImageBrush(file);
        if (brush == null)
        {
            Warn("当前 MAUI 版本不支持 background-image。");
            return;
        }
        SetProperty(element, "Background", brush);
    }

    private static object? CreateImageBrush(string file)
    {
        var type = Type.GetType("Microsoft.Maui.Controls.ImageBrush, Microsoft.Maui.Controls");
        if (type == null) return null;

        try
        {
            var brush = Activator.CreateInstance(type, nonPublic: true);
            if (brush == null) return null;

            type.GetProperty("ImageSource")?.SetValue(brush, ImageSource.FromFile(file));
            type.GetProperty("Aspect")?.SetValue(brush, Aspect.AspectFill);
            return brush;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 属性写入与还原 ----------

    private static void SetProperty(VisualElement element, string propertyName, object? value)
    {
        if (value == null) return;
        var property = FindProperty(element.GetType(), propertyName);
        if (property == null)
        {
            Warn($"控件 {element.GetType().Name} 不支持样式属性：{propertyName}");
            return;
        }
        Set(element, property, value);
    }

    private static void Set(VisualElement element, BindableProperty property, object? value)
    {
        if (value == null) return;

        var state = GetState(element);
        if (state.Changes.All(change => change.Property != property))
        {
            state.Changes.Add(new ChangeMark
            {
                Property = property,
                Original = element.GetValue(property),
                WasSet = element.IsSet(property),
                DynamicKey = DynamicKeyOf(element, property),
                Binding = BindingOf(element, property),
            });
        }

        try
        {
            element.SetValue(property, value);
        }
        catch (Exception ex)
        {
            Warn($"应用 {property.PropertyName} 失败：{ex.Message}");
        }
    }

    private static ElementState GetState(VisualElement element)
    {
        if (Applied.TryGetValue(element, out var state)) return state;
        state = new ElementState();
        Applied[element] = state;
        return state;
    }

    private static void RestoreAll()
    {
        foreach (var pair in Applied.ToArray()) RestoreState(pair.Key, pair.Value);
        Applied.Clear();

        // 先摘掉事件，避免重复 Load 造成同一个页面被多次应用
        foreach (var page in Attached)
            page.DescendantAdded -= OnDescendantAdded;
        Attached.Clear();
        PropertyCache.Clear();
    }

    private static void RestoreState(VisualElement element, ElementState state)
    {
        for (var i = state.Changes.Count - 1; i >= 0; i--)
        {
            var change = state.Changes[i];
            try
            {
                if (!string.IsNullOrEmpty(change.DynamicKey))
                {
                    element.SetDynamicResource(change.Property, change.DynamicKey);
                    continue;
                }

                if (change.Binding != null)
                {
                    element.SetBinding(change.Property, change.Binding);
                    continue;
                }

                if (change.WasSet && change.Original != null)
                {
                    element.SetValue(change.Property, change.Original);
                    continue;
                }

                element.ClearValue(change.Property);
            }
            catch
            {
                // 单个属性还原失败不影响其它属性
            }
        }
    }

    /// <summary>原始值是 DynamicResource 时取出资源键，卸载后重新挂接，避免主题色失效。</summary>
    private static string? DynamicKeyOf(BindableObject element, BindableProperty property)
    {
        try
        {
            if (DynamicResourcesField?.GetValue(element) is System.Collections.IDictionary map &&
                map.Contains(property))
                return map[property] as string;
        }
        catch
        {
            // 反射失败时退回普通还原逻辑
        }
        return null;
    }

    /// <summary>原始值是 AppThemeBinding 等绑定时取出绑定对象，卸载后重新挂接以免深浅色切换失效。</summary>
    private static BindingBase? BindingOf(BindableObject element, BindableProperty property)
    {
        try
        {
            if (PropertiesField?.GetValue(element) is not System.Collections.IDictionary map ||
                !map.Contains(property)) return null;

            var context = map[property];
            if (context == null) return null;

            var bindingProperty = context.GetType().GetProperty("Binding",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return bindingProperty?.GetValue(context) as BindingBase;
        }
        catch
        {
            return null;
        }
    }

    private static BindableProperty? FindProperty(Type type, string name)
    {
        var key = (type, name);
        if (PropertyCache.TryGetValue(key, out var cached)) return cached;

        BindableProperty? found = null;
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            var field = current.GetField(name + "Property",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (field?.GetValue(null) is BindableProperty property)
            {
                found = property;
                break;
            }
        }

        PropertyCache[key] = found;
        return found;
    }

    // ---------- 值解析 ----------

    private static string ResolveValue(string raw, int depth = 0)
    {
        if (depth > 8) return raw;
        if (!TryParseVar(raw, out var name, out var fallback)) return raw;

        var key = SkinPalette.NormalizeKey(name);
        if (_variables.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return ResolveValue(value, depth + 1);

        return fallback != null ? ResolveValue(fallback, depth + 1) : raw;
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

    private static bool TryParseColor(string raw, out Color color)
    {
        color = Colors.Transparent;
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            color = Colors.Transparent;
            return true;
        }

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            return TryParseColorFunction(text, out color);

        try
        {
            color = Color.FromArgb(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseColorFunction(string text, out Color color)
    {
        color = Colors.Transparent;
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open) return false;

        var body = text[(open + 1)..close].Replace('/', ' ').Replace(',', ' ');
        var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4) return false;

        if (!TryChannel(parts[0], out var r) || !TryChannel(parts[1], out var g) || !TryChannel(parts[2], out var b))
            return false;

        var a = (byte)255;
        if (parts.Length == 4 && TryChannel(parts[3], out var alpha)) a = alpha;

        color = Color.FromRgba(r, g, b, a);
        return true;
    }

    private static bool TryChannel(string text, out byte value)
    {
        value = 0;
        var raw = text.Trim();
        if (!double.TryParse(raw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return false;
        if (raw.EndsWith('%')) number = number / 100 * 255;
        value = (byte)Math.Round(Math.Clamp(number, 0, 255), MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool TryParseLength(string text, out double value)
    {
        value = 0;
        var raw = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0) return false;

        var factor = 1.0;
        if (raw.EndsWith("pt", StringComparison.Ordinal)) { factor = 1.3333; raw = raw[..^2]; }
        else if (raw.EndsWith("px", StringComparison.Ordinal)) raw = raw[..^2];
        else if (raw.EndsWith("dp", StringComparison.Ordinal)) raw = raw[..^2];
        else if (raw.EndsWith("%", StringComparison.Ordinal)) return false;

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
        value = number * factor;
        return true;
    }

    private static bool TryParseRadius(string text, out double value)
    {
        value = 0;
        var tokens = SplitTokens(text);
        if (tokens.Count == 0) return false;

        double? first = null;
        foreach (var token in tokens)
        {
            if (!TryParseLength(token, out var length)) continue;
            first ??= length;
            if (length > 0)
            {
                value = length;
                return true;
            }
        }

        if (first == null) return false;
        value = first.Value;
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

    private static TextAlignment ParseTextAlignment(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "center" => TextAlignment.Center,
        "right" or "end" => TextAlignment.End,
        "justify" => TextAlignment.Justify,
        _ => TextAlignment.Start,
    };

    private static string NormalizeFontFamily(string text)
    {
        var first = text.Split(',')[0].Trim().Trim('"', '\'').Trim();
        return first.ToLowerInvariant() switch
        {
            "sans-serif" => "sans-serif",
            "serif" => "serif",
            "monospace" => "monospace",
            _ => first,
        };
    }

    private static List<string> SplitTokens(string text) =>
        (text ?? string.Empty)
        .Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
        .ToList();

    private static IEnumerable<Element> VisualChildren(Element element)
    {
        if (element is not IVisualTreeElement tree) yield break;
        foreach (var child in tree.GetVisualChildren())
            if (child is Element childElement) yield return childElement;
    }

    private static List<Page> RootPages()
    {
        var result = new List<Page>();

        void Visit(Page? page)
        {
            if (page == null || result.Contains(page)) return;
            result.Add(page);

            switch (page)
            {
                case NavigationPage nav:
                    foreach (var child in nav.Navigation.NavigationStack) Visit(child);
                    break;
                case TabbedPage tabs:
                    foreach (var child in tabs.Children) Visit(child);
                    break;
                case FlyoutPage flyout:
                    Visit(flyout.Detail);
                    break;
            }

            foreach (var modal in page.Navigation.ModalStack) Visit(modal);
        }

        var app = Application.Current;
        if (app == null) return result;
        foreach (var window in app.Windows) Visit(window.Page);
        return result;
    }

    private static void Warn(string message)
    {
        if (!Warned.Add(message)) return;
        if (Warnings.Count >= 100) return;
        Warnings.Add(message);
        LogService.Append("[美化包] " + message);
    }
}
