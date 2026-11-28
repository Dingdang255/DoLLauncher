using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace DoLLauncher.Maui;

/// <summary>
/// 长文本独立查看界面：带原生标题栏与返回按钮（MAUI 端按「界面」= Push 的原生页，
/// 「页面」= 主界面右侧内嵌面板区分），正文等宽字体、可横竖滚动，并提供复制 / 分享。
/// </summary>
public sealed class TextViewerPage : ContentPage
{
    private readonly string _text;

    public TextViewerPage(string title, string text, string? subtitle = null)
    {
        _text = text ?? string.Empty;
        Title = string.IsNullOrWhiteSpace(title) ? "查看内容" : title;

        var body = new Label
        {
            Text = _text.Length == 0 ? "（空）" : _text,
            FontSize = 12,
            FontFamily = "monospace",
            // 之前用 NoWrap：安卓上会被压成单行 TextView，整段日志只剩一行可见。
            // 日志按原样换行即可，内容完整优先，超出宽度自动折行。
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Start,
        };

        // 用 Grid 明确分成「说明 / 正文」两行，正文给 * 高度，ScrollView 才会正常滚动，
        // 放进 VerticalStackLayout 会按内容无限撑高，长日志既滚不动又拖慢布局。
        var root = new Grid
        {
            Padding = new Thickness(16, 12, 16, 20),
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
        };

        var subtitleLabel = new Label
        {
            Text = subtitle ?? string.Empty,
            IsVisible = !string.IsNullOrWhiteSpace(subtitle),
            FontSize = 11,
            LineBreakMode = LineBreakMode.WordWrap,
            TextColor = Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#A0A0A0")
                : Color.FromArgb("#808080"),
        };
        Grid.SetRow(subtitleLabel, 0);
        root.Children.Add(subtitleLabel);

        var border = new Border
        {
            Padding = 12,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#2A2A2A")
                : Color.FromArgb("#F2F2F2"),
            Content = new ScrollView
            {
                Orientation = ScrollOrientation.Vertical,
                Content = body,
            },
        };
        Grid.SetRow(border, 1);
        root.Children.Add(border);

        Content = root;

        var copy = new ToolbarItem { Text = "复制" };
        copy.Clicked += async (_, _) =>
        {
            try
            {
                await Clipboard.SetTextAsync(_text);
            }
            catch
            {
                // 剪贴板不可用时忽略，页面内容仍可手动选择
            }
        };
        ToolbarItems.Add(copy);

        var share = new ToolbarItem { Text = "分享" };
        share.Clicked += async (_, _) =>
        {
            try
            {
                await Share.RequestAsync(new ShareTextRequest { Title = Title, Text = _text });
            }
            catch
            {
                // 分享面板不可用时忽略
            }
        };
        ToolbarItems.Add(share);
    }

    /// <summary>从任意页面 / 内嵌面板打开独立查看界面。</summary>
    public static async Task ShowAsync(string title, string text, string? subtitle = null)
    {
        var root = Application.Current?.Windows.FirstOrDefault()?.Page;
        var nav = root is NavigationPage navigation ? navigation.Navigation : root?.Navigation;
        if (nav == null) return;

        var page = new TextViewerPage(title, text, subtitle);
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                await nav.PushAsync(page);
            }
            catch (Exception ex)
            {
                DoLLauncher.Services.LogService.Error("打开长文本查看界面失败", ex);
            }
        });
    }
}
