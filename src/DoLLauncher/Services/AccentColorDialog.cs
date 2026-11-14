using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DoLLauncher.Core.Theme;

namespace DoLLauncher.Services;

/// <summary>
/// 自绘的主题主色选择框：R/G/B 三个滑条 + 实时预览，避免为 ColorDialog 引入 WinForms 依赖。
/// 视觉沿用 AppDialog 的卡片式布局。
/// </summary>
public static class AccentColorDialog
{
    public static RgbColor? Show(Window? owner, RgbColor current)
    {
        var win = new Window
        {
            Title = "选择主题主色",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner == null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Background = ResBrush("BgBrush") ?? Brushes.White,
            Foreground = ResBrush("TextBrush") ?? Brushes.Black,
        };
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) win.DialogResult = false;
        };
        AppDialog.AttachDialogChrome(win, 420);
        if (owner != null) win.Owner = owner;
        ThemeService.Attach(win);
        UiAnimations.AttachWindow(win);

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        root.Children.Add(new TextBlock
        {
            Text = "选择主题主色",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResBrush("TextBrush"),
        });
        root.Children.Add(new TextBlock
        {
            Text = "主色按「主色占比」混入强调色系（按钮、选中态、分页头等），亮色与深色主题分别混合。",
            Style = ResStyle("MutedText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        var preview = new Border
        {
            Height = 46,
            CornerRadius = new CornerRadius(7),
            BorderBrush = ResBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(current.R, current.G, current.B)),
        };
        root.Children.Add(preview);

        var hex = new TextBlock
        {
            Style = ResStyle("MutedText"),
            Margin = new Thickness(0, 8, 0, 10),
            Text = current.ToArgbHex(),
        };
        root.Children.Add(hex);

        var sliders = new Slider[3];
        var values = new TextBlock[3];
        string[] labels = { "R", "G", "B" };
        byte[] initial = { current.R, current.G, current.B };

        for (var i = 0; i < 3; i++)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

            grid.Children.Add(new TextBlock
            {
                Text = labels[i],
                Style = ResStyle("MutedText"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 255,
                Value = initial[i],
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);
            sliders[i] = slider;

            var value = new TextBlock
            {
                Text = initial[i].ToString(),
                Style = ResStyle("MutedText"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);
            values[i] = value;

            root.Children.Add(grid);
        }

        RgbColor Picked() => new(
            (byte)Math.Round(sliders[0].Value),
            (byte)Math.Round(sliders[1].Value),
            (byte)Math.Round(sliders[2].Value));

        void Update()
        {
            var rgb = Picked();
            preview.Background = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
            hex.Text = rgb.ToArgbHex();
            for (var i = 0; i < 3; i++) values[i].Text = ((byte)Math.Round(sliders[i].Value)).ToString();
        }

        foreach (var slider in sliders) slider.ValueChanged += (_, _) => Update();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var cancel = new Button
        {
            Content = "取消",
            Style = ResStyle("FlatButton"),
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => win.DialogResult = false;
        buttons.Children.Add(cancel);

        var ok = new Button
        {
            Content = "确定",
            Style = ResStyle("FlatButton"),
            MinWidth = 88,
            Foreground = ResBrush("AccentBrush"),
            BorderBrush = ResBrush("AccentSoftBrush"),
            IsDefault = true,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        win.Content = root;
        UiAnimations.ShowPage(root);

        return win.ShowDialog() == true ? Picked() : (RgbColor?)null;
    }

    private static Brush? ResBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    private static Style? ResStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;
}
