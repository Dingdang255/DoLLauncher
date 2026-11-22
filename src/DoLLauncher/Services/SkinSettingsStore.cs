using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using DoLLauncher.Core.Skin;

namespace DoLLauncher.Services;

/// <summary>单个美化包的持久化设置：主题 id + 设置项键值（统一按字符串保存）。</summary>
public sealed class SkinSettingsState
{
    public string Theme { get; set; } = string.Empty;

    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>窗口背景底图（相对皮肤目录，如 <c>custom/window.png</c>）。</summary>
    public string WindowImage { get; set; } = string.Empty;

    /// <summary>按钮底图（相对皮肤目录）。</summary>
    public string ButtonImage { get; set; } = string.Empty;

    /// <summary>卡片底图（相对皮肤目录）。</summary>
    public string CardImage { get; set; } = string.Empty;

    /// <summary>取设置项的当前值：优先已保存值，缺失时回退声明的默认值。</summary>
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

/// <summary>按皮肤读写 <c>skins/&lt;id&gt;/settings.json</c>；文件损坏或字段缺失时静默回退默认值。</summary>
public static class SkinSettingsStore
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string PathFor(string skinDir) =>
        string.IsNullOrWhiteSpace(skinDir) ? string.Empty : Path.Combine(skinDir, FileName);

    public static SkinSettingsState Load(string skinDir)
    {
        var state = new SkinSettingsState();
        try
        {
            var path = PathFor(skinDir);
            if (path.Length == 0 || !File.Exists(path)) return state;

            var parsed = JsonSerializer.Deserialize<SkinSettingsState>(File.ReadAllText(path), JsonOpts);
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
            return new SkinSettingsState();
        }
    }

    public static bool Save(string skinDir, SkinSettingsState state)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(skinDir) || state == null) return false;
            Directory.CreateDirectory(skinDir);

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in state.Values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                values[pair.Key.Trim()] = pair.Value ?? string.Empty;
            }

            var payload = new SkinSettingsState
            {
                Theme = state.Theme ?? string.Empty,
                Values = values,
                WindowImage = (state.WindowImage ?? string.Empty).Trim(),
                ButtonImage = (state.ButtonImage ?? string.Empty).Trim(),
                CardImage = (state.CardImage ?? string.Empty).Trim(),
            };
            File.WriteAllText(PathFor(skinDir), JsonSerializer.Serialize(payload, JsonOpts));
            return true;
        }
        catch (Exception ex)
        {
            LogService.Append("保存美化包设置失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>删除该皮肤的 settings.json 与导入的底图目录（用于重置 / 恢复出厂）。</summary>
    public static void ResetValues(string skinDir)
    {
        try
        {
            var path = PathFor(skinDir);
            if (path.Length > 0 && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            LogService.Append("删除美化包设置失败：" + ex.Message);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(skinDir)) return;
            var custom = Path.Combine(skinDir, "custom");
            if (Directory.Exists(custom)) Directory.Delete(custom, recursive: true);
        }
        catch (Exception ex)
        {
            LogService.Append("清理美化包底图失败：" + ex.Message);
        }
    }
}
