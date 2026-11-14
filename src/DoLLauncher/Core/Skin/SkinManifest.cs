using System.Text.Json.Serialization;
using DoLLauncher.Core.Theme;

namespace DoLLauncher.Core.Skin;

/// <summary>
/// 本地 UI 美化包（zip）内的 skin.json。
/// 只能覆盖窗口背景、弹窗背景、主题色与背景图片，不执行任何脚本。
/// </summary>
public sealed class SkinManifest
{
    public const string CurrentFormat = "dolskin/1";

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
}

/// <summary>美化包格式校验规则（平台无关）。</summary>
public static class SkinRules
{
    /// <summary>校验清单内容与相对资源路径；失败时返回中文原因。</summary>
    public static (bool Ok, string Reason) Validate(SkinManifest? manifest)
    {
        if (manifest == null) return (false, "美化包里没有可读取的 skin.json。");

        var format = (manifest.Format ?? string.Empty).Trim();
        if (!format.Equals(SkinManifest.CurrentFormat, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"不支持的美化包格式：{format}。当前仅支持 {SkinManifest.CurrentFormat}。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name)) return (false, "skin.json 缺少 name 字段。");

        if (HasInvalidColor(manifest.AccentColor, out var accentError)) return (false, "accentColor " + accentError);
        if (HasInvalidColor(manifest.WindowBackgroundColor, out var windowError)) return (false, "windowBackgroundColor " + windowError);
        if (HasInvalidColor(manifest.DialogBackgroundColor, out var dialogError)) return (false, "dialogBackgroundColor " + dialogError);

        foreach (var (path, label) in new[]
                 {
                     (manifest.WindowBackgroundImage, "windowBackgroundImage"),
                     (manifest.DialogBackgroundImage, "dialogBackgroundImage"),
                 })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!IsSafeRelativePath(path!)) return (false, $"{label} 必须是包内的相对路径，且不能包含 ..。");
        }

        return (true, string.Empty);
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
