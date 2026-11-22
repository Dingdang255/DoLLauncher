using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoLLauncher.Core.Skin;

namespace DoLLauncher.Services;

/// <summary>
/// dolskin/3 声明式组件：按 <c>components[]</c> 把 WPF 控件插入到宿主插槽。
/// 交互控件可挂 <c>events</c> 轻量脚本（经 <see cref="SkinScriptHost"/> 白名单执行），
/// <c>bind</c> 声明按 <c>{{ 表达式 }}</c> 实时求值；卸载时移除全部插入节点。
/// </summary>
public static class SkinComponentService
{
    private const string TagPrefix = "skin-component:";

    private sealed class Insertion
    {
        public FrameworkElement Node { get; init; } = null!;
        public Panel Parent { get; init; } = null!;
    }

    /// <summary>组件树里的一个节点（含嵌套子节点），用于注册 id、绑定与事件。</summary>
    private sealed class ComponentPart
    {
        public SkinComponentDefinition Definition { get; init; } = null!;
        public FrameworkElement Node { get; init; } = null!;
    }

    /// <summary>单个节点的数据绑定定义。</summary>
    private sealed class ComponentEntry
    {
        public FrameworkElement Node { get; init; } = null!;
        public string Id { get; init; } = string.Empty;
        public Dictionary<string, SkinBindingTemplate> Bindings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly List<Insertion> Inserted = new();
    private static readonly Dictionary<(FrameworkElement Host, string Id), FrameworkElement> Registry = new();
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>id → 节点索引，供脚本的 show / hide / toggle 定位。</summary>
    private static readonly Dictionary<string, FrameworkElement> ComponentNodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已插入节点的绑定定义；节点被移除后自动淘汰。</summary>
    private static readonly List<ComponentEntry> Bound = new();

    // 刷新绑定时会程序化改控件值，需屏蔽由此触发的回写 / 事件循环
    private static bool _refreshing;
    private static bool _handling;

    private static readonly HashSet<string> KnownProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "glyph", "value", "src",
        "background", "foreground", "border-color", "corner-radius",
        "padding", "margin", "width", "height", "font-size", "font-weight", "opacity",
        // 交互控件（Button / Toggle / Slider / Select）与绑定回写目标
        "checked", "min", "max", "step", "options", "bindTo",
    };

    /// <summary>当前已插入的顶层组件数量。</summary>
    public static int InsertedCount => Inserted.Count;

    /// <summary>遍历窗口可视树，为所有命中的 host 插入组件（幂等）。</summary>
    public static void Apply(Window window)
    {
        if (window == null) return;
        var components = SkinService.Current?.Components;
        if (components == null || components.Count == 0) return;

        var elements = new List<FrameworkElement>();
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<DependencyObject>();
        stack.Push(window);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;

            if (node is FrameworkElement element) elements.Add(element);

            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
            {
                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++) stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }

        foreach (var element in elements) TryApplyHost(element);
    }

    /// <summary>元素加载时尝试作为 host 插入组件（组件节点自身不会再次作为 host）。</summary>
    public static void TryApplyHost(FrameworkElement element)
    {
        if (element == null || IsComponentNode(element)) return;

        var components = SkinService.Current?.Components;
        if (components == null || components.Count == 0) return;

        var limit = Math.Min(components.Count, SkinRules.MaxComponents);
        for (var i = 0; i < limit; i++) TryInsert(element, components[i]);
    }

    /// <summary>移除全部插入节点并清空注册表。</summary>
    public static void Unload()
    {
        foreach (var item in Inserted)
        {
            try
            {
                item.Parent.Children.Remove(item.Node);
            }
            catch
            {
                // 父容器已被销毁时忽略
            }
        }

        Inserted.Clear();
        Registry.Clear();
        Warned.Clear();
        ComponentNodes.Clear();
        Bound.Clear();
        _refreshing = false;
        _handling = false;
    }

    /// <summary>按组件 id 找到已插入的节点；未找到时返回 null。</summary>
    public static FrameworkElement? FindComponent(string id)
    {
        var key = (id ?? string.Empty).Trim();
        if (key.Length == 0) return null;
        return ComponentNodes.TryGetValue(key, out var node) ? node : null;
    }

    /// <summary>重新求值所有仍存活的 <c>bind</c> 声明（变量、设置项或主题变化后调用）。</summary>
    public static void RefreshBindings()
    {
        if (_refreshing) return;

        _refreshing = true;
        try
        {
            for (var i = Bound.Count - 1; i >= 0; i--)
            {
                var entry = Bound[i];
                if (!IsAttached(entry.Node))
                {
                    Bound.RemoveAt(i);
                    continue;
                }

                ApplyEntry(entry);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static void TryInsert(FrameworkElement host, SkinComponentDefinition? definition)
    {
        try
        {
            InsertOne(host, definition);
        }
        catch (Exception ex)
        {
            LogService.Error("插入声明式组件失败", ex);
        }
    }

    private static void InsertOne(FrameworkElement host, SkinComponentDefinition? definition)
    {
        if (definition == null) return;

        var id = (definition.Id ?? string.Empty).Trim();
        if (id.Length == 0) return;
        if (IsComponentNode(host)) return;
        if (!SkinRules.IsKnownComponentWidget(definition.Widget)) return;
        if (!SkinRules.IsValidComponentSlot(definition.Slot)) return;
        if (!SkinRules.IsValidComponentHost(definition.Host)) return;
        if (Registry.ContainsKey((host, id))) return;
        if (!HostMatches(host, definition.Host)) return;
        if (HasVirtualizingAncestor(host))
        {
            WarnOnce("virtual:" + id, $"组件 {id} 的宿主位于虚拟化列表内，已跳过插入。");
            return;
        }

        if (definition.Props is { Count: > SkinRules.MaxComponentProps })
            WarnOnce("props:" + id, $"组件 {id} 的 props 超过上限 {SkinRules.MaxComponentProps}，超出部分将被忽略。");

        var slot = (definition.Slot ?? "end").Trim().ToLowerInvariant();
        Panel parent;
        int index;
        if (slot is "before" or "after")
        {
            if (ParentOf(host) is not Panel hostParent) return;
            var at = hostParent.Children.IndexOf(host);
            if (at < 0) return;
            parent = hostParent;
            index = slot == "after" ? at + 1 : at;
        }
        else
        {
            if (host is not Panel panel) return;
            parent = panel;
            index = slot == "start" ? 0 : panel.Children.Count;
        }

        var created = new List<FrameworkElement>();
        var parts = new List<ComponentPart>();
        var node = CreateWidget(definition, 0, created, parts);
        if (node == null) return;

        if (!string.IsNullOrWhiteSpace(definition.Class))
            SkinStyleApplier.SetSkinClasses(node, definition.Class);
        node.Tag = TagPrefix + id;

        parent.Children.Insert(index, node);
        Registry[(host, id)] = node;
        Inserted.Add(new Insertion { Node = node, Parent = parent });

        foreach (var element in created)
            SkinStyleApplier.ApplyToElement(element);

        foreach (var part in parts)
            RegisterComponent(part.Definition, part.Node);
    }

    /// <summary>
    /// 把节点登记到 id 索引，解析一次 <c>bind</c>（初始求值）与 <c>events</c>（挂事件）。
    /// 解析失败只告警并跳过该项，不影响组件本身。
    /// </summary>
    private static void RegisterComponent(SkinComponentDefinition definition, FrameworkElement node)
    {
        var id = (definition.Id ?? string.Empty).Trim();
        if (id.Length > 0) ComponentNodes.TryAdd(id, node);

        var entry = BuildEntry(definition, node, id);
        if (entry != null)
        {
            Bound.Add(entry);
            // 先渲染初始绑定再挂事件，避免程序化赋值触发回写
            ApplyEntry(entry);
        }

        AttachEvents(definition, node, id);
    }

    private static ComponentEntry? BuildEntry(
        SkinComponentDefinition definition, FrameworkElement node, string id)
    {
        var bindings = definition.Bind;
        if (bindings == null || bindings.Count == 0) return null;

        var entry = new ComponentEntry
        {
            Node = node,
            Id = id,
        };

        var count = 0;
        foreach (var pair in bindings)
        {
            if (++count > SkinRules.MaxComponentBindings) break;

            var property = (pair.Key ?? string.Empty).Trim();
            if (!SkinRules.IsKnownBindingProperty(property)) continue;

            var template = pair.Value ?? string.Empty;
            if (template.Length > SkinRules.MaxBindingLength)
            {
                WarnOnce($"bind-len:{id}:{property}",
                    $"组件 {id} 的绑定 {property} 超过 {SkinRules.MaxBindingLength} 字符上限，已忽略。");
                continue;
            }

            if (!SkinBindingTemplate.TryParse(template, out var parsed, out var error))
            {
                WarnOnce($"bind:{id}:{property}", $"组件 {id} 的绑定 {property} 无效：{error}");
                continue;
            }

            entry.Bindings[property] = parsed;
        }

        return entry.Bindings.Count > 0 ? entry : null;
    }

    /// <summary>按 <c>bindTo</c>（<c>settings.&lt;key&gt;</c> / <c>vars.&lt;name&gt;</c>）回写用户输入。</summary>
    private static void WriteBack(SkinComponentDefinition definition, string value)
    {
        var target = Prop(definition, "bindTo");
        var dot = target.IndexOf('.');
        if (dot <= 0 || dot == target.Length - 1) return;

        var kind = target[..dot].Trim().ToLowerInvariant();
        var name = target[(dot + 1)..].Trim();
        if (name.Length == 0) return;

        if (kind == "settings")
        {
            SkinService.SetSettingValue(name, value);
        }
        else if (kind == "vars")
        {
            SkinScriptHost.SetVariable(name, value);
            SkinService.NotifyBindingsChanged();
        }
    }

    /// <summary>交互控件的事件：Button 用 click，其余用 changed。</summary>
    private static void AttachEvents(SkinComponentDefinition definition, FrameworkElement node, string id)
    {
        var events = definition.Events;
        if (events == null || events.Count == 0) return;
        if (node is not (Button or CheckBox or Slider or TextBox or ComboBox)) return;

        var count = 0;
        foreach (var pair in events)
        {
            if (++count > SkinRules.MaxComponentEvents) break;

            var name = (pair.Key ?? string.Empty).Trim();
            if (!SkinRules.IsKnownComponentEvent(name)) continue;

            if (!SkinScriptEngine.TryParse(pair.Value, out var program, out var error))
            {
                if (Warned.Add($"event:{id}:{name}"))
                    LogService.Append($"美化包：组件 {id} 的 {name} 脚本无效（{error}），已跳过。");
                continue;
            }

            var isClick = name.Equals("click", StringComparison.OrdinalIgnoreCase);
            switch (node)
            {
                case Button button when isClick:
                    button.Click += (_, _) => RunScript(program);
                    break;

                case CheckBox check when !isClick:
                    check.Checked += (_, _) => RunChanged(definition, "true", program);
                    check.Unchecked += (_, _) => RunChanged(definition, "false", program);
                    break;

                case Slider slider when !isClick:
                    slider.ValueChanged += (_, _) => RunChanged(definition, FormatNumber(slider.Value), program);
                    break;

                case TextBox box when !isClick:
                    box.TextChanged += (_, _) => RunChanged(definition, box.Text ?? string.Empty, program);
                    break;

                case ComboBox combo when !isClick:
                    combo.SelectionChanged += (_, _) =>
                        RunChanged(definition, combo.SelectedItem as string ?? string.Empty, program);
                    break;
            }
        }
    }

    /// <summary>只执行事件脚本（Button 的 click，不回写值）。</summary>
    private static void RunScript(SkinScriptProgram program)
    {
        if (_refreshing || _handling) return;

        _handling = true;
        try
        {
            SkinScriptHost.Run(program);
        }
        finally
        {
            _handling = false;
        }
    }

    /// <summary>控件值变化：先回写（可选），再执行事件脚本；刷新绑定期间的赋值不触发。</summary>
    private static void RunChanged(SkinComponentDefinition definition, string value, SkinScriptProgram program)
    {
        if (_refreshing || _handling) return;

        _handling = true;
        try
        {
            WriteBack(definition, value);
            SkinScriptHost.Run(program);
        }
        finally
        {
            _handling = false;
        }
    }

    /// <summary>对单个节点重算全部 <c>bind</c>；非法颜色等值直接跳过。</summary>
    private static void ApplyEntry(ComponentEntry entry)
    {
        var host = SkinScriptHost.Instance;
        foreach (var pair in entry.Bindings)
        {
            var template = pair.Value;
            try
            {
                switch (pair.Key.ToLowerInvariant())
                {
                    case "text":
                        SetText(entry.Node, template.Render(host));
                        break;

                    case "value":
                    {
                        var expression = template.SingleExpression;
                        var value = expression != null
                            ? expression.Evaluate(host)
                            : SkinScriptValue.From(template.Render(host));
                        ApplyValue(entry.Node, value);
                        break;
                    }

                    case "visible":
                        entry.Node.Visibility = EvaluateBool(template, host)
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                        break;

                    case "enabled":
                        entry.Node.IsEnabled = EvaluateBool(template, host);
                        break;

                    case "background":
                    {
                        var brush = ResolveBrush(template.Render(host));
                        if (brush != null) SetBackground(entry.Node, brush);
                        break;
                    }

                    case "foreground":
                    {
                        var brush = ResolveBrush(template.Render(host));
                        if (brush != null) SetForeground(entry.Node, brush);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"美化包：组件 {entry.Id} 的绑定 {pair.Key} 求值失败", ex);
            }
        }
    }

    private static bool EvaluateBool(SkinBindingTemplate template, ISkinScriptHost host)
    {
        var expression = template.SingleExpression;
        return expression != null
            ? expression.Evaluate(host).AsBool()
            : SkinScriptValue.From(template.Render(host)).AsBool();
    }

    private static void SetText(FrameworkElement node, string text)
    {
        switch (node)
        {
            case TextBlock block:
                block.Text = text;
                break;

            case TextBox box:
                if (!string.Equals(box.Text, text, StringComparison.Ordinal)) box.Text = text;
                break;

            case ButtonBase button:
                if (!string.Equals(button.Content as string, text, StringComparison.Ordinal)) button.Content = text;
                break;

            case ContentControl content:
                content.Content = text;
                break;

            case Border border when border.Child is TextBlock child:
                child.Text = text;
                break;
        }
    }

    private static void ApplyValue(FrameworkElement node, SkinScriptValue value)
    {
        switch (node)
        {
            case ToggleButton toggle:
                toggle.IsChecked = value.AsBool();
                break;

            case Slider slider:
            {
                var number = value.AsNumber();
                if (double.IsNaN(number) || double.IsInfinity(number)) break;
                slider.Value = Math.Clamp(number, slider.Minimum, slider.Maximum);
                break;
            }

            case ProgressBar bar:
            {
                var number = value.AsNumber();
                if (double.IsNaN(number) || double.IsInfinity(number)) break;
                if (bar.Maximum <= 1 && number > 1 && number <= 100) number /= 100;
                bar.Value = Math.Clamp(number, bar.Minimum, bar.Maximum);
                break;
            }

            case ComboBox combo:
            {
                var text = value.AsText();
                if (text.Length == 0) break;
                foreach (var item in combo.Items)
                {
                    if (item == null || !string.Equals(item.ToString(), text, StringComparison.OrdinalIgnoreCase))
                        continue;
                    combo.SelectedItem = item;
                    break;
                }
                break;
            }
        }
    }

    private static void SetForeground(FrameworkElement element, Brush brush)
    {
        switch (element)
        {
            case TextBlock text:
                text.Foreground = brush;
                break;
            case Control control:
                control.Foreground = brush;
                break;
            case Border border when border.Child is TextBlock child:
                child.Foreground = brush;
                break;
        }
    }

    /// <summary>节点是否仍挂在窗口上（被移除的绑定会被淘汰）。</summary>
    private static bool IsAttached(FrameworkElement node)
    {
        if (node == null) return false;
        if (node.Parent != null) return true;
        return ParentOf(node) != null;
    }

    private static string FormatNumber(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool ParseBool(string raw, bool fallback)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return fallback;
        if (bool.TryParse(text, out var parsed)) return parsed;
        return text.ToLowerInvariant() switch
        {
            "1" or "on" or "yes" => true,
            "0" or "off" or "no" => false,
            _ => fallback,
        };
    }

    /// <summary>选项写法与设置项一致：逗号或竖线分隔。</summary>
    private static List<string> ParseOptions(string raw)
    {
        var options = new List<string>();
        foreach (var part in (raw ?? string.Empty).Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var text = part.Trim();
            if (text.Length == 0 || options.Contains(text)) continue;
            options.Add(text);
        }
        return options;
    }

    private static FrameworkElement? CreateWidget(
        SkinComponentDefinition definition, int depth, List<FrameworkElement> created,
        List<ComponentPart> parts)
    {
        WarnUnknownProps(definition);

        var widget = (definition.Widget ?? string.Empty).Trim();
        FrameworkElement element;

        switch (widget.ToLowerInvariant())
        {
            case "label":
            {
                var text = new TextBlock { Text = Prop(definition, "text") };
                ApplyTextProps(text, definition);
                ApplyBoxProps(text, definition);
                element = text;
                break;
            }

            case "badge":
            {
                var text = new TextBlock { Text = Prop(definition, "text") };
                ApplyTextProps(text, definition);
                if (!string.IsNullOrWhiteSpace(definition.Class))
                    SkinStyleApplier.SetSkinClasses(text, definition.Class);
                var border = new Border { Child = text };
                ApplyBoxProps(border, definition);
                created.Add(text);
                element = border;
                break;
            }

            case "divider":
            {
                var border = new Border
                {
                    Height = 1,
                    Margin = new Thickness(6, 0, 6, 0),
                    Background = ResolveBrush(Prop(definition, "border-color"))
                                 ?? Application.Current?.TryFindResource("BorderBrush") as Brush
                                 ?? new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                };
                ApplyBoxProps(border, definition);
                element = border;
                break;
            }

            case "spacer":
            {
                var spacer = new FrameworkElement { Width = 0, Height = 8 };
                ApplyBoxProps(spacer, definition);
                element = spacer;
                break;
            }

            case "image":
            {
                var image = new Image { Source = LoadImage(Prop(definition, "src")) };
                ApplyBoxProps(image, definition);
                element = image;
                break;
            }

            case "icon":
            {
                var text = new TextBlock
                {
                    Text = Prop(definition, "glyph"),
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                };
                ApplyTextProps(text, definition);
                ApplyBoxProps(text, definition);
                element = text;
                break;
            }

            case "progressbar":
            {
                var bar = new ProgressBar { Maximum = 1, Height = 6 };
                if (TryDouble(Prop(definition, "value"), out var value))
                {
                    if (value > 1 && value <= 100) value /= 100;
                    bar.Value = Math.Clamp(value, 0, 1);
                }
                ApplyBoxProps(bar, definition);
                element = bar;
                break;
            }

            case "card":
            {
                var panel = new StackPanel();
                var border = new Border { Child = panel };
                ApplyBoxProps(border, definition);
                AddChildren(panel, definition, depth, created, parts);
                created.Add(panel);
                element = border;
                break;
            }

            case "row":
            case "column":
            {
                var panel = new StackPanel
                {
                    Orientation = widget.Equals("row", StringComparison.OrdinalIgnoreCase)
                        ? Orientation.Horizontal
                        : Orientation.Vertical,
                };
                ApplyBoxProps(panel, definition);
                AddChildren(panel, definition, depth, created, parts);
                element = panel;
                break;
            }

            case "banner":
            {
                var text = new TextBlock { Text = Prop(definition, "text") };
                ApplyTextProps(text, definition);
                if (!string.IsNullOrWhiteSpace(definition.Class))
                    SkinStyleApplier.SetSkinClasses(text, definition.Class);
                var border = new Border
                {
                    Child = text,
                    Padding = new Thickness(10, 6, 10, 6),
                };
                ApplyBoxProps(border, definition);
                created.Add(text);
                element = border;
                break;
            }

            case "button":
            {
                var button = new Button { Content = Prop(definition, "text") };
                ApplyControlProps(button, definition);
                ApplyBoxProps(button, definition);
                element = button;
                break;
            }

            case "toggle":
            {
                var check = new CheckBox
                {
                    Content = Prop(definition, "text"),
                    IsChecked = ParseBool(Prop(definition, "checked"), false),
                };
                ApplyControlProps(check, definition);
                ApplyBoxProps(check, definition);
                element = check;
                break;
            }

            case "slider":
            {
                var min = TryDouble(Prop(definition, "min"), out var parsedMin) ? parsedMin : 0;
                var max = TryDouble(Prop(definition, "max"), out var parsedMax) ? parsedMax : 100;
                if (max <= min) max = min + 100;
                var step = TryDouble(Prop(definition, "step"), out var parsedStep) && parsedStep > 0 ? parsedStep : 1;
                var value = TryDouble(Prop(definition, "value"), out var parsedValue) ? parsedValue : 0;

                var slider = new Slider
                {
                    Minimum = min,
                    Maximum = max,
                    TickFrequency = step,
                    SmallChange = step,
                    LargeChange = step * 5,
                    IsSnapToTickEnabled = true,
                    Value = Math.Clamp(value, min, max),
                };
                ApplyBoxProps(slider, definition);
                element = slider;
                break;
            }

            case "input":
            {
                var box = new TextBox { Text = Prop(definition, "text") };
                ApplyControlProps(box, definition);
                ApplyBoxProps(box, definition);
                element = box;
                break;
            }

            case "select":
            {
                var combo = new ComboBox();
                var options = ParseOptions(Prop(definition, "options"));
                combo.ItemsSource = options;

                var wanted = Prop(definition, "value");
                if (wanted.Length == 0) wanted = Prop(definition, "text");
                var selected = options.FirstOrDefault(item =>
                                   item.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                               ?? (options.Count > 0 ? options[0] : null);
                if (selected != null) combo.SelectedItem = selected;

                ApplyBoxProps(combo, definition);
                element = combo;
                break;
            }

            default:
                WarnOnce("widget:" + widget, $"组件 {definition.Id} 的 widget 不支持：{widget}，已忽略。");
                return null;
        }

        // 嵌套组件也要能按 id 定位、参与绑定与事件
        parts.Add(new ComponentPart { Definition = definition, Node = element });
        created.Add(element);
        return element;
    }

    private static void AddChildren(
        Panel panel, SkinComponentDefinition definition, int depth, List<FrameworkElement> created,
        List<ComponentPart> parts)
    {
        if (definition.Children == null || definition.Children.Count == 0) return;
        if (depth >= SkinRules.MaxComponentDepth)
        {
            WarnOnce("depth:" + definition.Id,
                $"组件 {definition.Id} 的子组件超过嵌套上限 {SkinRules.MaxComponentDepth}，已忽略。");
            return;
        }

        foreach (var child in definition.Children)
        {
            if (child == null) continue;
            if (!SkinRules.IsKnownComponentWidget(child.Widget)) continue;

            var node = CreateWidget(child, depth + 1, created, parts);
            if (node == null) continue;

            if (!string.IsNullOrWhiteSpace(child.Class))
                SkinStyleApplier.SetSkinClasses(node, child.Class);
            node.Tag = TagPrefix + (child.Id ?? string.Empty).Trim();
            panel.Children.Add(node);
        }
    }

    private static bool HostMatches(FrameworkElement element, string host)
    {
        var text = (host ?? string.Empty).Trim();
        if (text.StartsWith('#'))
            return string.Equals(element.Name, text[1..], StringComparison.OrdinalIgnoreCase);

        var dot = text.IndexOf('.');
        var typeName = dot < 0 ? text : text[..dot];
        if (!TypeMatches(element.GetType(), typeName)) return false;
        return dot < 0 || SkinStyleApplier.HasSkinClass(element, text[(dot + 1)..]);
    }

    private static bool TypeMatches(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsComponentNode(FrameworkElement element)
    {
        var current = (DependencyObject?)element;
        for (var guard = 0; current != null && guard < 256; guard++)
        {
            if (current is FrameworkElement node && node.Tag is string tag
                && tag.StartsWith(TagPrefix, StringComparison.Ordinal))
                return true;
            current = ParentOf(current);
        }
        return false;
    }

    private static bool HasVirtualizingAncestor(DependencyObject node)
    {
        var current = node;
        for (var guard = 0; current != null && guard < 256; guard++)
        {
            if (current is VirtualizingPanel) return true;
            if (current is ItemsControl items && VirtualizingPanel.GetIsVirtualizing(items)) return true;
            current = ParentOf(current);
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
    {
        try
        {
            var parent = LogicalTreeHelper.GetParent(node);
            if (parent != null) return parent;
        }
        catch
        {
            // 非逻辑树元素时继续尝试视觉父级
        }

        return node is Visual ? VisualTreeHelper.GetParent(node) : null;
    }

    private static void ApplyBoxProps(FrameworkElement element, SkinComponentDefinition definition)
    {
        var background = ResolveBrush(Prop(definition, "background"));
        if (background != null) SetBackground(element, background);

        if (element is Border border)
        {
            var borderBrush = ResolveBrush(Prop(definition, "border-color"));
            if (borderBrush != null) border.BorderBrush = borderBrush;

            if (TryLength(Prop(definition, "corner-radius"), out var radius))
                border.CornerRadius = new CornerRadius(radius);
            if (TryThickness(Prop(definition, "padding"), out var padding))
                border.Padding = padding;
        }
        else if (element is Control control)
        {
            if (TryThickness(Prop(definition, "padding"), out var padding))
                control.Padding = padding;
        }

        if (TryThickness(Prop(definition, "margin"), out var margin)) element.Margin = margin;
        if (TryLength(Prop(definition, "width"), out var width)) element.Width = width;
        if (TryLength(Prop(definition, "height"), out var height)) element.Height = height;
        if (TryDouble(Prop(definition, "opacity"), out var opacity))
            element.Opacity = Math.Clamp(opacity, 0, 1);
    }

    private static void ApplyTextProps(TextBlock text, SkinComponentDefinition definition)
    {
        var foreground = ResolveBrush(Prop(definition, "foreground"));
        if (foreground != null) text.Foreground = foreground;

        if (TryLength(Prop(definition, "font-size"), out var size)) text.FontSize = size;
        var weight = Prop(definition, "font-weight");
        if (weight.Length > 0) text.FontWeight = ParseFontWeight(weight);
    }

    private static void ApplyControlProps(Control control, SkinComponentDefinition definition)
    {
        var foreground = ResolveBrush(Prop(definition, "foreground"));
        if (foreground != null) control.Foreground = foreground;
        if (TryLength(Prop(definition, "font-size"), out var size)) control.FontSize = size;
    }

    private static void SetBackground(FrameworkElement element, Brush brush)
    {
        switch (element)
        {
            case Panel panel:
                panel.Background = brush;
                break;
            case Border border:
                border.Background = brush;
                break;
            case Control control:
                control.Background = brush;
                break;
        }
    }

    private static void WarnUnknownProps(SkinComponentDefinition definition)
    {
        if (definition.Props == null || definition.Props.Count == 0) return;

        var count = 0;
        foreach (var pair in definition.Props)
        {
            count++;
            if (count > SkinRules.MaxComponentProps)
            {
                WarnOnce("props-count:" + definition.Id,
                    $"组件 {definition.Id} 的 props 超过上限 {SkinRules.MaxComponentProps}，超出部分将被忽略。");
                break;
            }

            var key = (pair.Key ?? string.Empty).Trim();
            if (key.Length == 0 || KnownProps.Contains(key)) continue;
            WarnOnce($"prop:{definition.Widget}:{key}",
                $"组件 {definition.Id} 的 props 不支持：{key}，已忽略。");
        }
    }

    private static string Prop(SkinComponentDefinition definition, string name)
    {
        if (definition.Props == null || definition.Props.Count == 0) return string.Empty;
        if (definition.Props.TryGetValue(name, out var exact)) return (exact ?? string.Empty).Trim();

        foreach (var pair in definition.Props)
            if ((pair.Key ?? string.Empty).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return (pair.Value ?? string.Empty).Trim();
        return string.Empty;
    }

    private static Brush? ResolveBrush(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = SkinStyleApplier.ResolveSkinValue(raw).Trim();
        return SkinColors.TryParse(text, out var color) ? SkinColors.ToBrush(color) : null;
    }

    private static bool TryLength(string raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = SkinStyleApplier.ResolveSkinValue(raw).Trim().ToLowerInvariant();
        if (text.Length == 0) return false;

        var scale = 1.0;
        if (text.EndsWith("px")) text = text[..^2];
        else if (text.EndsWith("pt"))
        {
            text = text[..^2];
            scale = 96.0 / 72.0;
        }
        else if (text.EndsWith("rem")) text = text[..^3];
        else if (text.EndsWith("em")) text = text[..^2];

        if (text.EndsWith('%')) return false;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
        value = number * scale;
        return true;
    }

    private static bool TryDouble(string raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = SkinStyleApplier.ResolveSkinValue(raw).Trim();
        var percent = text.EndsWith('%');
        if (percent) text = text[..^1];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
        value = percent ? number / 100 : number;
        return true;
    }

    private static bool TryThickness(string raw, out Thickness thickness)
    {
        thickness = new Thickness(0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = SkinStyleApplier.ResolveSkinValue(raw);
        var parts = text.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!TryLength(parts[i], out values[i])) return false;

        thickness = values.Length switch
        {
            1 => new Thickness(values[0]),
            2 => new Thickness(values[1], values[0], values[1], values[0]),
            3 => new Thickness(values[1], values[0], values[1], values[2]),
            _ => new Thickness(values[3], values[0], values[1], values[2]),
        };
        return true;
    }

    private static FontWeight ParseFontWeight(string raw)
    {
        var text = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            if (number >= 700) return FontWeights.Bold;
            if (number >= 600) return FontWeights.SemiBold;
            if (number >= 500) return FontWeights.Medium;
            return FontWeights.Normal;
        }

        return text switch
        {
            "bold" or "bolder" => FontWeights.Bold,
            "semibold" or "demi" => FontWeights.SemiBold,
            "medium" => FontWeights.Medium,
            "light" => FontWeights.Light,
            "lighter" => FontWeights.Light,
            _ => FontWeights.Normal,
        };
    }

    private static ImageSource? LoadImage(string relative)
    {
        var text = (relative ?? string.Empty).Trim();
        if (text.Length == 0 || !SkinRules.IsSafeRelativePath(text)) return null;

        var dir = SkinService.SkinDirectory;
        if (dir.Length == 0) return null;

        try
        {
            var file = Path.Combine(dir, text.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) return null;
            if (SkinSvgRenderer.IsSvg(file))
                return SkinSvgRenderer.TryLoadImageSource(file, 512, out var svg) ? svg : null;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            LogService.Error("读取声明式组件图片失败", ex);
            return null;
        }
    }

    private static void WarnOnce(string key, string message)
    {
        if (!Warned.Add(key)) return;
        LogService.Warn(message);
    }
}
