using System.Text.Encodings.Web;
using System.Text.Json;

namespace DoLLauncher.Core.Diagnostics;

/// <summary>日志等级。区别于 console 的原始标签：这里把「严重警告」与「致命错误」单列出来。</summary>
public enum GameLogLevel
{
    Information = 0,
    Warning = 1,
    WarningSevere = 2,
    Error = 3,
    Fatal = 4,
}

/// <summary>一条报错归纳规则：命中关键字后提升到指定等级，并给出中文原因分析。</summary>
public sealed record GameLogRule(string Keyword, GameLogLevel Level, string Hint);

/// <summary>
/// 游戏日志的等级判定与中文原因分析规则库（平台无关）。启动器侧分析与游戏页悬浮球共用同一套规则，
/// 游戏页通过 <see cref="RulesJson"/> 拿到序列化后的规则。
/// </summary>
public static class GameLogRules
{
    private static readonly GameLogRule[] AllRules =
    {
        // ---- 致命：游戏基本无法继续 ----
        new("out of memory", GameLogLevel.Fatal, "浏览器内存不足：关闭其它标签页，减少大型图片模组后重试。"),
        new("outofmemory", GameLogLevel.Fatal, "浏览器内存不足：关闭其它标签页，减少大型图片模组后重试。"),
        new("maximum call stack", GameLogLevel.Fatal, "出现无限递归（常见于模组互相调用）：先停用最近安装的模组排查。"),
        new("failed to initialize", GameLogLevel.Fatal, "初始化失败：多为游戏本体或 ModLoader 文件不完整，重新下载/校验版本文件。"),
        new("failed to start", GameLogLevel.Fatal, "启动阶段失败：先做「完整性检查」，确认游戏本体与 ModLoader 齐全。"),
        new("cannot allocate", GameLogLevel.Fatal, "内存分配失败：浏览器可用内存不足，关闭其它标签页或减少大图模组。"),
        new("idbfactory.open", GameLogLevel.Fatal, "无法打开浏览器存档库：可能被其它标签页长期占用，关闭其它游戏标签页后重开。"),

        // ---- 严重警告：能启动但会明显影响功能 ----
        new("be banned by", GameLogLevel.WarningSevere,
            "模组冲突屏蔽：前面的模组主动屏蔽了后面的模组（例如 maplebirchFramework 屏蔽 Simple Frameworks），被屏蔽的模组不会加载。需要它就先停用屏蔽方，或在冲突提示里确认忽略。"),
        new("be banned", GameLogLevel.WarningSevere,
            "模组被过滤：ModLoader 按冲突规则跳过了该模组，游戏本身仍能启动；需要这个模组时，先处理对应的模组冲突。"),
        new("missing dependency", GameLogLevel.WarningSevere,
            "缺少前置模组：查看「可能存在问题」里的前置缺失页，安装或启用对应前置。"),
        new("dictionary", GameLogLevel.WarningSevere,
            "存档压缩字典与游戏版本不匹配：用同版本游戏或导出（export）方式迁移存档。"),
        new("decompress", GameLogLevel.WarningSevere, "存档解压失败：存档来自更高版本的游戏，或文件损坏。"),

        // ---- 错误 ----
        new("do_patch", GameLogLevel.Error,
            "TweeReplacer 打补丁失败：模组要替换的原文在当前游戏版本里找不到（或被其它模组改写），相关替换不会生效。更新该模组或先停用它。"),
        new("match failed", GameLogLevel.Warning,
            "补丁匹配失败：模组针对的文本不存在（常见于游戏更新后的旧模组），对应的改动不会生效。"),
        new("cannot find img", GameLogLevel.Warning,
            "图片包缺少这张图：一般不影响运行，可忽略或更新图片包。"),
        new("has duplicate name", GameLogLevel.Warning,
            "ModLoader 缓存里存在同名文件：多个模组使用相同文件名时会这样，通常不影响运行。"),
        new("quotaexceeded", GameLogLevel.Error,
            "浏览器存储空间不足（IndexedDB 配额用完）：清理旧存档/其它网站数据，或增大浏览器可用空间。"),
        new("out-of-line keys", GameLogLevel.Error,
            "存档助手写入了缺少主键的数据：通常是旧版导出文件，用当前版本的存档助手重新导出再导入。"),
        new("cannot read prop", GameLogLevel.Error,
            "脚本读到了 undefined 的属性：多为模组与当前游戏版本不匹配，或前置模组缺失/未启用。试运行「兼容性检查」。"),
        new("is not a function", GameLogLevel.Error,
            "调用了不存在的函数：模组依赖的前置版本不对，或 ModLoader 版本与模组要求不符。"),
        new("not a valid mod", GameLogLevel.Error,
            "zip 不是有效模组，加载时会被跳过；检查模组包是否损坏或包内多套了一层目录。"),
        new("boot.json", GameLogLevel.Error,
            "模组包 boot.json 缺失或无法解析：zip 结构不符或存在尾随逗号，重新下载该模组。"),
        new("failed to fetch", GameLogLevel.Error,
            "浏览器连不上启动器内置服务器：启动器被关闭、端口被占用，或页面是旧标签页。确认启动器仍在运行并重新打开游戏页。"),
        new("err_connection", GameLogLevel.Error,
            "网络连接被拒绝：启动器服务器已停止或端口改变，重新从启动器启动游戏。"),
        new("script error.", GameLogLevel.Error,
            "浏览器隐藏了跨域脚本错误详情：通常来自模组注入的外部脚本，核对是哪个模组后单独排查。"),
        new("unexpected token", GameLogLevel.Error,
            "脚本存在语法错误：常见于自定义模组 JS 的尾随逗号/编码问题。"),
        new("syntaxerror", GameLogLevel.Error, "脚本语法错误：定位对应模组文件，或先停用它验证。"),
        new("indexeddb", GameLogLevel.Error,
            "浏览器存档库读写失败：可能有其它标签页占用同一存档库，关闭其它游戏标签页再试。"),
        new("idbobjectstore", GameLogLevel.Error,
            "存档库操作失败：多为多标签页同时写入或存储被清理。"),
        new("404", GameLogLevel.Error, "资源加载返回 404：图片/脚本缺失，检查图片包与模组文件是否完整。"),
        new("error: ", GameLogLevel.Error, "通用脚本错误，结合日志上下文与下方完整内容判断。"),

        // ---- 警告 ----
        new("deprecated", GameLogLevel.Warning, "使用了已废弃的接口：通常不影响运行，等模组作者更新即可。"),
        new("warning", GameLogLevel.Warning, "游戏或模组给出的警告：若功能正常可以先忽略。"),
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>全部规则，按「越靠前越具体」排列。</summary>
    public static IReadOnlyList<GameLogRule> Rules => AllRules;

    /// <summary>悬浮球使用的规则 JSON：keyword / level(0-4) / hint。</summary>
    public static string RulesJson() => JsonSerializer.Serialize(
        AllRules.Select(r => new { keyword = r.Keyword, level = (int)r.Level, hint = r.Hint }), JsonOpts);

    public static GameLogLevel Max(GameLogLevel a, GameLogLevel b) => a >= b ? a : b;

    /// <summary>console 原始标签对应的等级。</summary>
    public static GameLogLevel LevelOfTag(string? tag) => (tag ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "warn" => GameLogLevel.Warning,
        "error" => GameLogLevel.Error,
        "reject" => GameLogLevel.Error,
        "fatal" => GameLogLevel.Fatal,
        _ => GameLogLevel.Information,
    };

    /// <summary>一句话判定等级（取命中规则里最高的），并返回所有命中的中文原因分析。</summary>
    public static (GameLogLevel Level, IReadOnlyList<string> Hints) Classify(string? text, GameLogLevel baseLevel)
    {
        var level = baseLevel;
        var hints = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return (level, hints);

        foreach (var rule in AllRules)
        {
            if (!text.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase)) continue;
            level = Max(level, rule.Level);
            if (!hints.Contains(rule.Hint)) hints.Add(rule.Hint);
        }

        if (hints.Count == 0 && level >= GameLogLevel.Error)
        {
            hints.Add("没有匹配到已知模式，请对照完整内容与日志前后几行确认触发场景。");
        }
        return (level, hints);
    }

    public static string LevelText(GameLogLevel level) => level switch
    {
        GameLogLevel.Fatal => "Fatal",
        GameLogLevel.Error => "Error",
        GameLogLevel.WarningSevere => "Warning（严重）",
        GameLogLevel.Warning => "Warning",
        _ => "Information",
    };
}
