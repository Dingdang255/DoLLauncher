using DoLLauncher.Maui.Controls;

namespace DoLLauncher.Maui;

/// <summary>通用 Markdown 阅读页：标题为条目名，正文用 MarkdownView 渲染，可滚动。</summary>
public sealed class MarkdownPage : ContentPage
{
    public MarkdownPage(string title, string markdown)
    {
        Title = title;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(18, 14, 18, 28),
                Children =
                {
                    new MarkdownView { Markdown = markdown ?? string.Empty },
                },
            },
        };
    }
}
