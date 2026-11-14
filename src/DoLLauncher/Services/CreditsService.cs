using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DoLLauncher.Services;

/// <summary>致谢名单里的一条：职位、头像、名称与 Bilibili / GitHub 链接。</summary>
public sealed class CreditEntry
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>头像：本地文件路径或 http(s) 地址；留空时显示名字首字。</summary>
    [JsonPropertyName("avatar")] public string Avatar { get; set; } = "";
    [JsonPropertyName("bilibili")] public string Bilibili { get; set; } = "";
    [JsonPropertyName("github")] public string Github { get; set; } = "";
    /// <summary>其它官方页面（如 Wiki）；没有官方 GitHub 时用它。</summary>
    [JsonPropertyName("website")] public string Website { get; set; } = "";
}

public sealed class CreditsFile
{
    [JsonPropertyName("projectUrl")] public string ProjectUrl { get; set; } = "";
    [JsonPropertyName("entries")] public List<CreditEntry> Entries { get; set; } = new();
}

/// <summary>
/// 读取致谢名单：优先使用构建时嵌入 exe 的 credits.json（源码根目录），
/// 找不到时退回启动器根目录的同名文件，最后使用内置占位名单。
/// 改名单只要编辑源码里的 credits.json 再重新构建即可。
/// </summary>
public static class CreditsService
{
    private const string ResourceName = "DoLLauncher.credits.json";

    public static string FilePath => Path.Combine(Paths.Root, "credits.json");

    public static CreditsFile Load()
    {
        try
        {
            var embedded = LoadEmbedded();
            if (embedded != null) return embedded;
        }
        catch (Exception ex)
        {
            LogService.Append("读取内置 credits.json 失败：" + ex.Message);
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var file = JsonSerializer.Deserialize<CreditsFile>(File.ReadAllText(FilePath));
                if (Normalize(file) is { } normalized) return normalized;
            }
        }
        catch (Exception ex)
        {
            LogService.Append("读取 credits.json 失败：" + ex.Message);
        }
        return Default;
    }

    private static CreditsFile? LoadEmbedded()
    {
        using var stream = typeof(CreditsService).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return Normalize(JsonSerializer.Deserialize<CreditsFile>(reader.ReadToEnd()));
    }

    private static CreditsFile? Normalize(CreditsFile? file)
    {
        if (file == null) return null;
        file.Entries ??= new List<CreditEntry>();
        return file.Entries.Count > 0 ? file : null;
    }

    /// <summary>名单与链接全部来自 credits.json，代码里不再保留任何硬编码网址。</summary>
    private static CreditsFile Default => new();
}
