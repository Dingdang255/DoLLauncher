using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using DoLLauncher.Controls;
using DoLLauncher.Core.Browser;
using DoLLauncher.Core.Search;
using DoLLauncher.Core.Theme;
using DoLLauncher.Core.Versions;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Win32;
using SubtitleTextHelper = DoLLauncher.Core.Ui.SubtitleText;

namespace DoLLauncher;

public partial class MainWindow
{
    // ---------- 致谢名单 ----------

    private void LoadCredits()
    {
        try
        {
            CreditsPanel.Children.Clear();
            var credits = CreditsService.Load();
            _projectUrl = credits.ProjectUrl ?? string.Empty;
            ProjectHomeBtn.Visibility = _projectUrl.Trim().Length == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            foreach (var entry in credits.Entries)
                CreditsPanel.Children.Add(BuildCreditRow(entry));
        }
        catch (Exception ex)
        {
            Log("加载致谢名单失败：" + ex.Message);
        }
    }

    /// <summary>头像统一尺寸，避免不同来源的图片把行高撑乱。</summary>
    private const double CreditAvatarSize = 36;

    private FrameworkElement BuildCreditRow(CreditEntry entry)
    {
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        var pale = TryFindResource("AccentPaleBrush") as Brush ?? Brushes.Transparent;
        var mutedStyle = TryFindResource("MutedText") as Style;

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };

        var radius = CreditAvatarSize / 2;
        var avatar = new Border
        {
            Width = CreditAvatarSize,
            Height = CreditAvatarSize,
            CornerRadius = new CornerRadius(radius),
            Background = pale,
            Clip = new EllipseGeometry(new Point(radius, radius), radius, radius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var image = TryLoadAvatar(entry.Avatar, (int)(CreditAvatarSize * 2));
        avatar.Child = image != null
            ? new Image { Source = image, Stretch = Stretch.UniformToFill }
            : new TextBlock
            {
                Text = FirstChar(entry.Name),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        DockPanel.SetDock(avatar, Dock.Left);
        row.Children.Add(avatar);

        // 名称 / 职位两行取较宽的一行，图标按钮紧贴在它右侧，宽度由布局自动决定
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold });
        if ((entry.Role ?? string.Empty).Trim().Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = entry.Role,
                Style = mutedStyle,
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        if ((entry.Bilibili ?? string.Empty).Trim().Length > 0)
            links.Children.Add(MakeCreditLinkButton(entry.Bilibili));
        if ((entry.Github ?? string.Empty).Trim().Length > 0)
            links.Children.Add(MakeCreditLinkButton(entry.Github, links.Children.Count > 0 ? 4 : 0));
        if ((entry.Website ?? string.Empty).Trim().Length > 0)
            links.Children.Add(MakeCreditLinkButton(entry.Website, links.Children.Count > 0 ? 4 : 0));

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(text);
        if (links.Children.Count > 0) content.Children.Add(links);

        row.Children.Add(content);
        return row;
    }

    /// <summary>
    /// 域名图标按钮：默认只显示小图标，悬停时动画展开显示「打开 xxx 主页」。
    /// 宽度用固定起点 + 文字实测宽度计算，不依赖鼠标坐标，避免位置错乱。
    /// </summary>
    private FrameworkElement MakeCreditLinkButton(string? url, double leftMargin = 0)
    {
        var text = (url ?? string.Empty).Trim();
        var provider = LinkProvider(text);
        var labelText = provider switch
        {
            "Wiki" => "打开 Wiki 介绍",
            "链接" => "打开链接",
            _ => $"打开 {provider} 主页",
        };
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;

        var icon = BuildProviderIcon(provider);
        var label = new TextBlock
        {
            Text = labelText,
            FontSize = 11,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 1, 0),
            Opacity = 0,
            TextWrapping = TextWrapping.NoWrap,
        };
        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 5, 0),
        };
        inner.Children.Add(icon);
        inner.Children.Add(label);

        // 收起宽度 = 左右留白 10 + 图标 18
        const double collapsed = 28;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var expanded = Math.Ceiling(10 + 18 + label.DesiredSize.Width + 8);

        var button = new Button
        {
            Style = TryFindResource("CreditLinkButton") as Style,
            Width = collapsed,
            Height = 24,
            Margin = new Thickness(leftMargin, 0, 0, 0),
            ToolTip = labelText,
            Content = inner,
        };

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        button.MouseEnter += (_, _) =>
        {
            button.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(expanded, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
            label.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(30) });
        };
        button.MouseLeave += (_, _) =>
        {
            button.BeginAnimation(FrameworkElement.WidthProperty,
                new DoubleAnimation(collapsed, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            label.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(80)));
        };
        button.Click += (_, _) => OpenExternal(text);
        return button;
    }

    private static string LinkProvider(string url)
    {
        if (url.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase)) return "Bilibili";
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase)) return "GitHub";
        if (url.Contains("miraheze.org", StringComparison.OrdinalIgnoreCase)) return "Wiki";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0) return uri.Host;
        return "链接";
    }

    /// <summary>按域名返回矢量图标：Bilibili 蓝色小电视、GitHub 黑猫，其它域名用通用链接图标。</summary>
    private static FrameworkElement BuildProviderIcon(string provider)
    {
        var chip = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };

        if (string.Equals(provider, "Bilibili", StringComparison.OrdinalIgnoreCase))
        {
            // 使用从 B 站站点抓取的真实 favicon，而不是手绘图形
            var realIcon = TryLoadAvatar("Assets/credits/bilibili.png");
            if (realIcon != null)
            {
                chip.Background = Brushes.Transparent;
                chip.Child = new Image { Source = realIcon, Stretch = Stretch.Uniform };
                return chip;
            }

            chip.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF7, 0xFD));
            var blue = new SolidColorBrush(Color.FromRgb(0x00, 0xA1, 0xD6));
            var canvas = new Canvas { Width = 18, Height = 18 };

            var body = new Shapes.Rectangle
            {
                Width = 12,
                Height = 8,
                RadiusX = 2.5,
                RadiusY = 2.5,
                Fill = blue,
            };
            Canvas.SetLeft(body, 3);
            Canvas.SetTop(body, 6);
            canvas.Children.Add(body);

            foreach (var left in new[] { 6.0, 10.4 })
            {
                var eye = new Shapes.Rectangle
                {
                    Width = 1.6,
                    Height = 2.6,
                    RadiusX = 0.8,
                    RadiusY = 0.8,
                    Fill = Brushes.White,
                };
                Canvas.SetLeft(eye, left);
                Canvas.SetTop(eye, 8.7);
                canvas.Children.Add(eye);
            }

            foreach (var (x1, y1, x2, y2) in new[] { (4.5, 1.5, 7.5, 6.0), (13.5, 1.5, 10.5, 6.0) })
            {
                canvas.Children.Add(new Shapes.Line
                {
                    X1 = x1,
                    Y1 = y1,
                    X2 = x2,
                    Y2 = y2,
                    Stroke = blue,
                    StrokeThickness = 1.4,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                });
            }

            chip.Child = canvas;
            return chip;
        }

        if (string.Equals(provider, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            chip.Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF5, 0xF8));
            chip.Child = new Shapes.Path
            {
                Data = Geometry.Parse(
                    "M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27s1.36.09 2 .27c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z"),
                Fill = new SolidColorBrush(Color.FromRgb(0x24, 0x29, 0x2F)),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(2.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return chip;
        }

        chip.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF7));
        chip.Child = new TextBlock
        {
            Text = "\uE71B",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x70, 0xF3)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return chip;
    }

    private static string FirstChar(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        return text.Length > 0 ? text[..1].ToUpperInvariant() : "?";
    }

    private static ImageSource? TryLoadAvatar(string? path, int decodeWidth = 0)
    {
        try
        {
            var text = (path ?? string.Empty).Trim();
            if (text.Length == 0) return null;
            Uri uri;
            if (text.StartsWith("pack:", StringComparison.OrdinalIgnoreCase))
                uri = new Uri(text, UriKind.Absolute);
            else if (text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                     text.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase))
                uri = new Uri("pack://application:,,,/" + text.Replace('\\', '/'), UriKind.Absolute);
            else if (Path.IsPathRooted(text)) uri = new Uri(text);
            else if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)) uri = absolute;
            else uri = new Uri(Path.Combine(Paths.Root, text));

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void ProjectHome_Click(object sender, RoutedEventArgs e)
    {
        if (_projectUrl.Trim().Length == 0)
        {
            AppDialog.Info("credits.json 里还没有填写项目主页地址。", "项目主页");
            return;
        }
        OpenExternal(_projectUrl);
    }

    /// <summary>反馈入口：打开仓库的 Issue 模板选择页。</summary>
    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        var home = (_projectUrl ?? string.Empty).Trim().TrimEnd('/');
        if (home.Length == 0)
        {
            AppDialog.Info("credits.json 里还没有填写项目主页地址，暂时无法打开反馈页。", "反馈与建议");
            return;
        }
        OpenExternal(home + "/issues/new/choose");
    }

    private void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("打开链接失败：" + ex.Message);
            AppDialog.Error("无法打开链接：" + ex.Message, "打开链接");
        }
    }

}
