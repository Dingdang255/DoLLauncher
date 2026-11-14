namespace DoLLauncher.Core.Ui;

/// <summary>左上角副标题的取值规则。</summary>
public static class SubtitleText
{
    /// <summary>留空时使用的原始副标题。</summary>
    public const string Default = "版本 · 模组 · 存档";

    /// <summary>空 → 返回默认文案；非空 → 返回去空白后的文本。</summary>
    public static string Resolve(string? custom)
    {
        var text = (custom ?? string.Empty).Trim();
        return text.Length == 0 ? Default : text;
    }
}
