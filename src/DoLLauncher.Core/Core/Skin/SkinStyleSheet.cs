using System.Text;

namespace DoLLauncher.Core.Skin;

/// <summary>
/// 美化包内的 skin.css：一套精简的 CSS 子集，只做样式声明解析，不执行任何脚本。
/// 支持元素 / #名称 / .样式类 选择器、后代组合、逗号分组、注释、变量与
/// <c>@media (prefers-color-scheme: dark|light)</c>。
/// </summary>
public sealed class SkinStyleSheet
{
    /// <summary>全局变量：<c>:root</c> 里的 <c>--xxx</c> 与裸调色板键。</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>@media (prefers-color-scheme: dark) 内的变量。</summary>
    public Dictionary<string, string> DarkVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>@media (prefers-color-scheme: light) 内的变量。</summary>
    public Dictionary<string, string> LightVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按当前深浅模式合并变量，供样式解析与调色板覆盖使用。</summary>
    public Dictionary<string, string> VariablesFor(bool dark)
    {
        var merged = new Dictionary<string, string>(Variables, StringComparer.OrdinalIgnoreCase);
        var themeVariables = dark ? DarkVariables : LightVariables;
        foreach (var pair in themeVariables) merged[pair.Key] = pair.Value;
        return merged;
    }

    /// <summary>普通样式规则，按出现顺序生效（后出现的覆盖先出现的）。</summary>
    public List<SkinStyleRule> Rules { get; } = new();

    /// <summary>解析过程中的告警，用于写入日志与状态提示。</summary>
    public List<string> Warnings { get; } = new();

    public static SkinStyleSheet Parse(string? text) => Parse(text, null, 0);

    private static SkinStyleSheet Parse(string? text, bool? requiresDark, int depth)
    {
        var sheet = new SkinStyleSheet();
        var source = StripComments(text ?? string.Empty);
        var index = 0;

        while (index < source.Length)
        {
            var brace = source.IndexOf('{', index);
            if (brace < 0) break;

            var selectorText = source.Substring(index, brace - index).Trim();
            var end = FindBlockEnd(source, brace + 1);
            if (end < 0)
            {
                sheet.Warnings.Add("样式块缺少右花括号 }。");
                break;
            }

            var body = source.Substring(brace + 1, end - brace - 1);
            index = end + 1;
            if (selectorText.Length == 0) continue;

            if (selectorText.StartsWith('@'))
            {
                if (selectorText.StartsWith("@media", StringComparison.OrdinalIgnoreCase) && depth < 2)
                {
                    var dark = ParseMediaCondition(selectorText);
                    if (dark == null)
                    {
                        sheet.Warnings.Add($"暂不支持媒体查询：{selectorText}");
                        continue;
                    }

                    var inner = Parse(body, dark, depth + 1);
                    foreach (var pair in inner.Variables) sheet.Variables[pair.Key] = pair.Value;
                    foreach (var pair in inner.DarkVariables) sheet.DarkVariables[pair.Key] = pair.Value;
                    foreach (var pair in inner.LightVariables) sheet.LightVariables[pair.Key] = pair.Value;
                    sheet.Rules.AddRange(inner.Rules);
                    sheet.Warnings.AddRange(inner.Warnings);
                }
                else
                {
                    sheet.Warnings.Add($"暂不支持规则：{selectorText}");
                }
                continue;
            }

            var declarations = ParseDeclarations(body, sheet.Warnings);
            if (declarations.Count == 0) continue;

            // :root / html / body 里的声明统一当作全局变量（变量或调色板键）
            if (selectorText.Equals(":root", StringComparison.OrdinalIgnoreCase)
                || selectorText.Equals("html", StringComparison.OrdinalIgnoreCase)
                || selectorText.Equals("body", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var declaration in declarations)
                {
                    var name = declaration.Property.Trim();
                    if (name.StartsWith("--", StringComparison.Ordinal)) name = name[2..];
                    name = name.Trim();
                    if (name.Length == 0) continue;

                    var target = requiresDark == true
                        ? sheet.DarkVariables
                        : requiresDark == false
                            ? sheet.LightVariables
                            : sheet.Variables;
                    target[name] = declaration.Value;
                }
                continue;
            }

            foreach (var rawSelector in SplitTopLevel(selectorText, ','))
            {
                var selector = SkinSelector.TryParse(rawSelector.Trim());
                if (selector == null)
                {
                    sheet.Warnings.Add($"无法解析选择器：{rawSelector.Trim()}");
                    continue;
                }

                var rule = new SkinStyleRule
                {
                    Source = rawSelector.Trim(),
                    RequiresDark = requiresDark,
                };
                rule.Selectors.Add(selector);
                rule.Declarations.AddRange(declarations);
                sheet.Rules.Add(rule);
            }
        }

        return sheet;
    }

    private static bool? ParseMediaCondition(string selectorText)
    {
        if (!selectorText.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase)) return null;
        if (selectorText.Contains("dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (selectorText.Contains("light", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static int FindBlockEnd(string source, int start)
    {
        var depth = 1;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static string StripComments(string text)
    {
        if (!text.Contains("/*", StringComparison.Ordinal)) return text;

        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }
            builder.Append(text[i]);
            i++;
        }
        return builder.ToString();
    }

    private static List<SkinDeclaration> ParseDeclarations(string body, List<string> warnings)
    {
        var list = new List<SkinDeclaration>();
        foreach (var chunk in SplitTopLevel(body, ';'))
        {
            var text = chunk.Trim();
            if (text.Length == 0) continue;

            var colon = IndexOfTopLevel(text, ':');
            if (colon <= 0)
            {
                warnings.Add($"无法解析声明：{text}");
                continue;
            }

            var property = text[..colon].Trim();
            var value = text[(colon + 1)..].Trim();
            // 本地值本身优先级最高，!important 只是语法兼容（"!important" 共 10 个字符）
            if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
                value = value[..^10].TrimEnd();

            if (property.Length == 0 || value.Length == 0) continue;
            list.Add(new SkinDeclaration(property, value));
        }
        return list;
    }

    private static int IndexOfTopLevel(string text, char target)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    continue;
                case '(':
                case '[':
                    depth++;
                    continue;
                case ')':
                case ']':
                    if (depth > 0) depth--;
                    continue;
            }

            if (depth == 0 && c == target) return i;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        char quote = '\0';
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    continue;
                case '(':
                case '[':
                    depth++;
                    continue;
                case ')':
                case ']':
                    if (depth > 0) depth--;
                    continue;
            }

            if (depth == 0 && c == separator)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }
}

/// <summary>一条 CSS 规则：若干选择器 + 若干声明。</summary>
public sealed class SkinStyleRule
{
    /// <summary>原始选择器文本，仅用于日志。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>只在该主题模式下生效；null 表示不限制。</summary>
    public bool? RequiresDark { get; init; }

    public List<SkinSelector> Selectors { get; } = new();

    public List<SkinDeclaration> Declarations { get; } = new();
}

public readonly record struct SkinDeclaration(string Property, string Value);

/// <summary>单个选择器片段：<c>Type#name.class</c>，可带祖先片段（后代组合）。</summary>
public sealed class SkinSelector
{
    public string Type { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool Universal { get; private set; }
    public List<string> Classes { get; } = new();

    /// <summary>祖先片段，从远到近排列。</summary>
    public List<SkinSelector> Ancestors { get; } = new();

    public static SkinSelector? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = SplitDescendant(raw);
        if (parts.Count == 0) return null;

        var ancestors = new List<SkinSelector>();
        for (var i = 0; i < parts.Count - 1; i++)
        {
            var ancestor = ParsePart(parts[i]);
            if (ancestor == null) return null;
            ancestors.Add(ancestor);
        }

        var last = ParsePart(parts[^1]);
        if (last == null) return null;
        last.Ancestors.AddRange(ancestors);
        return last;
    }

    private static List<string> SplitDescendant(string raw)
    {
        var parts = new List<string>();
        var builder = new StringBuilder();

        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c) || c == '>')
            {
                if (builder.Length > 0)
                {
                    parts.Add(builder.ToString());
                    builder.Clear();
                }
                continue;
            }

            // + / ~ 兄弟组合与伪类暂不支持，交给失败分支提示
            if (c is '+' or '~' or ':') return new List<string>();
            builder.Append(c);
        }

        if (builder.Length > 0) parts.Add(builder.ToString());
        return parts;
    }

    private static SkinSelector? ParsePart(string part)
    {
        if (part.Length == 0) return null;

        var selector = new SkinSelector();
        var type = new StringBuilder();
        var index = 0;

        while (index < part.Length)
        {
            var c = part[index];
            if (c == '#')
            {
                index++;
                var name = ReadIdentifier(part, ref index);
                if (name.Length == 0 || selector.Name.Length > 0) return null;
                selector.Name = name;
                continue;
            }

            if (c == '.')
            {
                index++;
                var name = ReadIdentifier(part, ref index);
                if (name.Length == 0) return null;
                selector.Classes.Add(name);
                continue;
            }

            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '*')
            {
                type.Append(c);
                index++;
                continue;
            }

            return null;
        }

        var typeText = type.ToString();
        if (typeText == "*") selector.Universal = true;
        else selector.Type = typeText;

        if (!selector.Universal && selector.Type.Length == 0 && selector.Name.Length == 0 && selector.Classes.Count == 0)
            return null;

        return selector;
    }

    private static string ReadIdentifier(string text, ref int index)
    {
        var start = index;
        while (index < text.Length)
        {
            var c = text[index];
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') break;
            index++;
        }
        return text[start..index];
    }
}
