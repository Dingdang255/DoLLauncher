using System.IO;
using System.Text.Json;

namespace DoLLauncher.Core.Story;

/// <summary>分类的中文显示名。</summary>
public static class StoryCategoryNames
{
    /// <summary>把分类枚举转换为中文名（角色 / 地点 / 事件 / 未分类）。</summary>
    public static string ToChinese(StoryCategory category) => category switch
    {
        StoryCategory.Character => "角色",
        StoryCategory.Place => "地点",
        StoryCategory.Event => "事件",
        _ => "未分类",
    };
}

/// <summary>
/// 剧情节点分类规则：精确匹配 → 关键词兜底 → 未分类。
/// 内置一份最小默认表；用户可用 <c>user\story-rules.json</c> 覆盖（由 UI 传入路径）。
/// </summary>
public sealed class StoryRules
{
    private readonly Dictionary<string, (StoryCategory Category, string Group)> _exact;
    private readonly List<(string Match, StoryCategory Category, string Group)> _keyword;

    private StoryRules(
        Dictionary<string, (StoryCategory Category, string Group)> exact,
        List<(string Match, StoryCategory Category, string Group)> keyword)
    {
        _exact = exact;
        _keyword = keyword;
    }

    /// <summary>内置默认规则。</summary>
    public static StoryRules Default { get; } = CreateDefault();

    /// <summary>
    /// 读取用户规则文件；文件不存在时返回内置规则，格式非法时回退内置规则并给出中文提示。
    /// </summary>
    /// <param name="userRulesPath">用户规则文件完整路径，可为 null。</param>
    /// <param name="warning">失败或回退时的中文提示；正常时为 null。</param>
    public static StoryRules Load(string? userRulesPath, out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(userRulesPath) || !File.Exists(userRulesPath)) return Default;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(userRulesPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warning = "剧情分类规则文件格式无效，已使用内置规则。";
                return Default;
            }

            var exact = Default._exact;
            var keyword = Default._keyword;
            if (root.TryGetProperty("exact", out var exactEl) && exactEl.ValueKind == JsonValueKind.Object)
            {
                exact = new Dictionary<string, (StoryCategory, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in exactEl.EnumerateObject())
                {
                    if (TryReadRule(item.Value, out var category, out var group))
                        exact[item.Name] = (category, group);
                }
            }

            if (root.TryGetProperty("keyword", out var keywordEl) && keywordEl.ValueKind == JsonValueKind.Array)
            {
                keyword = new List<(string, StoryCategory, string)>();
                foreach (var item in keywordEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("match", out var matchEl) || matchEl.ValueKind != JsonValueKind.String) continue;
                    var match = matchEl.GetString();
                    if (string.IsNullOrWhiteSpace(match)) continue;
                    if (TryReadRule(item, out var category, out var group))
                        keyword.Add((match!, category, group));
                }
            }

            return new StoryRules(exact, keyword);
        }
        catch (JsonException)
        {
            warning = "剧情分类规则文件不是合法 JSON，已使用内置规则。";
            return Default;
        }
        catch (Exception ex)
        {
            warning = "读取剧情分类规则失败（" + ex.Message + "），已使用内置规则。";
            return Default;
        }
    }

    /// <summary>按标题分类；未命中时返回 false，并给出未分类。</summary>
    /// <param name="title">段落标题。</param>
    /// <param name="category">命中的分类，未命中为 <see cref="StoryCategory.Unknown"/>。</param>
    /// <param name="group">命中的分组名，未命中为空串。</param>
    public bool TryClassify(string title, out StoryCategory category, out string group)
    {
        category = StoryCategory.Unknown;
        group = "";
        if (string.IsNullOrWhiteSpace(title)) return false;

        var text = title.Trim();
        if (_exact.TryGetValue(text, out var exactHit))
        {
            category = exactHit.Category;
            group = exactHit.Group;
            return true;
        }

        foreach (var (match, hitCategory, hitGroup) in _keyword)
        {
            if (text.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                category = hitCategory;
                group = hitGroup;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadRule(JsonElement element, out StoryCategory category, out string group)
    {
        category = StoryCategory.Unknown;
        group = "";
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty("category", out var catEl) || catEl.ValueKind != JsonValueKind.String) return false;
        if (!Enum.TryParse<StoryCategory>(catEl.GetString(), ignoreCase: true, out category)) return false;
        if (!Enum.IsDefined(category)) return false;
        if (element.TryGetProperty("group", out var groupEl) && groupEl.ValueKind == JsonValueKind.String)
            group = groupEl.GetString() ?? "";
        return true;
    }

    private static StoryRules CreateDefault()
    {
        var exact = new Dictionary<string, (StoryCategory, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bedroom"] = (StoryCategory.Place, "卧室"),
            ["School"] = (StoryCategory.Place, "学校"),
            ["Classroom"] = (StoryCategory.Place, "学校"),
            ["Bathroom"] = (StoryCategory.Place, "浴室"),
            ["Park"] = (StoryCategory.Place, "公园"),
            ["Beach"] = (StoryCategory.Place, "海滩"),
            ["Forest"] = (StoryCategory.Place, "森林"),
            ["Orphanage"] = (StoryCategory.Place, "孤儿院"),
            ["Robin"] = (StoryCategory.Character, "Robin"),
            ["Bailey"] = (StoryCategory.Character, "Bailey"),
            ["Eden"] = (StoryCategory.Character, "Eden"),
            ["Avery"] = (StoryCategory.Character, "Avery"),
            ["Kylar"] = (StoryCategory.Character, "Kylar"),
            ["Whitney"] = (StoryCategory.Character, "Whitney"),
        };

        var keyword = new List<(string, StoryCategory, string)>
        {
            ("bedroom", StoryCategory.Place, "卧室"),
            ("room", StoryCategory.Place, "房间"),
            ("school", StoryCategory.Place, "学校"),
            ("class", StoryCategory.Place, "学校"),
            ("street", StoryCategory.Place, "街道"),
            ("alley", StoryCategory.Place, "街道"),
        };

        return new StoryRules(exact, keyword);
    }
}
