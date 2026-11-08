using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DoLLauncher.Services;

/// <summary>
/// 轻量 Markdown 渲染：把 README 转成 FlowDocument，支持标题、列表、代码块、引用、
/// 分隔线以及 **粗体**、*斜体*、`代码`、~~删除线~~、[链接](url)。
/// </summary>
public static class MarkdownService
{
    /// <summary>优先用主题画刷（深色主题下自动跟着变），取不到时退回亮色默认值。</summary>
    private static Brush Theme(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private static readonly FontFamily BodyFont = new("Microsoft YaHei UI");
    private static readonly FontFamily CodeFont = new("Consolas");

    private static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedRx = new(@"^\s*(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RuleRx = new(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);

    private static readonly Regex InlineRx = new(
        @"\[(?<ltext>[^\]]+)\]\((?<lurl>[^)\s]+)\)|`(?<ctext>[^`]+)`|\*\*(?<btext>.+?)\*\*|~~(?<stext>.+?)~~|(?<!\*)\*(?<itext>[^*\n]+)\*",
        RegexOptions.Compiled);

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = BodyFont,
            FontSize = 13,
            Foreground = Theme("TextBrush", Color.FromRgb(0x2B, 0x2F, 0x38)),
        };

        if (string.IsNullOrWhiteSpace(markdown)) return doc;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;
                doc.Blocks.Add(CodeBlock(string.Join("\n", code)));
                continue;
            }

            var heading = HeadingRx.Match(trimmed);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var p = new Paragraph { Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 5) };
                p.FontSize = level switch { 1 => 22, 2 => 19, 3 => 17, 4 => 15, _ => 14 };
                p.FontWeight = FontWeights.SemiBold;
                AddInline(p, heading.Groups[2].Value.Trim());
                doc.Blocks.Add(p);
                i++;
                continue;
            }

            if (RuleRx.IsMatch(trimmed))
            {
                doc.Blocks.Add(new Paragraph
                {
                    BorderBrush = Theme("BorderBrush", Color.FromRgb(0xC8, 0xD4, 0xE4)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 8, 0, 10),
                });
                i++;
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                var quoted = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    quoted.Add(lines[i].TrimStart().TrimStart('>').Trim());
                    i++;
                }
                var p = new Paragraph
                {
                    Background = Theme("NavHoverBrush", Color.FromRgb(0xF6, 0xF8, 0xFB)),
                    BorderBrush = Theme("BorderBrush", Color.FromRgb(0xC8, 0xD4, 0xE4)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 5, 8, 5),
                    Margin = new Thickness(0, 4, 0, 8),
                    Foreground = Theme("MutedBrush", Color.FromRgb(0x8A, 0x92, 0x9E)),
                };
                AddInline(p, string.Join(" ", quoted));
                doc.Blocks.Add(p);
                continue;
            }

            var bullet = BulletRx.Match(raw);
            if (bullet.Success)
            {
                var items = new List<string>();
                while (i < lines.Length)
                {
                    var m = BulletRx.Match(lines[i]);
                    if (!m.Success) break;
                    items.Add(m.Groups[1].Value);
                    i++;
                }
                foreach (var item in items)
                {
                    var p = new Paragraph { Margin = new Thickness(20, 2, 0, 2) };
                    p.Inlines.Add(new Run("• "));
                    AddInline(p, item);
                    doc.Blocks.Add(p);
                }
                continue;
            }

            var numbered = NumberedRx.Match(raw);
            if (numbered.Success)
            {
                while (i < lines.Length)
                {
                    var m = NumberedRx.Match(lines[i]);
                    if (!m.Success) break;
                    var p = new Paragraph { Margin = new Thickness(20, 2, 0, 2) };
                    p.Inlines.Add(new Run(m.Groups[1].Value + ". "));
                    AddInline(p, m.Groups[2].Value);
                    doc.Blocks.Add(p);
                    i++;
                }
                continue;
            }

            // 普通段落：把连续的非空、非特殊行合并成一段
            var paragraph = new List<string> { trimmed };
            i++;
            while (i < lines.Length)
            {
                var next = lines[i];
                var t = next.Trim();
                if (t.Length == 0) break;
                if (t.StartsWith("#", StringComparison.Ordinal) ||
                    t.StartsWith("```", StringComparison.Ordinal) ||
                    t.StartsWith(">", StringComparison.Ordinal) ||
                    BulletRx.IsMatch(next) ||
                    NumberedRx.IsMatch(next) ||
                    RuleRx.IsMatch(t))
                    break;
                paragraph.Add(t);
                i++;
            }

            var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInline(para, string.Join(" ", paragraph));
            doc.Blocks.Add(para);
        }

        return doc;
    }

    private static Paragraph CodeBlock(string code)
    {
        var p = new Paragraph
        {
            Background = Theme("CardHoverBrush", Color.FromRgb(0xF3, 0xF5, 0xF9)),
            FontFamily = CodeFont,
            FontSize = 12,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 10),
        };
        p.Inlines.Add(new Run(code.Length > 0 ? code : " "));
        return p;
    }

    private static void AddInline(Paragraph paragraph, string text)
    {
        var last = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > last) paragraph.Inlines.Add(new Run(text[last..m.Index]));
            last = m.Index + m.Length;

            if (m.Groups["ltext"].Success)
            {
                paragraph.Inlines.Add(BuildLink(m.Groups["ltext"].Value, m.Groups["lurl"].Value));
            }
            else if (m.Groups["ctext"].Success)
            {
                paragraph.Inlines.Add(new Run(m.Groups["ctext"].Value)
                {
                    FontFamily = CodeFont,
                    FontSize = 12,
                    Background = Theme("CardHoverBrush", Color.FromRgb(0xF3, 0xF5, 0xF9)),
                });
            }
            else if (m.Groups["btext"].Success)
            {
                paragraph.Inlines.Add(new Run(m.Groups["btext"].Value) { FontWeight = FontWeights.SemiBold });
            }
            else if (m.Groups["stext"].Success)
            {
                paragraph.Inlines.Add(new Run(m.Groups["stext"].Value) { TextDecorations = TextDecorations.Strikethrough });
            }
            else if (m.Groups["itext"].Success)
            {
                paragraph.Inlines.Add(new Run(m.Groups["itext"].Value) { FontStyle = FontStyles.Italic });
            }
        }

        if (last < text.Length) paragraph.Inlines.Add(new Run(text[last..]));
    }

    private static Inline BuildLink(string label, string url)
    {
        var run = new Run(label) { Foreground = Theme("AccentBrush", Color.FromRgb(0x2F, 0x6F, 0xD0)) };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return run;
        }

        var link = new Hyperlink(run)
        {
            NavigateUri = uri,
            ToolTip = uri.AbsoluteUri,
        };
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // 浏览器打不开就算了
            }
        };
        return link;
    }
}
