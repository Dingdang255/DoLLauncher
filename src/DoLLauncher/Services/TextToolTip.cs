using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DoLLauncher.Services;

/// <summary>
/// 文本实际显示不全（被省略号截断或被父容器裁剪）时，悬停显示完整内容；能完整显示就不弹。
/// 同时覆盖 TextBlock 与 AccessText（CheckBox/RadioButton 等带助记符的内容用的是 AccessText）。
/// </summary>
public static class TextToolTip
{
    /// <summary>悬停多久弹出，0 表示立即。</summary>
    private const int InitialDelayMs = 200;

    /// <summary>行尾禁止出现的开引号/开括号。</summary>
    private const string OpeningMarks = "（(【[《「『“‘〔〖｛{⌈⌊";

    /// <summary>行首禁止出现的收引号/收括号与句读标点。</summary>
    private const string ClosingMarks = "）)】]》」』”’〕〗｝}⌋⌉，。；：？！、…·．.,;:!?";

    /// <summary>可以断行的其他符号（斜杠、运算符等）。</summary>
    private const string BreakSymbols = "／/\\|=+*－-—–~～";

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Hook(typeof(TextBlock));
        Hook(typeof(AccessText));
    }

    private static void Hook(Type type)
    {
        EventManager.RegisterClassHandler(type, UIElement.MouseEnterEvent,
            new MouseEventHandler(OnEnter), true);
        EventManager.RegisterClassHandler(type, UIElement.MouseLeaveEvent,
            new MouseEventHandler(OnLeave), true);
    }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        var text = GetText(element);
        if (string.IsNullOrEmpty(text) || element.ActualWidth <= 0) return;

        // 在进入事件的同一拍就判断并挂提示，提示才会随这次悬停正常弹出
        var clipped = IsClipped(element, text, GetFont(element));
        if (clipped)
        {
            ShowBubble(element, text);
        }
        else
        {
            // 尺寸变大后已能完整显示：撤掉之前挂上去的旧提示
            ClearBubble(element, text);
        }

        // 拖动分栏/窗口缩放后布局可能还没跟上；等布局结束再复核，只用于纠正误判
        element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => Recheck(element, text)));
    }

    private static void Recheck(FrameworkElement element, string text)
    {
        if (!element.IsMouseOver || GetText(element) != text) return;
        if (!IsClipped(element, text, GetFont(element))) ClearBubble(element, text);
    }

    private static void ShowBubble(FrameworkElement element, string text)
    {
        // 已经有同一条提示挂着就不重复赋值，否则会打断已经弹出的提示
        if (element.ToolTip is TextBlock bubble && bubble.Tag as string == text) return;

        ToolTipService.SetInitialShowDelay(element, InitialDelayMs);
        ToolTipService.SetBetweenShowDelay(element, 0);
        ToolTipService.SetShowDuration(element, 20000);
        var tip = BuildBubble(text, element);
        tip.Tag = text;
        element.ToolTip = tip;
    }

    /// <summary>悬停提示按「长约等于宽」排版。</summary>
    private static TextBlock BuildBubble(string text, FrameworkElement source)
    {
        var font = GetFont(source);
        var lineWidth = Math.Max(1, MeasureWidth(text, font));
        var lineHeight = Math.Max(1, MeasureHeight(font));

        // 面积相等的正方形边长，长文本因此大致呈方形而不是一条长条
        var side = Math.Sqrt(lineWidth * lineHeight);
        var screenLimit = SystemParameters.WorkArea.Width * 0.6;
        var maxWidth = Math.Clamp(side, 160, Math.Min(520, screenLimit));

        var lines = Wrap(text, font, maxWidth);
        var widest = lines.Count == 0 ? 0 : lines.Max(line => MeasureWidth(line, font));

        return new TextBlock
        {
            Text = string.Join("\n", lines),
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = Math.Max(widest + 2, 40),
            FontSize = font.Size,
        };
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0xF900 && c <= 0xFAFF) || (c >= 0x3040 && c <= 0x30FF) ||
        (c >= 0xAC00 && c <= 0xD7AF);

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) && !IsCjk(c);

    /// <summary>最小断行单元：文字 + 两侧是否允许断行（配合标点的避头尾规则）。</summary>
    private sealed class Atom
    {
        public Atom(string text, bool glueLeft = false, bool glueRight = false)
        {
            Text = text;
            GlueLeft = glueLeft;
            GlueRight = glueRight;
        }

        public string Text { get; }

        /// <summary>不能在这个单元前面断行（收标点不落行首）。</summary>
        public bool GlueLeft { get; }

        /// <summary>不能在这个单元后面断行（开括号不留在行尾）。</summary>
        public bool GlueRight { get; }
    }

    /// <summary>
    /// 切分断行单元：英文/数字串（含 1.6.0 这类内部点号）整体不拆；
    /// 中文两字一组，尽量不拆开词；开括号只粘后文、收标点只粘前文。
    /// </summary>
    private static List<Atom> Tokenize(string text)
    {
        var atoms = new List<Atom>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                atoms.Add(new Atom(" "));
                i++;
                continue;
            }

            if (IsWordChar(c))
            {
                var start = i;
                i++;
                while (i < text.Length)
                {
                    var n = text[i];
                    if (IsWordChar(n))
                    {
                        i++;
                        continue;
                    }
                    // 词内的点/下划线/连字符（1.6.0、mod_name、foo-bar）不拆开
                    if ((n == '.' || n == '_' || n == '-') && i + 1 < text.Length &&
                        IsWordChar(text[i + 1]))
                    {
                        i += 2;
                        continue;
                    }
                    break;
                }
                atoms.Add(new Atom(text.Substring(start, i - start)));
                continue;
            }

            if (OpeningMarks.IndexOf(c) >= 0)
            {
                atoms.Add(new Atom(c.ToString(), false, true));
                i++;
                continue;
            }

            if (ClosingMarks.IndexOf(c) >= 0 || BreakSymbols.IndexOf(c) >= 0)
            {
                atoms.Add(new Atom(c.ToString(), true, false));
                i++;
                continue;
            }

            var end = i + 1;
            if (end < text.Length && IsCjk(c) && IsCjk(text[end])) end++;
            atoms.Add(new Atom(text.Substring(i, end - i)));
            i = end;
        }
        return atoms;
    }

    private static int CharCount(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c)) count++;
        }
        return count;
    }

    private static string JoinAtoms(List<Atom> atoms) =>
        string.Concat(atoms.Select(atom => atom.Text));

    /// <summary>按行宽贪心排字；遇到回避头尾的位置就整体后移，minChars 为每行最少字数。</summary>
    private static List<List<Atom>> Layout(List<Atom> atoms, FontInfo font,
        double maxWidth, int minChars)
    {
        var rows = new List<List<Atom>>();
        var current = new List<Atom>();
        var builder = new System.Text.StringBuilder();
        var chars = 0;

        void Flush()
        {
            if (current.Count == 0) return;
            rows.Add(current);
            current = new List<Atom>();
            builder.Clear();
            chars = 0;
        }

        foreach (var atom in atoms)
        {
            if (current.Count > 0 && chars >= minChars &&
                MeasureWidth(builder.ToString() + atom.Text, font) > maxWidth)
            {
                // 收标点不能落到行首、开括号不能留在行尾：这两个位置保持不断，宁可本行超一点点
                if (atom.GlueLeft || current[^1].GlueRight)
                {
                    current.Add(atom);
                    builder.Append(atom.Text);
                    chars += CharCount(atom.Text);
                    continue;
                }

                Flush();
                if (atom.Text == " ") continue; // 行首不留空格
            }

            current.Add(atom);
            builder.Append(atom.Text);
            chars += CharCount(atom.Text);
        }

        Flush();
        return rows;
    }

    /// <summary>
    /// 智能断行：先按行宽试排得到行数，再按「总字数 / 行数 / 2.75」向上取整算出每行最少字数，
    /// 行尾过短时从上一行匀字过去，避免出现一两个字的碎片行。
    /// </summary>
    private static List<string> Wrap(string text, FontInfo font, double maxWidth)
    {
        var atoms = Tokenize(text);
        if (atoms.Count == 0) return new List<string> { text };

        var roughRows = Layout(atoms, font, maxWidth, 0);
        var lineCount = Math.Max(1, roughRows.Count);
        var minChars = (int)Math.Ceiling(CharCount(text) / (double)lineCount / 2.75);

        var rows = Layout(atoms, font, maxWidth, minChars);

        while (rows.Count > 1 && CharCount(JoinAtoms(rows[^1])) < minChars)
        {
            var prev = rows[^2];
            var last = rows[^1];
            if (prev.Count == 0 || CharCount(JoinAtoms(prev)) <= minChars) break;

            var moved = prev[^1];
            // 收标点/开括号不适合挪到下一行开头
            if (moved.GlueLeft || moved.GlueRight) break;

            last.Insert(0, moved);
            prev.RemoveAt(prev.Count - 1);
            if (last.Count > 1 && last[0].Text == " ") last.RemoveAt(0);
        }

        return rows
            .Select(row => JoinAtoms(row).Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static double MeasureWidth(string text, FontInfo font)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(font.Family, font.Style, font.Weight, font.Stretch),
            font.Size, Brushes.Black, 1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static double MeasureHeight(FontInfo font)
    {
        var formatted = new FormattedText("国Ag", CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(font.Family, font.Style, font.Weight, font.Stretch),
            font.Size, Brushes.Black, 1.0);
        return formatted.Height;
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        ClearBubble(element, GetText(element));
    }

    /// <summary>只清掉自己挂上去的完整文本提示，父级（例如整行）的固定提示保持不变。</summary>
    private static void ClearBubble(FrameworkElement element, string text)
    {
        if (element.ToolTip is TextBlock bubble && bubble.Tag as string == text) element.ToolTip = null;
    }

    private static string GetText(FrameworkElement element) => element switch
    {
        TextBlock block => block.Text,
        AccessText access => access.Text,
        _ => string.Empty,
    };

    private sealed record FontInfo(FontFamily Family, FontStyle Style, FontWeight Weight,
        FontStretch Stretch, double Size, Thickness Padding, TextTrimming Trimming,
        TextWrapping Wrapping);

    private static FontInfo GetFont(FrameworkElement element)
    {
        if (element is TextBlock block)
        {
            return new FontInfo(block.FontFamily, block.FontStyle, block.FontWeight,
                block.FontStretch, block.FontSize, block.Padding, block.TextTrimming,
                block.TextWrapping);
        }

        // AccessText 的字体信息来自 TextElement 附加属性（可继承）
        var access = (AccessText)element;
        return new FontInfo(TextElement.GetFontFamily(element), TextElement.GetFontStyle(element),
            TextElement.GetFontWeight(element), TextElement.GetFontStretch(element),
            TextElement.GetFontSize(element), new Thickness(0), access.TextTrimming,
            access.TextWrapping);
    }

    /// <summary>
    /// 按实际排版判断是否显示不全。自动换行的文本会折成多行，
    /// 只能比较「折行后的总高度」与控件高度；拿整段单行宽度去比较会把
    /// 自带换行、其实完整显示的文本误判成截断。
    /// </summary>
    private static bool IsClipped(FrameworkElement element, string text, FontInfo font)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(element).PixelsPerDip;
            var typeface = new Typeface(font.Family, font.Style, font.Weight, font.Stretch);

            var contentWidth = element.ActualWidth - font.Padding.Left - font.Padding.Right;
            var contentHeight = element.ActualHeight - font.Padding.Top - font.Padding.Bottom;
            if (contentWidth <= 0 || contentHeight <= 0) return false;

            // MaxTextWidth 赋 PositiveInfinity 会抛参数异常，只有有限宽度才设置
            FormattedText Measure(double maxWidth)
            {
                var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
                    element.FlowDirection, typeface, font.Size, Brushes.Black, dpi);
                if (!double.IsInfinity(maxWidth)) formatted.MaxTextWidth = maxWidth;
                return formatted;
            }

            var wrapping = font.Wrapping != TextWrapping.NoWrap;
            double textWidth = 0;

            if (wrapping)
            {
                // 换行文本：按可用宽度重新排版。截断会整行消失，因此短缺接近一行才算；
                // 排版测量与控件在 Display/Ideal 模式下的行高有零点几行误差，留半行余量。
                var lineHeight = Math.Max(1, MeasureHeight(font));
                if (Measure(contentWidth).Height > contentHeight + lineHeight * 0.6) return true;
            }
            else
            {
                textWidth = Measure(double.PositiveInfinity).WidthIncludingTrailingWhitespace;

                // 省略号截断：控件本身已被压窄
                if (font.Trimming != TextTrimming.None && textWidth > contentWidth + 1) return true;
            }

            var root = FindRoot(element);
            if (root == null) return false;

            var slot = LayoutInformation.GetLayoutSlot(element);
            var rect = new Rect(element.TransformToAncestor(root).Transform(new Point(0, 0)),
                new Size(Math.Min(element.ActualWidth, slot.Width), element.ActualHeight));

            // 与所有祖先的实际边框求交：文字只要伸出了自身容器（会被相邻面板盖住或裁掉）就算显示不全
            for (var parent = VisualTreeHelper.GetParent(element);
                 parent != null;
                 parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not UIElement ancestor) continue;
                if (ancestor.RenderSize.Width <= 0 || ancestor.RenderSize.Height <= 0) continue;

                var bounds = new Rect(ancestor.TransformToAncestor(root).Transform(new Point(0, 0)),
                    ancestor.RenderSize);
                rect.Intersect(bounds);
                if (rect.IsEmpty) return true;
                if (ReferenceEquals(parent, root)) break;
            }

            if (wrapping)
            {
                // 换行文本只要控件本身没被祖先裁掉就说明每一行都完整可见
                return rect.Width + 1 < element.ActualWidth || rect.Height + 1 < element.ActualHeight;
            }

            return rect.Width + 1 < textWidth;
        }
        catch
        {
            return false;
        }
    }

    private static Visual? FindRoot(DependencyObject source)
    {
        var current = source;
        while (true)
        {
            var parent = VisualTreeHelper.GetParent(current);
            if (parent == null) return current as Visual;
            current = parent;
        }
    }
}
