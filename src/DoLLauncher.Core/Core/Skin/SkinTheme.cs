using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoLLauncher.Core.Skin;

/// <summary>dolskin/3 的可切换主题：调色板变量 + 可选背景纹理与边饰。</summary>
public sealed class SkinTheme
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("variables")] public Dictionary<string, string> Variables { get; set; } = new();

    /// <summary>背景纹理，包内相对路径。</summary>
    [JsonPropertyName("texture")] public string Texture { get; set; } = string.Empty;

    /// <summary>九宫格边饰 PNG，包内相对路径。</summary>
    [JsonPropertyName("ornament")] public string Ornament { get; set; } = string.Empty;

    /// <summary>九宫格切片尺寸（像素）。</summary>
    [JsonPropertyName("slice")] public int Slice { get; set; } = 24;
}

/// <summary>dolskin/3 的 <c>effects</c>：环境层与动效开关。</summary>
public sealed class SkinEffectOptions
{
    [JsonPropertyName("ambient")] public SkinAmbientOptions Ambient { get; set; } = new();
    [JsonPropertyName("motion")] public SkinMotionOptions Motion { get; set; } = new();
}

/// <summary>环境层：粒子 / 漂移光带 / 渐变天空。</summary>
public sealed class SkinAmbientOptions
{
    [JsonPropertyName("particles")] public int Particles { get; set; } = 4;
    [JsonPropertyName("veil")] public bool Veil { get; set; }
    [JsonPropertyName("sky")] public bool Sky { get; set; }

    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.6;
}

/// <summary>动效开关；默认全部开启。</summary>
public sealed class SkinMotionOptions
{
    [JsonPropertyName("panelIn")] public bool PanelIn { get; set; } = true;
    [JsonPropertyName("breathe")] public bool Breathe { get; set; } = true;
    [JsonPropertyName("hairline")] public bool Hairline { get; set; } = true;
    [JsonPropertyName("ripple")] public bool Ripple { get; set; } = true;
}

/// <summary>dolskin/3 的 <c>settings[]</c>：美化包设置页里的一项控件声明。</summary>
public sealed class SkinSettingDefinition
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

    /// <summary>toggle / select / slider / color。</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "toggle";

    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;

    /// <summary>默认值，保留 JSON 原始类型（bool / number / string）。</summary>
    [JsonPropertyName("default")] public JsonElement? Default { get; set; }

    [JsonPropertyName("min")] public double? Min { get; set; }
    [JsonPropertyName("max")] public double? Max { get; set; }
    [JsonPropertyName("step")] public double? Step { get; set; }

    [JsonPropertyName("options")] public List<string> Options { get; set; } = new();

    /// <summary>设置项作用目标（可选，由平台层解释）。</summary>
    [JsonPropertyName("target")] public string Target { get; set; } = string.Empty;

    /// <summary>select 的选项直接取主题 id 列表。</summary>
    [JsonPropertyName("optionsFromThemes")] public bool OptionsFromThemes { get; set; }

    public string GetDefaultText()
    {
        if (Default is not { } value) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText(),
        };
    }

    public bool GetDefaultBool(bool fallback = false)
    {
        if (Default is not { } value) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => bool.TryParse(GetDefaultText(), out var parsed) ? parsed : fallback,
        };
    }

    public double GetDefaultDouble(double fallback = 0)
    {
        if (Default is not { } value) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return double.TryParse(GetDefaultText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
