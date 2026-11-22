using System.Text.Json.Serialization;
using DoLLauncher.Core.Theme;

namespace DoLLauncher.Core.Skin;

/// <summary>
/// 本地 UI 美化包（zip）内的 skin.json。
/// 可覆盖窗口背景、弹窗背景、主题色、CSS 样式表与图片，不执行任何脚本。
/// </summary>
public sealed class SkinManifest
{
    public const string CurrentFormat = "dolskin/3";

    /// <summary>兼容的历史格式；新增字段均为可选，旧包可以继续使用。</summary>
    public static readonly string[] SupportedFormats = { "dolskin/1", "dolskin/2", "dolskin/3" };

    [JsonPropertyName("format")] public string Format { get; set; } = CurrentFormat;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

    /// <summary>主色（#AARRGGBB 或 #RRGGBB）；为空表示沿用启动器当前主题色。</summary>
    [JsonPropertyName("accentColor")] public string AccentColor { get; set; } = string.Empty;

    /// <summary>主窗口背景图，相对包内路径。</summary>
    [JsonPropertyName("windowBackgroundImage")] public string WindowBackgroundImage { get; set; } = string.Empty;

    /// <summary>主窗口背景底色（没有背景图或作为半透明叠加底色时使用）。</summary>
    [JsonPropertyName("windowBackgroundColor")] public string WindowBackgroundColor { get; set; } = string.Empty;

    /// <summary>弹窗背景图，相对包内路径。</summary>
    [JsonPropertyName("dialogBackgroundImage")] public string DialogBackgroundImage { get; set; } = string.Empty;

    /// <summary>弹窗背景底色（没有背景图时使用）。</summary>
    [JsonPropertyName("dialogBackgroundColor")] public string DialogBackgroundColor { get; set; } = string.Empty;

    /// <summary>CSS 样式表路径；留空时自动查找包根目录的 skin.css / style.css。</summary>
    [JsonPropertyName("styleSheet")] public string StyleSheet { get; set; } = string.Empty;

    /// <summary>额外变量（等价于 CSS 里 :root 的 --变量），可覆盖调色板或供样式表引用。</summary>
    [JsonPropertyName("variables")] public Dictionary<string, string> Variables { get; set; } = new();

    /// <summary>dolskin/3 可切换主题列表；旧包为空。</summary>
    [JsonPropertyName("themes")] public List<SkinTheme> Themes { get; set; } = new();

    /// <summary>默认主题 id；为空或 <c>auto</c> 时由平台决定。</summary>
    [JsonPropertyName("defaultTheme")] public string DefaultTheme { get; set; } = string.Empty;

    /// <summary>dolskin/3 环境层与动效开关；旧包为 null。</summary>
    [JsonPropertyName("effects")] public SkinEffectOptions? Effects { get; set; }

    /// <summary>dolskin/3 美化包设置页控件声明；旧包为空。</summary>
    [JsonPropertyName("settings")] public List<SkinSettingDefinition> Settings { get; set; } = new();

    /// <summary>dolskin/3 声明式组件树（插槽）；旧包为空。</summary>
    [JsonPropertyName("components")] public List<SkinComponentDefinition> Components { get; set; } = new();

    /// <summary>
    /// Android 专属配置（可空）。WPF 端忽略该字段；旧包没有该段时行为不变。
    /// </summary>
    [JsonPropertyName("android")] public AndroidSkinOptions? Android { get; set; }

    /// <summary>
    /// 实际使用的样式表相对路径：<see cref="AndroidSkinOptions.StyleSheet"/> 优先，
    /// 其次顶层 <see cref="StyleSheet"/>；都为空时返回空串（由平台自动查找 skin.css）。
    /// </summary>
    public string PreferredStyleSheet
    {
        get
        {
            var android = (Android?.StyleSheet ?? string.Empty).Trim();
            return android.Length > 0 ? android : (StyleSheet ?? string.Empty).Trim();
        }
    }
}

/// <summary>Android 专属的美化包配置：可单独提供样式表与变量覆盖。</summary>
public sealed class AndroidSkinOptions
{
    /// <summary>Android 端使用的样式表路径（相对包内）；为空时回退到顶层 styleSheet。</summary>
    [JsonPropertyName("styleSheet")] public string StyleSheet { get; set; } = string.Empty;

    /// <summary>Android 端额外的变量（优先级高于顶层 variables 与 CSS :root）。</summary>
    [JsonPropertyName("variables")] public Dictionary<string, string> Variables { get; set; } = new();
}

/// <summary>美化包格式校验规则（平台无关）。</summary>
public static class SkinRules
{
    public const int MaxThemes = 16;
    public const int MaxKeyframes = 64;
    public const int MaxSettings = 32;
    public const int MaxParticles = 24;
    public const int MaxComponents = 100;
    public const int MaxComponentDepth = 4;
    public const int MaxComponentProps = 32;
    public const int MaxComponentEvents = 8;
    public const int MaxComponentBindings = 8;
    public const int MaxBindingLength = 512;
    public const double MaxAnimationMs = 60_000;
    public const int MaxTransformOps = 8;

    /// <summary>声明式组件支持的控件类型。</summary>
    public static readonly string[] ComponentWidgets =
    {
        "Label", "Badge", "Divider", "Spacer", "Image", "Icon",
        "ProgressBar", "Card", "Row", "Column", "Banner",
        "Button", "Toggle", "Slider", "Input", "Select",
    };

    /// <summary>组件支持的事件名（轻量脚本入口）。</summary>
    public static readonly string[] ComponentEvents = { "click", "changed" };

    /// <summary>数据绑定支持的属性名。</summary>
    public static readonly string[] BindingProperties =
    {
        "text", "visible", "enabled", "value", "background", "foreground",
    };

    public static bool IsKnownComponentWidget(string? widget)
        => ComponentWidgets.Any(item => item.Equals((widget ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownComponentEvent(string? name)
        => ComponentEvents.Any(item => item.Equals((name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownBindingProperty(string? name)
        => BindingProperties.Any(item => item.Equals((name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsValidComponentSlot(string? slot)
    {
        var text = (slot ?? string.Empty).Trim();
        return text.Equals("before", StringComparison.OrdinalIgnoreCase)
               || text.Equals("after", StringComparison.OrdinalIgnoreCase)
               || text.Equals("start", StringComparison.OrdinalIgnoreCase)
               || text.Equals("end", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>只接受 <c>#Name</c>、<c>Type</c> 或 <c>Type.Class</c> 的简单写法。</summary>
    public static bool IsValidComponentHost(string? host)
    {
        var text = (host ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        if (text.StartsWith('#')) return text.Length > 1 && IsIdentifier(text[1..]);

        var dot = text.IndexOf('.');
        if (dot < 0) return IsIdentifier(text);
        if (dot == 0 || dot == text.Length - 1) return false;
        return IsIdentifier(text[..dot]) && IsIdentifier(text[(dot + 1)..]);
    }

    private static bool IsIdentifier(string text)
    {
        if (text.Length == 0) return false;
        foreach (var c in text)
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
        return true;
    }

    /// <summary>校验清单内容与相对资源路径；失败时返回中文原因。</summary>
    public static (bool Ok, string Reason) Validate(SkinManifest? manifest)
    {
        if (manifest == null) return (false, "美化包里没有可读取的 skin.json。");

        var format = (manifest.Format ?? string.Empty).Trim();
        if (!SkinManifest.SupportedFormats.Any(item => item.Equals(format, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"不支持的美化包格式：{format}。当前支持 {string.Join(" / ", SkinManifest.SupportedFormats)}。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name)) return (false, "skin.json 缺少 name 字段。");

        if (HasInvalidColor(manifest.AccentColor, out var accentError)) return (false, "accentColor " + accentError);
        if (HasInvalidColor(manifest.WindowBackgroundColor, out var windowError)) return (false, "windowBackgroundColor " + windowError);
        if (HasInvalidColor(manifest.DialogBackgroundColor, out var dialogError)) return (false, "dialogBackgroundColor " + dialogError);

        foreach (var (path, label) in new[]
                 {
                     (manifest.WindowBackgroundImage, "windowBackgroundImage"),
                     (manifest.DialogBackgroundImage, "dialogBackgroundImage"),
                     (manifest.StyleSheet, "styleSheet"),
                     (manifest.Android?.StyleSheet ?? string.Empty, "android.styleSheet"),
                 })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!IsSafeRelativePath(path!)) return (false, $"{label} 必须是包内的相对路径，且不能包含 ..。");
        }

        if (HasInvalidVariables(manifest.Variables, "variables", out var topError)) return (false, topError);
        if (HasInvalidVariables(manifest.Android?.Variables, "android.variables", out var androidError))
            return (false, androidError);

        return (true, string.Empty);
    }

    /// <summary>
    /// 对可选字段（主题 / 环境层 / 设置项 / 关键帧 / 动画）做非致命校验，返回中文告警。
    /// 非法项由调用方忽略即可，不会导致整包拒绝。
    /// </summary>
    public static IReadOnlyList<string> CollectWarnings(SkinManifest? manifest)
        => CollectWarnings(manifest, null);

    /// <summary>同上；额外检查样式表里的关键帧、动画时长与 transform 操作数。</summary>
    public static IReadOnlyList<string> CollectWarnings(SkinManifest? manifest, SkinStyleSheet? sheet)
    {
        var warnings = new List<string>();
        if (manifest == null) return warnings;

        var themes = manifest.Themes ?? new List<SkinTheme>();
        if (themes.Count > MaxThemes)
            warnings.Add($"themes 数量超过上限 {MaxThemes}，超出部分将被忽略。");

        var themeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in themes)
        {
            if (theme == null)
            {
                warnings.Add("themes 中存在空主题项，已忽略。");
                continue;
            }

            var id = (theme.Id ?? string.Empty).Trim();
            if (id.Length == 0) warnings.Add("themes 中存在缺少 id 的主题，已忽略。");
            else if (!themeIds.Add(id)) warnings.Add($"主题 id 重复：{id}，仅保留第一个。");

            if (!string.IsNullOrWhiteSpace(theme.Texture) && !IsSafeRelativePath(theme.Texture))
                warnings.Add($"主题 {id} 的 texture 必须是包内相对路径且不能包含 ..：{theme.Texture}");
            if (!string.IsNullOrWhiteSpace(theme.Ornament) && !IsSafeRelativePath(theme.Ornament))
                warnings.Add($"主题 {id} 的 ornament 必须是包内相对路径且不能包含 ..：{theme.Ornament}");
            if (theme.Slice <= 0)
                warnings.Add($"主题 {id} 的 slice 必须大于 0，已按默认值处理。");

            CollectVariableWarnings(theme.Variables, $"主题 {id} 的 variables", warnings);
        }

        var defaultTheme = (manifest.DefaultTheme ?? string.Empty).Trim();
        if (defaultTheme.Length > 0 && !defaultTheme.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !themeIds.Contains(defaultTheme))
        {
            warnings.Add($"defaultTheme 指向不存在的主题：{defaultTheme}");
        }

        if (manifest.Effects?.Ambient is { } ambient)
        {
            if (ambient.Particles < 0 || ambient.Particles > MaxParticles)
            {
                warnings.Add(
                    $"effects.ambient.particles 应在 0..{MaxParticles} 之间（推荐 0/8/16/24），当前为 {ambient.Particles}。");
            }

            if (ambient.Opacity < 0 || ambient.Opacity > 1)
                warnings.Add($"effects.ambient.opacity 应在 0..1 之间，当前为 {ambient.Opacity}。");
        }

        var settings = manifest.Settings ?? new List<SkinSettingDefinition>();
        if (settings.Count > MaxSettings)
            warnings.Add($"settings 数量超过上限 {MaxSettings}，超出部分将被忽略。");

        var settingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (setting == null)
            {
                warnings.Add("settings 中存在空设置项，已忽略。");
                continue;
            }

            var key = (setting.Key ?? string.Empty).Trim();
            if (key.Length == 0) warnings.Add("settings 中存在缺少 key 的设置项，已忽略。");
            else if (!settingKeys.Add(key)) warnings.Add($"设置项 key 重复：{key}，仅保留第一个。");

            var type = (setting.Type ?? string.Empty).Trim().ToLowerInvariant();
            switch (type)
            {
                case "toggle":
                case "color":
                    break;

                case "select":
                    var hasOptions = setting.Options is { Count: > 0 };
                    if (!hasOptions && !setting.OptionsFromThemes)
                        warnings.Add($"设置项 {key} 是 select，但没有 options 且未启用 optionsFromThemes，已忽略。");
                    break;

                case "slider":
                    if (setting.Min is not { } min || setting.Max is not { } max || min >= max)
                        warnings.Add($"设置项 {key} 是 slider，但 min/max 缺失或 min 不小于 max，已忽略。");
                    break;

                default:
                    warnings.Add($"设置项 {key} 的 type 不支持：{setting.Type}（仅支持 toggle/select/slider/color），已忽略。");
                    break;
            }
        }

        CollectComponentWarnings(manifest.Components, warnings);

        if (sheet != null) CollectSheetWarnings(sheet, warnings);
        return warnings;
    }

    /// <summary>声明式组件：数量、深度、id、host、slot、widget、props 上限，只告警不拒绝整包。</summary>
    private static void CollectComponentWarnings(
        List<SkinComponentDefinition>? components, List<string> warnings)
    {
        var list = components ?? new List<SkinComponentDefinition>();
        if (list.Count > MaxComponents)
            warnings.Add($"components 数量超过上限 {MaxComponents}，超出部分将被忽略。");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CheckComponentLevel(list, 1, ids, warnings);
    }

    private static void CheckComponentLevel(
        List<SkinComponentDefinition>? components, int depth, HashSet<string> ids, List<string> warnings)
    {
        if (components == null || components.Count == 0) return;
        if (depth > MaxComponentDepth)
        {
            warnings.Add($"组件嵌套深度超过上限 {MaxComponentDepth}，更深的子组件将被忽略。");
            return;
        }

        foreach (var component in components)
        {
            if (component == null)
            {
                warnings.Add("components 中存在空组件项，已忽略。");
                continue;
            }

            var id = (component.Id ?? string.Empty).Trim();
            if (id.Length == 0) warnings.Add($"组件缺少 id（widget={component.Widget}），已忽略。");
            else if (!ids.Add(id)) warnings.Add($"组件 id 重复：{id}，仅保留第一个。");

            var host = (component.Host ?? string.Empty).Trim();
            if (host.Length == 0) warnings.Add($"组件 {id} 缺少 host，已忽略。");
            else if (!IsValidComponentHost(host))
                warnings.Add($"组件 {id} 的 host 写法无效：{host}（只支持 #Name、Type、Type.Class）");

            if (!IsValidComponentSlot(component.Slot))
                warnings.Add($"组件 {id} 的 slot 不支持：{component.Slot}（仅支持 before/after/start/end）");

            if (!IsKnownComponentWidget(component.Widget))
                warnings.Add($"组件 {id} 的 widget 不支持：{component.Widget}，已忽略。");

            if (component.Props is { Count: > MaxComponentProps })
                warnings.Add($"组件 {id} 的 props 超过上限 {MaxComponentProps}，超出部分将被忽略。");

            CollectEventWarnings(component, id, warnings);
            CollectBindingWarnings(component, id, warnings);

            CheckComponentLevel(component.Children, depth + 1, ids, warnings);
        }
    }

    /// <summary>事件脚本：数量、事件名、脚本语法与体积上限。</summary>
    private static void CollectEventWarnings(
        SkinComponentDefinition component, string id, List<string> warnings)
    {
        var events = component.Events ?? new Dictionary<string, string>();
        if (events.Count > MaxComponentEvents)
        {
            warnings.Add($"组件 {id} 的 events 超过上限 {MaxComponentEvents}，超出部分将被忽略。");
            return;
        }

        foreach (var pair in events)
        {
            var name = (pair.Key ?? string.Empty).Trim();
            if (!IsKnownComponentEvent(name))
            {
                warnings.Add($"组件 {id} 的事件不支持：{pair.Key}（仅支持 click / changed）");
                continue;
            }

            if (!SkinScriptEngine.TryParse(pair.Value, out _, out var scriptError))
                warnings.Add($"组件 {id} 的 {name} 脚本无效：{scriptError}");
        }
    }

    /// <summary>数据绑定：数量、属性名、模板语法与长度上限。</summary>
    private static void CollectBindingWarnings(
        SkinComponentDefinition component, string id, List<string> warnings)
    {
        var bindings = component.Bind ?? new Dictionary<string, string>();
        if (bindings.Count > MaxComponentBindings)
        {
            warnings.Add($"组件 {id} 的 bind 超过上限 {MaxComponentBindings}，超出部分将被忽略。");
            return;
        }

        foreach (var pair in bindings)
        {
            var property = (pair.Key ?? string.Empty).Trim();
            if (!IsKnownBindingProperty(property))
            {
                warnings.Add($"组件 {id} 的绑定属性不支持：{pair.Key}"
                             + "（仅支持 text/visible/enabled/value/background/foreground）");
                continue;
            }

            var template = pair.Value ?? string.Empty;
            if (template.Length > MaxBindingLength)
            {
                warnings.Add($"组件 {id} 的绑定 {property} 超过 {MaxBindingLength} 字符上限。");
                continue;
            }

            if (!SkinBindingTemplate.TryParse(template, out _, out var bindingError))
                warnings.Add($"组件 {id} 的绑定 {property} 无效：{bindingError}");
        }
    }

    private static void CollectSheetWarnings(SkinStyleSheet sheet, List<string> warnings)
    {
        var keyframeCount = sheet.Keyframes.Sum(pair => pair.Value?.Count ?? 0);
        if (keyframeCount > MaxKeyframes)
            warnings.Add($"@keyframes 关键帧总数超过上限 {MaxKeyframes}，超出部分将被忽略。");

        foreach (var rule in sheet.Rules)
        {
            foreach (var declaration in rule.Declarations)
            {
                var property = declaration.Property.Trim().ToLowerInvariant();
                var value = declaration.Value;

                switch (property)
                {
                    case "animation":
                        if (SkinAnimationSpec.TryParse(value, out var animation)
                            && animation.DurationMs > MaxAnimationMs)
                        {
                            warnings.Add($"动画时长超过上限 60s：{value}");
                        }
                        break;

                    case "animation-duration":
                    case "transition-duration":
                        if (SkinCss.TryParseTime(value, out var duration) && duration > MaxAnimationMs)
                            warnings.Add($"{property} 超过上限 60s：{value}");
                        break;

                    case "transition":
                        if (SkinTransitionSpec.TryParse(value, out var transition)
                            && transition.DurationMs > MaxAnimationMs)
                        {
                            warnings.Add($"过渡时长超过上限 60s：{value}");
                        }
                        break;

                    case "transform":
                        if (!SkinTransformSpec.TryParse(value, out _))
                            warnings.Add($"无法解析 transform：{value}");
                        break;
                }
            }
        }
    }

    private static void CollectVariableWarnings(
        Dictionary<string, string>? variables, string label, List<string> warnings)
    {
        if (variables == null || variables.Count == 0) return;

        if (variables.Count > 200)
            warnings.Add($"{label} 变量数量超过上限 200，超出部分将被忽略。");

        foreach (var pair in variables)
        {
            if ((pair.Key ?? string.Empty).Trim().Length > 64)
                warnings.Add($"{label} 中的变量名过长（上限 64 字符）：{pair.Key}");
            if ((pair.Value ?? string.Empty).Length > 200)
                warnings.Add($"{label} 中的变量值过长（上限 200 字符）：{pair.Key}");
        }
    }

    /// <summary>变量表大小 / 键值长度上限校验，避免恶意或超长内容拖慢解析。</summary>
    private static bool HasInvalidVariables(Dictionary<string, string>? variables, string label, out string reason)
    {
        reason = string.Empty;
        if (variables == null || variables.Count == 0) return false;
        if (variables.Count > 200)
        {
            reason = $"{label} 变量数量超过上限 200。";
            return true;
        }

        foreach (var pair in variables)
        {
            if ((pair.Key ?? string.Empty).Trim().Length > 64)
            {
                reason = $"{label} 中的变量名过长（上限 64 字符）。";
                return true;
            }
            if ((pair.Value ?? string.Empty).Length > 200)
            {
                reason = $"{label} 中的变量值过长（上限 200 字符）：{pair.Key}";
                return true;
            }
        }
        return false;
    }

    /// <summary>相对路径安全校验：不允许绝对路径与越界片段。</summary>
    public static bool IsSafeRelativePath(string path)
    {
        var text = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (text.Length == 0) return false;
        if (text.StartsWith('/')) return false;
        if (text.Length > 1 && text[1] == ':') return false;
        return !text.Split('/').Any(part => part == "..");
    }

    private static bool HasInvalidColor(string? hex, out string reason)
    {
        reason = string.Empty;
        var text = (hex ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        if (RgbColor.TryParse(text, out _)) return false;
        reason = "不是有效的十六进制颜色（示例：#FF4C9AFF）。";
        return true;
    }
}
