using System.Text.Json.Serialization;

namespace DoLLauncher.Core.Skin;

/// <summary>
/// dolskin/3 的 <c>components[]</c> 声明式组件：把控件插入到指定宿主元素的插槽里。
/// 支持 <c>events</c> 轻量脚本与 <c>bind</c> 数据绑定，脚本能力由白名单宿主提供。
/// </summary>
public sealed class SkinComponentDefinition
{
    /// <summary>组件标识；同一包内唯一，用于去重。</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>宿主写法：<c>#Name</c>、<c>Type</c> 或 <c>Type.Class</c>。</summary>
    [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;

    /// <summary>插入位置：before / after（宿主父面板的兄弟位置）或 start / end（宿主自身是面板）。</summary>
    [JsonPropertyName("slot")] public string Slot { get; set; } = "end";

    /// <summary>控件类型：Label / Badge / Divider / Spacer / Image / Icon / ProgressBar / Card / Row / Column / Banner。</summary>
    [JsonPropertyName("widget")] public string Widget { get; set; } = "Label";

    /// <summary>皮肤样式类（对应 skin.css 里的 <c>.类名</c>），多个用空格分隔。</summary>
    [JsonPropertyName("class")] public string Class { get; set; } = string.Empty;

    /// <summary>控件属性：text / glyph / value / src 以及通用颜色、尺寸等。</summary>
    [JsonPropertyName("props")] public Dictionary<string, string> Props { get; set; } = new();

    /// <summary>子组件（仅 Card / Row / Column 使用），最多 4 层嵌套。</summary>
    [JsonPropertyName("children")] public List<SkinComponentDefinition> Children { get; set; } = new();

    /// <summary>
    /// 事件脚本：键为事件名（<c>click</c> / <c>changed</c>），值为轻量脚本。
    /// 脚本只能调用白名单动作，无法访问文件、网络或反射。
    /// </summary>
    [JsonPropertyName("events")] public Dictionary<string, string> Events { get; set; } = new();

    /// <summary>
    /// 数据绑定：键为属性名（<c>text</c> / <c>visible</c> / <c>enabled</c> / <c>value</c> /
    /// <c>background</c> / <c>foreground</c>），值为 <c>{{ 表达式 }}</c> 模板。
    /// </summary>
    [JsonPropertyName("bind")] public Dictionary<string, string> Bind { get; set; } = new();
}
