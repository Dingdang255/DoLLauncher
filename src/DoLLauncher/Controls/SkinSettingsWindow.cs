using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DoLLauncher.Core.Skin;
using DoLLauncher.Services;
using Microsoft.Win32;

namespace DoLLauncher.Controls;

/// <summary>
/// 「美化包设置」窗口：按当前皮肤的 <c>settings[]</c> 声明式生成控件，
/// 每次变更立即生效并写入 <c>skins/&lt;id&gt;/settings.json</c>。
/// 纯代码实现，视觉沿用启动器主题画刷，不使用系统 MessageBox。
/// </summary>
public sealed class SkinSettingsWindow : Window
{
    private readonly string _skinDir;
    private SkinSettingsState _state = new();
    private bool _suppress;
    private TextBlock? _metaText;

    public SkinSettingsWindow()
    {
        Title = "美化包设置";
        Width = 520;
        MaxHeight = 720;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        ThemeService.Attach(this);
        UiAnimations.AttachWindow(this);

        _skinDir = SkinService.CurrentFolder.Length > 0
            ? Path.Combine(SkinService.SkinsRoot, SkinService.CurrentFolder)
            : string.Empty;
        _state = SkinService.Current == null || _skinDir.Length == 0
            ? new SkinSettingsState()
            : SkinSettingsStore.Load(_skinDir);

        BuildContent();
    }

    private void BuildContent()
    {
        var skin = SkinService.Current;
        var root = new Grid { Margin = new Thickness(20, 16, 20, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(BuildHeader(skin));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 10, 0, 0),
            MaxHeight = 540,
        };
        scroll.Content = BuildBody(skin);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = BuildFooter(skin);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    private UIElement BuildHeader(SkinManifest? skin)
    {
        var panel = new StackPanel();

        var title = string.IsNullOrWhiteSpace(skin?.Name) ? "美化包设置" : skin!.Name;
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        panel.Children.Add(titleBlock);

        _metaText = new TextBlock
        {
            Style = ResStyle("MutedText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Text = MetaText(skin),
        };
        panel.Children.Add(_metaText);

        if (skin != null && !string.IsNullOrWhiteSpace(skin.Description))
        {
            panel.Children.Add(new TextBlock
            {
                Style = ResStyle("MutedText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Text = skin.Description,
            });
        }

        return panel;
    }

    private static string MetaText(SkinManifest? skin)
    {
        if (skin == null) return "当前未加载美化包。";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(skin.Version)) parts.Add("v" + skin.Version.Trim());
        if (!string.IsNullOrWhiteSpace(skin.Author)) parts.Add(skin.Author.Trim());
        var theme = SkinService.CurrentTheme;
        parts.Add("当前主题：" + (theme == null ? "无" : theme.Name));
        return string.Join(" · ", parts);
    }

    private UIElement BuildBody(SkinManifest? skin)
    {
        var panel = new StackPanel();
        var items = new List<SkinSettingDefinition>();

        if (skin?.Settings != null)
        {
            foreach (var definition in skin.Settings)
            {
                if (definition == null) continue;
                if (items.Count >= SkinRules.MaxSettings) break;
                if (string.IsNullOrWhiteSpace(definition.Key)) continue;
                items.Add(definition);
            }
        }

        if (items.Count == 0)
        {
            panel.Children.Add(MessageCard(skin == null ? "请先加载美化包。" : "该美化包没有可调项。"));
            if (skin != null) panel.Children.Add(BuildSurfaceGroup());
            return panel;
        }

        _suppress = true;
        try
        {
            foreach (var definition in items) panel.Children.Add(BuildSettingCard(definition));
            if (skin != null) panel.Children.Add(BuildSurfaceGroup());
        }
        finally
        {
            _suppress = false;
        }
        return panel;
    }

    /// <summary>底图分组：窗口背景 / 按钮 / 卡片，导入后复制到 <c>skins/&lt;id&gt;/custom/</c>。</summary>
    private UIElement BuildSurfaceGroup()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

        var title = new TextBlock
        {
            Text = "底图",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 8),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        panel.Children.Add(title);

        panel.Children.Add(BuildSurfaceRow("window", "窗口背景图", _state.WindowImage));
        panel.Children.Add(BuildSurfaceRow("button", "按钮底图", _state.ButtonImage));
        panel.Children.Add(BuildSurfaceRow("card", "卡片底图", _state.CardImage));
        return panel;
    }

    private UIElement BuildSurfaceRow(string kind, string label, string relative)
    {
        var card = new Border { Style = ResStyle("SettingsItemCard"), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Medium,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        grid.Children.Add(name);

        var file = (relative ?? string.Empty).Trim().Replace('\\', '/');
        var fileName = new TextBlock
        {
            Style = ResStyle("MutedText"),
            Text = file.Length == 0 ? "未设置" : Path.GetFileName(file),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(fileName, 1);
        grid.Children.Add(fileName);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var import = new Button
        {
            Content = "导入",
            Style = ResStyle("FlatButton"),
            MinWidth = 64,
            Margin = new Thickness(0, 0, 6, 0),
        };
        import.Click += (_, _) => ImportSurface(kind, label);
        actions.Children.Add(import);

        var remove = new Button
        {
            Content = "移除",
            Style = ResStyle("FlatButton"),
            MinWidth = 64,
            IsEnabled = file.Length > 0,
        };
        remove.Click += (_, _) => RemoveSurface(kind);
        actions.Children.Add(remove);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private void ImportSurface(string kind, string label)
    {
        if (_skinDir.Length == 0) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择" + label,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
        {
            LogService.Append("不支持的底图格式：" + ext);
            return;
        }

        try
        {
            var custom = Path.Combine(_skinDir, "custom");
            Directory.CreateDirectory(custom);
            foreach (var old in Directory.GetFiles(custom, kind + ".*")) File.Delete(old);

            var target = Path.Combine(custom, kind + ext);
            File.Copy(dialog.FileName, target, overwrite: true);

            var relative = "custom/" + kind + ext;
            SetSurfaceField(kind, relative);
            SaveState();
            SkinService.SetSurfaceImage(kind, relative);
            Rebuild();
        }
        catch (Exception ex)
        {
            LogService.Append("导入底图失败：" + ex.Message);
        }
    }

    private void RemoveSurface(string kind)
    {
        if (_skinDir.Length == 0) return;

        try
        {
            var custom = Path.Combine(_skinDir, "custom");
            if (Directory.Exists(custom))
                foreach (var old in Directory.GetFiles(custom, kind + ".*")) File.Delete(old);
        }
        catch (Exception ex)
        {
            LogService.Append("移除底图失败：" + ex.Message);
        }

        SetSurfaceField(kind, string.Empty);
        SaveState();
        SkinService.SetSurfaceImage(kind, null);
        Rebuild();
    }

    private void SetSurfaceField(string kind, string relative)
    {
        switch (kind)
        {
            case "window": _state.WindowImage = relative; break;
            case "button": _state.ButtonImage = relative; break;
            case "card": _state.CardImage = relative; break;
        }
    }

    private UIElement BuildSettingCard(SkinSettingDefinition definition)
    {
        var card = new Border { Style = ResStyle("SettingsItemCard") };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(definition.Label) ? definition.Key : definition.Label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Medium,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        grid.Children.Add(label);

        var control = (FrameworkElement)BuildControl(definition);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        card.Child = grid;
        return card;
    }

    private UIElement BuildControl(SkinSettingDefinition definition)
    {
        var type = (definition.Type ?? "toggle").Trim().ToLowerInvariant();
        return type switch
        {
            "select" => BuildSelect(definition),
            "slider" => BuildSlider(definition),
            "color" => BuildColor(definition),
            _ => BuildToggle(definition),
        };
    }

    private UIElement BuildToggle(SkinSettingDefinition definition)
    {
        var check = new CheckBox
        {
            Style = ResStyle("TextCheck"),
            IsChecked = ParseBool(_state.ValueOf(definition), definition.GetDefaultBool()),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        check.Checked += (_, _) =>
        {
            if (!_suppress) Change(definition, "true");
        };
        check.Unchecked += (_, _) =>
        {
            if (!_suppress) Change(definition, "false");
        };
        return check;
    }

    private sealed class ThemeOption
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public override string ToString() => Name;
    }

    private UIElement BuildSelect(SkinSettingDefinition definition)
    {
        var value = _state.ValueOf(definition);
        var combo = new ComboBox
        {
            Style = ResStyle("FlatCombo"),
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        if (definition.OptionsFromThemes)
        {
            // 自定义 ComboBox 模板显示 SelectionBoxItem，用 ToString 返回中文名最稳
            var themes = SkinService.Themes
                .Select(item => new ThemeOption
                {
                    Id = item.Id,
                    Name = string.IsNullOrWhiteSpace(item.Name) ? item.Id : item.Name,
                })
                .ToList();
            combo.SelectedValuePath = "Id";
            combo.ItemsSource = themes;
            combo.SelectedItem = themes.FirstOrDefault(item =>
                                     item.Id.Equals(value, StringComparison.OrdinalIgnoreCase))
                                 ?? (themes.Count > 0 ? themes[0] : null);
        }
        else
        {
            var options = definition.Options ?? new List<string>();
            combo.ItemsSource = options;
            combo.SelectedItem = options.FirstOrDefault(item =>
                                     string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                                 ?? (options.Count > 0 ? options[0] : null);
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (_suppress) return;
            var text = definition.OptionsFromThemes ? combo.SelectedValue as string : combo.SelectedItem as string;
            if (!string.IsNullOrEmpty(text)) Change(definition, text);
        };
        return combo;
    }

    private UIElement BuildSlider(SkinSettingDefinition definition)
    {
        var min = definition.Min ?? 0;
        var max = definition.Max ?? 100;
        if (max <= min) max = min + 100;
        var step = definition.Step is { } declared && declared > 0 ? declared : 1;

        var initial = ParseDouble(_state.ValueOf(definition), definition.GetDefaultDouble(min));
        initial = Math.Clamp(initial, min, max);

        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = initial,
            TickFrequency = step,
            SmallChange = step,
            LargeChange = step * 5,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.SetResourceReference(ForegroundProperty, "AccentBrush");
        host.Children.Add(slider);

        var valueText = new TextBlock
        {
            Style = ResStyle("MutedText"),
            Text = FormatNumber(initial),
            Width = 48,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(valueText, 1);
        host.Children.Add(valueText);

        slider.ValueChanged += (_, _) =>
        {
            var text = FormatNumber(slider.Value);
            valueText.Text = text;
            if (!_suppress) Change(definition, text);
        };
        return host;
    }

    private UIElement BuildColor(SkinSettingDefinition definition)
    {
        var box = new TextBox
        {
            Style = ResStyle("DarkTextBox"),
            Text = _state.ValueOf(definition),
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.TextChanged += (_, _) =>
        {
            if (!_suppress) Change(definition, box.Text ?? string.Empty);
        };
        return box;
    }

    private UIElement BuildFooter(SkinManifest? skin)
    {
        var hasSettings = skin?.Settings is { Count: > 0 };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        if (skin != null && hasSettings)
        {
            var reset = new Button
            {
                Content = "重置本页",
                Style = ResStyle("FlatButton"),
                MinWidth = 88,
                Margin = new Thickness(0, 0, 8, 0),
            };
            reset.Click += (_, _) => ResetPage();
            panel.Children.Add(reset);
        }

        if (skin != null)
        {
            var factory = new Button
            {
                Content = "恢复出厂",
                Style = ResStyle("FlatButton"),
                MinWidth = 88,
                Margin = new Thickness(0, 0, 8, 0),
            };
            factory.Click += (_, _) => ResetFactory();
            panel.Children.Add(factory);
        }

        var close = new Button
        {
            Content = "关闭",
            Style = ResStyle("FlatButton"),
            MinWidth = 84,
        };
        close.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        close.SetResourceReference(Control.BorderBrushProperty, "AccentSoftBrush");
        close.Click += (_, _) => Close();
        panel.Children.Add(close);

        return panel;
    }

    private void Change(SkinSettingDefinition definition, string value)
    {
        var key = (definition.Key ?? string.Empty).Trim();
        if (key.Length == 0) return;

        _state.SetValue(key, value);
        ApplySetting(definition, key, value);
        UpdateMeta();
        SaveState();
    }

    private void UpdateMeta()
    {
        if (_metaText != null) _metaText.Text = MetaText(SkinService.Current);
    }

    /// <summary>按设置项 key / target 立即应用；无法识别的项只保存值。</summary>
    private static void ApplySetting(SkinSettingDefinition definition, string key, string value)
    {
        if (SkinService.Current == null) return;

        var lower = key.ToLowerInvariant();
        if (lower == "theme")
        {
            SkinService.ApplyTheme(value);
            return;
        }

        if (lower.Contains("motion"))
        {
            SkinService.MotionEnabled = ParseBool(value, true);
            return;
        }

        if (lower.Contains("particle"))
        {
            var number = (int)Math.Round(ParseDouble(value, definition.GetDefaultDouble(0)));
            var ambient = SkinService.Effects.Ambient;
            if (ambient == null)
            {
                ambient = new SkinAmbientOptions();
                SkinService.Effects.Ambient = ambient;
            }
            ambient.Particles = Math.Clamp(number, 0, SkinRules.MaxParticles);
            SkinService.RefreshEffects();
            return;
        }

        if (lower.Contains("ornament"))
        {
            SkinService.OrnamentEnabled = ParseBool(value, true);
            SkinService.RefreshEffects();
            return;
        }

        var target = (definition.Target ?? string.Empty).Trim();
        if (target.StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value))
            SkinService.SetVariableOverride(target, value);
    }

    private void SaveState()
    {
        if (_skinDir.Length > 0) SkinSettingsStore.Save(_skinDir, _state);
    }

    /// <summary>重置本页：删除保存值、清除变量覆盖并应用本皮肤默认值。</summary>
    private void ResetPage()
    {
        if (SkinService.Current == null || _skinDir.Length == 0) return;

        SkinSettingsStore.ResetValues(_skinDir);
        _state = new SkinSettingsState();
        SkinService.ClearVariableOverrides();
        SkinService.ApplyStoredSettings();
        Rebuild();
    }

    /// <summary>恢复出厂：删除 settings.json 并重新启用皮肤（默认主题与效果从头应用）。</summary>
    private void ResetFactory()
    {
        if (SkinService.Current == null) return;

        SkinSettingsStore.ResetValues(_skinDir);
        _state = new SkinSettingsState();

        var folder = SkinService.CurrentFolder;
        if (folder.Length > 0) SkinService.Activate(folder);
        else SkinService.ApplyStoredSettings();

        ThemeService.Reapply();
        if (Owner != null) SkinService.ApplyToWindow(Owner);
        Rebuild();
    }

    private void Rebuild()
    {
        _state = _skinDir.Length > 0 ? SkinSettingsStore.Load(_skinDir) : new SkinSettingsState();
        _metaText = null;
        BuildContent();
    }

    private static UIElement MessageCard(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return new Border { Style = ResStyle("SettingsItemCard"), Child = block };
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return fallback;
        if (bool.TryParse(text, out var parsed)) return parsed;
        return text.ToLowerInvariant() switch
        {
            "1" or "on" or "yes" => true,
            "0" or "off" or "no" => false,
            _ => fallback,
        };
    }

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static Style? ResStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;
}
