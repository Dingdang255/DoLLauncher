using System.Text;

namespace DoLLauncher.Core.Skin;

/// <summary>
/// 美化包内的 skin.css：一套精简的 CSS 子集，只做样式声明解析，不执行任何脚本。
/// 支持元素 / #名称 / .样式类 选择器、状态伪类、后代组合、逗号分组、注释、变量、
/// <c>@keyframes</c> 与 <c>@media (prefers-color-scheme: dark|light)</c>。
/// </summary>
public sealed class SkinStyleSheet
{
    /// <summary>全局变量：<c>:root</c> 里的 <c>--xxx</c> 与裸调色板键。</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>@keyframes 动画帧，键为动画名（忽略大小写）。</summary>
    public Dictionary<string, List<SkinKeyframe>> Keyframes { get; } = new(StringComparer.OrdinalIgnoreCase);

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
                if (selectorText.StartsWith("@keyframes", StringComparison.OrdinalIgnoreCase))
                {
                    var name = selectorText["@keyframes".Length..].Trim();
                    if (name.Length == 0)
                    {
                        sheet.Warnings.Add("暂不支持规则：@keyframes 缺少动画名。");
                        continue;
                    }

                    SetKeyframes(sheet, name, ParseKeyframes(body, name, sheet.Warnings));
                    continue;
                }

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
                    foreach (var pair in inner.Keyframes) SetKeyframes(sheet, pair.Key, pair.Value);
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

    /// <summary>解析 <c>@keyframes</c> 块内部的 from / to / 百分比帧。</summary>
    private static List<SkinKeyframe> ParseKeyframes(string body, string name, List<string> warnings)
    {
        var frames = new List<SkinKeyframe>();
        var index = 0;

        while (index < body.Length)
        {
            var brace = body.IndexOf('{', index);
            if (brace < 0) break;

            var offsetText = body[index..brace].Trim();
            var end = FindBlockEnd(body, brace + 1);
            if (end < 0)
            {
                warnings.Add($"@keyframes {name} 缺少右花括号 }}。");
                break;
            }

            var block = body[(brace + 1)..end];
            index = end + 1;
            if (offsetText.Length == 0) continue;

            var declarations = ParseDeclarations(block, warnings);
            if (declarations.Count == 0) continue;

            foreach (var rawOffset in SplitTopLevel(offsetText, ','))
            {
                if (!TryParseKeyframeOffset(rawOffset.Trim(), out var offset))
                {
                    warnings.Add($"@keyframes {name} 的帧偏移无效：{rawOffset.Trim()}");
                    continue;
                }

                var frame = new SkinKeyframe { Offset = offset };
                frame.Declarations.AddRange(declarations);
                frames.Add(frame);
            }
        }

        return frames;
    }

    private static bool TryParseKeyframeOffset(string text, out double offset)
    {
        offset = 0;
        var raw = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0) return false;
        if (raw == "from") return true;
        if (raw == "to")
        {
            offset = 1;
            return true;
        }

        var percent = raw.EndsWith('%');
        if (percent) raw = raw[..^1];
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) return false;

        if (percent || value > 1) value /= 100;
        offset = Math.Clamp(value, 0, 1);
        return true;
    }

    /// <summary>写入一组关键帧并执行总数上限（同名的后定义覆盖先定义）。</summary>
    private static void SetKeyframes(SkinStyleSheet sheet, string name, List<SkinKeyframe> frames)
    {
        if (frames.Count == 0) return;

        var total = sheet.Keyframes
            .Where(pair => !pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Sum(pair => pair.Value.Count);
        var allowed = SkinRules.MaxKeyframes - total;
        if (allowed <= 0)
        {
            sheet.Warnings.Add($"@keyframes 关键帧总数超过上限 {SkinRules.MaxKeyframes}，已忽略：{name}");
            return;
        }

        if (frames.Count > allowed)
        {
            sheet.Warnings.Add($"@keyframes 关键帧总数超过上限 {SkinRules.MaxKeyframes}，已截断：{name}");
            frames = frames.Take(allowed).ToList();
        }

        sheet.Keyframes[name] = frames;
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

/// <summary>单个选择器片段：<c>Type#name.class</c>，可带祖先片段（后代组合）与末尾状态伪类。</summary>
public sealed class SkinSelector
{
    public string Type { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool Universal { get; private set; }
    public List<string> Classes { get; } = new();

    /// <summary>
    /// 末尾状态伪类，已归一化为 <c>hover</c> / <c>pressed</c> / <c>selected</c> /
    /// <c>checked</c> / <c>disabled</c>；<c>:active</c> 等价于 <c>pressed</c>。
    /// </summary>
    public List<string> States { get; } = new();

    public bool HasStates => States.Count > 0;

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

        var lastPart = parts[^1];
        var states = new List<string>();
        var colon = lastPart.IndexOf(':');
        if (colon >= 0)
        {
            if (!TryParsePseudo(lastPart[colon..], states)) return null;
            lastPart = lastPart[..colon];
        }

        var last = ParsePart(lastPart);
        if (last == null)
        {
            // 允许 :disabled 这类纯状态选择器
            if (states.Count == 0) return null;
            last = new SkinSelector();
        }

        last.States.AddRange(states);
        last.Ancestors.AddRange(ancestors);
        return last;
    }

    /// <summary>解析末尾伪类；伪元素（::before 等）返回 false，交给上层告警。</summary>
    private static bool TryParsePseudo(string text, List<string> states)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] != ':') return false;
            if (index + 1 < text.Length && text[index + 1] == ':') return false;
            index++;

            var start = index;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '-')) index++;
            if (index == start) return false;

            var name = text[start..index].ToLowerInvariant();
            if (index < text.Length && text[index] == '(')
            {
                var close = FindClosingParen(text, index);
                if (close < 0) return false;
                index = close + 1;
                continue; // :not(...) 等带参伪类暂不参与匹配
            }

            switch (name)
            {
                case "hover":
                    AddState(states, "hover");
                    break;
                case "pressed":
                case "active":
                    AddState(states, "pressed");
                    break;
                case "selected":
                    AddState(states, "selected");
                    break;
                case "checked":
                    AddState(states, "checked");
                    break;
                case "disabled":
                    AddState(states, "disabled");
                    break;
                // focus 等其它伪类忽略，避免整条规则解析失败
            }
        }

        return true;
    }

    private static void AddState(List<string> states, string state)
    {
        if (!states.Contains(state)) states.Add(state);
    }

    private static int FindClosingParen(string text, int openIndex)
    {
        var depth = 1;
        for (var i = openIndex + 1; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
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

            // + / ~ 兄弟组合暂不支持，交给失败分支提示；伪类保留给末尾解析
            if (c is '+' or '~') return new List<string>();
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
