using System.Text.Json.Serialization;
using DoLLauncher.Core.Theme;

namespace DoLLauncher.Core.Skin;

/// <summary>
/// 本地 UI 美化包（zip）内的 skin.json。
/// 可覆盖窗口背景、弹窗背景、主题色、CSS 样式表与图片，不执行任何脚本。
/// </summary>
public sealed class SkinManifest
{
    public const string CurrentFormat = "dolskin/1";

    /// <summary>兼容的历史格式；新增字段均为可选，旧包可以继续使用。</summary>
    public static readonly string[] SupportedFormats = { "dolskin/1", "dolskin/2" };

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
