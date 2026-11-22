using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoLLauncher.Core.Skin;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 单个美化包的持久化设置，字段与 WPF 的 SkinSettingsStore 保持一致，
/// 这样同一个包在两端共用 <c>settings.json</c> 结构（主题 id + 键值）。
/// </summary>
public sealed class AndroidSkinSettingsState
{
    [JsonPropertyName("theme")] public string Theme { get; set; } = string.Empty;

    [JsonPropertyName("values")] public Dictionary<string, string> Values { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("windowImage")] public string WindowImage { get; set; } = string.Empty;

    [JsonPropertyName("buttonImage")] public string ButtonImage { get; set; } = string.Empty;

    [JsonPropertyName("cardImage")] public string CardImage { get; set; } = string.Empty;

    /// <summary>取设置项当前值：优先已保存值，缺失时回退清单声明的默认值。</summary>
    public string ValueOf(SkinSettingDefinition definition)
    {
        var key = (definition.Key ?? string.Empty).Trim();
        if (key.Length > 0 && Values.TryGetValue(key, out var stored) && stored != null) return stored;
        return definition.GetDefaultText();
    }

    public void SetValue(string key, string value)
    {
        var name = (key ?? string.Empty).Trim();
        if (name.Length == 0) return;
        Values[name] = value ?? string.Empty;
        if (name.Equals("theme", StringComparison.OrdinalIgnoreCase)) Theme = Values[name];
    }
}

/// <summary>读写 <c>skins/&lt;folder&gt;/settings.json</c>；文件损坏时静默回退默认值。</summary>
public static class AndroidSkinSettingsStore
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string PathFor(string folder)
    {
        var name = (folder ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;
        return Path.Combine(AndroidSkinStore.SkinsRoot, name, FileName);
    }

    public static AndroidSkinSettingsState Load(string folder)
    {
        var state = new AndroidSkinSettingsState();
        try
        {
            var path = PathFor(folder);
            if (path.Length == 0 || !File.Exists(path)) return state;

            var parsed = JsonSerializer.Deserialize<AndroidSkinSettingsState>(File.ReadAllText(path), JsonOpts);
            if (parsed == null) return state;

            state.Theme = (parsed.Theme ?? string.Empty).Trim();
            state.WindowImage = (parsed.WindowImage ?? string.Empty).Trim();
            state.ButtonImage = (parsed.ButtonImage ?? string.Empty).Trim();
            state.CardImage = (parsed.CardImage ?? string.Empty).Trim();

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (parsed.Values != null)
            {
                foreach (var pair in parsed.Values)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                    values[pair.Key.Trim()] = pair.Value ?? string.Empty;
                }
            }
            state.Values = values;
            return state;
        }
        catch (Exception ex)
        {
            LogService.Append("读取美化包设置失败，已使用默认值：" + ex.Message);
            return new AndroidSkinSettingsState();
        }
    }

    public static bool Save(string folder, AndroidSkinSettingsState state)
    {
        try
        {
            var path = PathFor(folder);
            if (path.Length == 0 || state == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
            return true;
        }
        catch (Exception ex)
        {
            LogService.Append("保存美化包设置失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>写入单个设置项并立刻落盘。</summary>
    public static bool SetValue(string folder, string key, string value)
    {
        if (PathFor(folder).Length == 0) return false;
        var state = Load(folder);
        state.SetValue(key, value);
        return Save(folder, state);
    }
}
