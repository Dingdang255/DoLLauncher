using System.Text.RegularExpressions;

namespace DoLLauncher.Maui.Controls;

/// <summary>
/// 轻量 Markdown 阅读控件：把标题、列表、代码块、引用、分隔线以及
/// **粗体**、*斜体*、`代码`、~~删除线~~、[链接](url) 渲染成一列原生控件。
/// 与 WPF 端 Services/MarkdownService.cs 的支持范围保持一致。
/// </summary>
public sealed class MarkdownView : ContentView
{
    private static readonly Color TextLight = Color.FromArgb("#2B2F38");
    private static readonly Color TextDark = Color.FromArgb("#E8E8E8");
    private static readonly Color MutedLight = Color.FromArgb("#8A929E");
    private static readonly Color MutedDark = Color.FromArgb("#A0A0A0");
    private static readonly Color CardLight = Color.FromArgb("#F3F5F9");
    private static readonly Color CardDark = Color.FromArgb("#232323");
    private static readonly Color BorderLight = Color.FromArgb("#DCDCDC");
    private static readonly Color BorderDark = Color.FromArgb("#3C3C3C");

    private static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedRx = new(@"^(\s*)(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RuleRx = new(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);

    private static readonly Regex InlineRx = new(
        @"\[(?<ltext>[^\]]+)\]\((?<lurl>[^)\s]+)\)|`(?<ctext>[^`]+)`|\*\*(?<btext>.+?)\*\*|~~(?<stext>.+?)~~|(?<!\*)\*(?<itext>[^*\n]+)\*",
        RegexOptions.Compiled);

    private readonly VerticalStackLayout _stack;
    private bool _dirty;

    public static readonly BindableProperty MarkdownProperty = BindableProperty.Create(
        nameof(Markdown), typeof(string), typeof(MarkdownView), string.Empty,
        propertyChanged: (b, _, _) => ((MarkdownView)b).OnMarkdownChanged());

    public MarkdownView()
    {
        _stack = new VerticalStackLayout { Spacing = 0 };
        Content = _stack;
        // 隐藏时不解析（下载列表里大量折叠的更新日志），显示时再补渲染。
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsVisible) && IsVisible && _dirty) Render();
        };
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void OnMarkdownChanged()
    {
        _dirty = true;
        if (IsVisible) Render();
    }

    /// <summary>重新构建内容，页面拿到新数据后可主动调用。</summary>
    public void Render()
    {
        _dirty = false;
        _stack.Children.Clear();
        var markdown = Markdown;
        if (string.IsNullOrWhiteSpace(markdown)) return;

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
                _stack.Children.Add(CodeBlock(string.Join("\n", code)));
                continue;
            }

            var heading = HeadingRx.Match(trimmed);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var headingLabel = NewLabel(level switch { 1 => 21, 2 => 18, 3 => 16, 4 => 15, _ => 14 });
                headingLabel.FontAttributes = FontAttributes.Bold;
                headingLabel.Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 5);
                headingLabel.FormattedText = Inline(heading.Groups[2].Value.Trim());
                _stack.Children.Add(headingLabel);
                i++;
                continue;
            }

            if (RuleRx.IsMatch(trimmed))
            {
                var rule = new BoxView { HeightRequest = 1, Margin = new Thickness(0, 8, 0, 10) };
                rule.SetAppThemeColor(BoxView.ColorProperty, BorderLight, BorderDark);
                _stack.Children.Add(rule);
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
                _stack.Children.Add(Quote(string.Join(" ", quoted)));
                continue;
            }

            var bullet = BulletRx.Match(raw);
            if (bullet.Success)
            {
                while (i < lines.Length)
                {
                    var m = BulletRx.Match(lines[i]);
                    if (!m.Success) break;
                    _stack.Children.Add(ListItem(m.Groups[2].Value,
                        Indent(m.Groups[1].Value.Length) + "• "));
                    i++;
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
                    _stack.Children.Add(ListItem(m.Groups[3].Value,
                        Indent(m.Groups[1].Value.Length) + m.Groups[2].Value + ". "));
                    i++;
                }
                continue;
            }

            // 普通段落：连续的非空、非特殊行合并成一段
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

            var label = NewLabel(13);
            label.Margin = new Thickness(0, 0, 0, 8);
            label.FormattedText = Inline(string.Join(" ", paragraph));
            _stack.Children.Add(label);
        }
    }

    /// <summary>列表缩进：每级两个空格按 12 递增。</summary>
    private static string Indent(int spaces) => new(' ', Math.Min(spaces / 2, 3) * 3);

    private Label NewLabel(double fontSize)
    {
        var label = new Label
        {
            FontSize = fontSize,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        label.SetAppThemeColor(Label.TextColorProperty, TextLight, TextDark);
        return label;
    }

    private Label ListItem(string text, string prefix)
    {
        var label = NewLabel(13);
        label.Margin = new Thickness(14, 2, 0, 2);
        var formatted = Inline(text);
        var first = new Span { Text = prefix };
        formatted.Spans.Insert(0, first);
        label.FormattedText = formatted;
        return label;
    }

    private View Quote(string text)
    {
        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(0),
        };

        var bar = new BoxView { WidthRequest = 3, VerticalOptions = LayoutOptions.Fill };
        bar.SetAppThemeColor(BoxView.ColorProperty, BorderLight, BorderDark);
        grid.Add(bar, 0);

        var label = NewLabel(12);
        label.Margin = new Thickness(9, 4, 6, 4);
        label.SetAppThemeColor(Label.TextColorProperty, MutedLight, MutedDark);
        label.FormattedText = Inline(text);
        grid.Add(label, 1);
        return grid;
    }

    private View CodeBlock(string code)
    {
        var label = new Label
        {
            Text = code.Length > 0 ? code : " ",
            FontFamily = "monospace",
            FontSize = 11,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        label.SetAppThemeColor(Label.TextColorProperty, TextLight, TextDark);

        var border = new Border
        {
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 4, 0, 10),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = label,
        };
        border.SetAppThemeColor(Border.BackgroundColorProperty, CardLight, CardDark);
        return border;
    }

    /// <summary>把一行文本拆成带样式的 Span；链接可点击。</summary>
    private FormattedString Inline(string text)
    {
        var formatted = new FormattedString();
        var last = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > last) formatted.Spans.Add(new Span { Text = text[last..m.Index] });
            last = m.Index + m.Length;

            if (m.Groups["ltext"].Success)
            {
                var link = new Span { Text = m.Groups["ltext"].Value, TextDecorations = TextDecorations.Underline };
                link.SetDynamicResource(Span.TextColorProperty, "AccentColor");
                var url = m.Groups["lurl"].Value;
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    var tap = new TapGestureRecognizer();
                    tap.Tapped += async (_, _) =>
                    {
                        try { await Launcher.Default.OpenAsync(uri); }
                        catch { /* 浏览器打不开就算了 */ }
                    };
                    link.GestureRecognizers.Add(tap);
                }
                formatted.Spans.Add(link);
            }
            else if (m.Groups["ctext"].Success)
            {
                var span = new Span { Text = m.Groups["ctext"].Value, FontFamily = "monospace", FontSize = 12 };
                span.SetDynamicResource(Span.TextColorProperty, "AccentColor");
                formatted.Spans.Add(span);
            }
            else if (m.Groups["btext"].Success)
            {
                formatted.Spans.Add(new Span { Text = m.Groups["btext"].Value, FontAttributes = FontAttributes.Bold });
            }
            else if (m.Groups["stext"].Success)
            {
                formatted.Spans.Add(new Span { Text = m.Groups["stext"].Value, TextDecorations = TextDecorations.Strikethrough });
            }
            else if (m.Groups["itext"].Success)
            {
                formatted.Spans.Add(new Span { Text = m.Groups["itext"].Value, FontAttributes = FontAttributes.Italic });
            }
        }

        if (last < text.Length) formatted.Spans.Add(new Span { Text = text[last..] });
        return formatted;
    }
}
