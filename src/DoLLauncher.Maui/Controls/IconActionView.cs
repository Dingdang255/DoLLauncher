using Microsoft.Maui.Controls.Shapes;

namespace DoLLauncher.Maui.Controls;

/// <summary>
/// 带 Fluent 图标的操作按钮：图标 + 可选文字，点击事件与 WPF 的 IconButton 对应。
/// 用 Border + 手势实现，因为 MAUI 的 Button 只能显示单一字体的文本，无法图标与中文混排。
/// </summary>
public sealed class IconActionView : ContentView
{
    public static readonly BindableProperty GlyphProperty = BindableProperty.Create(
        nameof(Glyph), typeof(string), typeof(IconActionView), string.Empty,
        propertyChanged: (b, _, _) => ((IconActionView)b).Refresh());

    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(IconActionView), string.Empty,
        propertyChanged: (b, _, _) => ((IconActionView)b).Refresh());

    /// <summary>primary / pale / muted / danger / plain，对应 WPF 的几种 IconButton 样式。</summary>
    public static readonly BindableProperty VariantProperty = BindableProperty.Create(
        nameof(Variant), typeof(string), typeof(IconActionView), "muted",
        propertyChanged: (b, _, _) => ((IconActionView)b).Refresh());

    /// <summary>只显示图标，不显示文字（用于卡片右侧的紧凑操作）。</summary>
    public static readonly BindableProperty IsCompactProperty = BindableProperty.Create(
        nameof(IsCompact), typeof(bool), typeof(IconActionView), false,
        propertyChanged: (b, _, _) => ((IconActionView)b).Refresh());

    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(
        nameof(CommandParameter), typeof(object), typeof(IconActionView));

    public event EventHandler? Tapped;

    private readonly Border _border;
    private readonly Label _glyph;
    private readonly Label _label;

    public IconActionView()
    {
        _glyph = new Label
        {
            FontFamily = "FluentIcons",
            FontSize = 17,
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        _label = new Label
        {
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.NoWrap,
        };

        var stack = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Children = { _glyph, _label },
        };

        _border = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = new Thickness(12, 8),
            MinimumHeightRequest = 40,
            Content = stack,
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (IsEnabled) Tapped?.Invoke(this, EventArgs.Empty);
        };
        _border.GestureRecognizers.Add(tap);

        Content = _border;
        Refresh();
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Variant
    {
        get => (string)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <summary>
    /// 导航项选中态：直接写颜色而不是动态资源。
    /// MAUI 里 Border.BackgroundColor 绑 DynamicResource 在运行时切换不会重绘，只有重建页面时才显示一次。
    /// </summary>
    public void SetSelected(bool selected)
    {
        _border.RemoveDynamicResource(Border.BackgroundColorProperty);
        if (selected)
        {
            _border.BackgroundColor = Resource("AccentPaleColor", Color.FromArgb("#EAF3FF"));
            SetForegroundDynamic("AccentColor", Color.FromArgb("#1370F3"));
        }
        else
        {
            _border.BackgroundColor = Colors.Transparent;
            SetForeground(Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));
        }
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == IsEnabledProperty.PropertyName)
            Opacity = IsEnabled ? 1 : 0.45;
    }

    private void Refresh()
    {
        _glyph.Text = Glyph;
        _glyph.IsVisible = !string.IsNullOrEmpty(Glyph);

        var showText = !IsCompact && !string.IsNullOrEmpty(Text);
        _label.Text = Text;
        _label.IsVisible = showText;

        _border.Padding = IsCompact ? new Thickness(10, 8) : new Thickness(12, 8);
        _border.HorizontalOptions = IsCompact ? LayoutOptions.Center : LayoutOptions.Fill;

        EnsureStyleClasses();

        switch ((Variant ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "primary":
                _border.SetDynamicResource(Border.BackgroundColorProperty, "AccentColor");
                SetForegroundDynamic("AccentOnColor", Colors.White);
                break;
            case "pale":
                _border.SetDynamicResource(Border.BackgroundColorProperty, "AccentPaleColor");
                SetForegroundDynamic("AccentColor", Color.FromArgb("#1370F3"));
                break;
            case "danger":
                _border.RemoveDynamicResource(Border.BackgroundColorProperty);
                _border.SetAppThemeColor(Border.BackgroundColorProperty,
                    Color.FromArgb("#E0E0E0"), Color.FromArgb("#3C3C3C"));
                SetForeground(Color.FromArgb("#C62828"), Color.FromArgb("#EF9A9A"));
                break;
            case "plain":
                _border.RemoveDynamicResource(Border.BackgroundColorProperty);
                _border.BackgroundColor = Colors.Transparent;
                SetForeground(Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));
                break;
            default:
                _border.RemoveDynamicResource(Border.BackgroundColorProperty);
                _border.SetAppThemeColor(Border.BackgroundColorProperty,
                    Color.FromArgb("#E0E0E0"), Color.FromArgb("#3C3C3C"));
                SetForeground(Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));
                break;
        }

        if (!string.IsNullOrEmpty(Text))
            SemanticProperties.SetDescription(this, Text);
    }

    /// <summary>给美化包的 .class 选择器提供稳定的类名：button / button-primary / button-compact。</summary>
    private void EnsureStyleClasses()
    {
        var classes = StyleClass;
        if (classes == null)
        {
            classes = new List<string>();
            StyleClass = classes;
        }

        void Ensure(string name)
        {
            foreach (var item in classes)
                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase)) return;
            classes.Add(name);
        }

        Ensure("button");
        var variant = (Variant ?? string.Empty).Trim().ToLowerInvariant();
        if (variant.Length > 0) Ensure("button-" + variant);
        if (IsCompact) Ensure("button-compact");

        // 内层 Border 才是实际可见的按钮底板，同步一份类名让 .button 的背景 / 圆角生效
        _border.StyleClass = new List<string>(classes);
    }

    private void SetForeground(Color color)
    {
        _glyph.RemoveDynamicResource(Label.TextColorProperty);
        _label.RemoveDynamicResource(Label.TextColorProperty);
        _glyph.TextColor = color;
        _label.TextColor = color;
    }

    private void SetForegroundDynamic(string key, Color fallback)
    {
        _glyph.SetDynamicResource(Label.TextColorProperty, key);
        _label.SetDynamicResource(Label.TextColorProperty, key);
    }

    private void SetForeground(Color light, Color dark)
    {
        _glyph.RemoveDynamicResource(Label.TextColorProperty);
        _label.RemoveDynamicResource(Label.TextColorProperty);
        _glyph.SetAppThemeColor(Label.TextColorProperty, light, dark);
        _label.SetAppThemeColor(Label.TextColorProperty, light, dark);
    }

    private static Color Resource(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color)
            return color;
        return fallback;
    }
}
