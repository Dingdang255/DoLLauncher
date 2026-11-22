using System.Globalization;
using DoLLauncher.Core.Skin;
using DoLLauncher.Services;
using Microsoft.Maui.Controls.Shapes;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// dolskin/3 声明式组件的 MAUI 实现：按 <c>components[]</c> 把控件插入页面插槽。
/// 交互控件可挂 <c>events</c> 轻量脚本（经 <see cref="AndroidSkinScriptHost"/> 白名单执行），
/// <c>bind</c> 声明按 <c>{{ 表达式 }}</c> 实时求值；卸载时移除全部插入节点。
/// </summary>
public static class MauiSkinComponentService
{
    private sealed class Insertion
    {
        public Layout Parent { get; init; } = null!;
        public View Node { get; init; } = null!;
    }

    /// <summary>组件树里的一个节点：Root 是插入可视树的控件，Text/Value 指向绑定与事件的实际目标。</summary>
    private sealed class WidgetNode
    {
        public View Root { get; init; } = null!;
        public Layout? Container { get; init; }
        public VisualElement? TextTarget { get; init; }
        public VisualElement? ValueTarget { get; init; }
    }

    private sealed class ComponentEntry
    {
        public VisualElement Node { get; init; } = null!;
        public VisualElement? TextTarget { get; init; }
        public VisualElement? ValueTarget { get; init; }
        public string Id { get; init; } = string.Empty;
        public Dictionary<string, SkinBindingTemplate> Bindings { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly List<Insertion> Inserted = new();
    private static readonly Dictionary<(VisualElement Host, string Id), View> Registry = new();
    private static readonly Dictionary<string, VisualElement> ComponentNodes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ComponentEntry> Bound = new();
    private static readonly HashSet<VisualElement> Owned = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>CSS 元素名 → MAUI 类型名（含 WPF 习惯写法）。</summary>
    private static readonly Dictionary<string, string[]> HostAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stackpanel"] = new[] { "VerticalStackLayout", "HorizontalStackLayout", "StackLayout" },
        ["textblock"] = new[] { "Label" },
        ["textbox"] = new[] { "Entry", "Editor" },
        ["combobox"] = new[] { "Picker" },
        ["checkbox"] = new[] { "CheckBox" },
        ["checkboxitem"] = new[] { "CheckBox" },
        ["frame"] = new[] { "Border" },
    };

    private static readonly HashSet<string> KnownProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "glyph", "value", "src",
        "background", "foreground", "border-color", "corner-radius",
        "padding", "margin", "width", "height", "font-size", "font-weight", "opacity",
        "checked", "min", "max", "step", "options", "bindTo",
    };

    private static bool _refreshing;
    private static bool _handling;

    /// <summary>当前已插入的顶层组件数量。</summary>
    public static int InsertedCount => Inserted.Count;

    /// <summary>遍历页面可视树，为命中的 host 插入组件（幂等）。</summary>
    public static void Apply(Page? page)
    {
        var components = AndroidSkinStore.Current?.Components;
        if (page == null || components == null || components.Count == 0) return;

        foreach (var element in Walk(page).OfType<VisualElement>().ToList()) TryApplyHost(page, element);
    }

    /// <summary>按组件 id 找到已插入的节点；未找到时返回 null。</summary>
    public static VisualElement? FindComponent(string id)
    {
        var key = (id ?? string.Empty).Trim();
        if (key.Length == 0) return null;
        return ComponentNodes.TryGetValue(key, out var node) ? node : null;
    }

    /// <summary>重新求值所有仍存活的 <c>bind</c> 声明。</summary>
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

        foreach (var element in Owned)
        {
            try
            {
                MauiSkinAnimationRunner.Detach(element);
            }
            catch
            {
                // 元素已销毁时忽略
            }
        }

        Inserted.Clear();
        Registry.Clear();
        ComponentNodes.Clear();
        Bound.Clear();
        Owned.Clear();
        Warned.Clear();
        _refreshing = false;
        _handling = false;
    }

    // ---------- 插入 ----------

    private static void TryApplyHost(Page page, VisualElement element)
    {
        if (element == null || Owned.Contains(element)) return;

        var components = AndroidSkinStore.Current?.Components;
        if (components == null || components.Count == 0) return;

        var limit = Math.Min(components.Count, SkinRules.MaxComponents);
        for (var i = 0; i < limit; i++)
        {
            try
            {
                InsertOne(page, element, components[i]);
            }
            catch (Exception ex)
            {
                LogService.Error("插入声明式组件失败", ex);
            }
        }
    }

    private static void InsertOne(Page page, VisualElement host, SkinComponentDefinition? definition)
    {
        if (definition == null) return;

        var id = (definition.Id ?? string.Empty).Trim();
        if (id.Length == 0) return;
        if (!SkinRules.IsKnownComponentWidget(definition.Widget)) return;
        if (!SkinRules.IsValidComponentSlot(definition.Slot)) return;
        if (!SkinRules.IsValidComponentHost(definition.Host)) return;
        if (Registry.ContainsKey((host, id))) return;
        if (!HostMatches(page, host, definition.Host)) return;

        if (HasVirtualizingAncestor(host))
        {
            WarnOnce("virtual:" + id, $"组件 {id} 的宿主位于虚拟化列表内，已跳过插入。");
            return;
        }

        if (definition.Props is { Count: > SkinRules.MaxComponentProps })
            WarnOnce("props:" + id, $"组件 {id} 的 props 超过上限 {SkinRules.MaxComponentProps}，超出部分将被忽略。");

        var slot = (definition.Slot ?? "end").Trim().ToLowerInvariant();
        Layout parent;
        int index;
        if (slot is "before" or "after")
        {
            if (host.Parent is not Layout hostParent) return;
            var at = hostParent.Children.IndexOf(host);
            if (at < 0) return;
            parent = hostParent;
            index = slot == "after" ? at + 1 : at;
        }
        else
        {
            if (host is not Layout panel) return;
            parent = panel;
            index = slot == "start" ? 0 : panel.Children.Count;
        }

        var parts = new List<(SkinComponentDefinition Definition, WidgetNode Node)>();
        var node = CreateWidget(definition, 0, parts);
        if (node == null) return;

        if (!string.IsNullOrWhiteSpace(definition.Class))
            SetSkinClasses(node.Root, definition.Class);

        parent.Children.Insert(index, node.Root);
        Registry[(host, id)] = node.Root;
        Inserted.Add(new Insertion { Parent = parent, Node = node.Root });
        Owned.Add(node.Root);

        MauiSkinStyler.ApplyTree(node.Root);

        foreach (var part in parts) RegisterComponent(part.Definition, part.Node);
    }

    private static void RegisterComponent(SkinComponentDefinition definition, WidgetNode node)
    {
        var id = (definition.Id ?? string.Empty).Trim();
        if (id.Length > 0) ComponentNodes.TryAdd(id, node.Root);

        var entry = BuildEntry(definition, node, id);
        if (entry != null)
        {
            Bound.Add(entry);
            ApplyEntry(entry);
        }

        AttachEvents(definition, node, id);
    }

    private static ComponentEntry? BuildEntry(SkinComponentDefinition definition, WidgetNode node, string id)
    {
        var bindings = definition.Bind;
        if (bindings == null || bindings.Count == 0) return null;

        var entry = new ComponentEntry
        {
            Node = node.Root,
            TextTarget = node.TextTarget,
            ValueTarget = node.ValueTarget,
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

    // ---------- 控件创建 ----------

    private static WidgetNode? CreateWidget(SkinComponentDefinition definition, int depth,
        List<(SkinComponentDefinition, WidgetNode)> parts)
    {
        WarnUnknownProps(definition);

        var widget = (definition.Widget ?? string.Empty).Trim().ToLowerInvariant();
        WidgetNode node;

        switch (widget)
        {
            case "label":
            {
                var label = new Label { Text = Prop(definition, "text") };
                ApplyTextProps(label, definition);
                ApplyBoxProps(label, definition);
                node = new WidgetNode { Root = label, TextTarget = label };
                break;
            }

            case "badge":
            {
                var label = new Label { Text = Prop(definition, "text") };
                ApplyTextProps(label, definition);
                var border = new Border
                {
                    Content = label,
                    Padding = new Thickness(8, 2),
                };
                ApplyBorderProps(border, definition);
                ApplyBoxProps(border, definition);
                node = new WidgetNode { Root = border, TextTarget = label };
                break;
            }

            case "divider":
            {
                var box = new BoxView
                {
                    HeightRequest = 1,
                    Color = ResolveColor(Prop(definition, "border-color")) ?? Color.FromArgb("#40808080"),
                    Margin = new Thickness(6, 0),
                };
                ApplyBoxProps(box, definition);
                node = new WidgetNode { Root = box };
                break;
            }

            case "spacer":
            {
                var box = new BoxView
                {
                    HeightRequest = 8,
                    WidthRequest = 0,
                    Color = Colors.Transparent,
                };
                ApplyBoxProps(box, definition);
                node = new WidgetNode { Root = box };
                break;
            }

            case "image":
            {
                var image = new Image
                {
                    Source = LoadImage(Prop(definition, "src")),
                    Aspect = Aspect.AspectFit,
                };
                ApplyBoxProps(image, definition);
                node = new WidgetNode { Root = image };
                break;
            }

            case "icon":
            {
                var label = new Label
                {
                    Text = Prop(definition, "glyph"),
                    FontFamily = "FluentIcons",
                };
                ApplyTextProps(label, definition);
                ApplyBoxProps(label, definition);
                node = new WidgetNode { Root = label, TextTarget = label };
                break;
            }

            case "progressbar":
            {
                var bar = new ProgressBar { HeightRequest = 6, Progress = 0 };
                if (TryDouble(Prop(definition, "value"), out var value))
                {
                    if (value > 1 && value <= 100) value /= 100;
                    bar.Progress = Math.Clamp(value, 0, 1);
                }
                ApplyBoxProps(bar, definition);
                node = new WidgetNode { Root = bar, ValueTarget = bar };
                break;
            }

            case "card":
            {
                var stack = new VerticalStackLayout { Spacing = 6 };
                var border = new Border { Content = stack };
                ApplyBorderProps(border, definition);
                ApplyBoxProps(border, definition);
                AddChildren(stack, definition, depth, parts);
                node = new WidgetNode { Root = border, Container = stack };
                break;
            }

            case "row":
            case "column":
            {
                Layout layout = widget == "row"
                    ? new HorizontalStackLayout { Spacing = 6 }
                    : new VerticalStackLayout { Spacing = 6 };
                ApplyBoxProps(layout, definition);
                AddChildren(layout, definition, depth, parts);
                node = new WidgetNode { Root = (View)layout, Container = layout };
                break;
            }

            case "banner":
            {
                var label = new Label { Text = Prop(definition, "text") };
                ApplyTextProps(label, definition);
                var border = new Border
                {
                    Content = label,
                    Padding = new Thickness(10, 6),
                };
                ApplyBorderProps(border, definition);
                ApplyBoxProps(border, definition);
                node = new WidgetNode { Root = border, TextTarget = label };
                break;
            }

            case "button":
            {
                var button = new Button { Text = Prop(definition, "text") };
                ApplyTextProps(button, definition);
                ApplyBorderProps(button, definition);
                ApplyBoxProps(button, definition);
                node = new WidgetNode { Root = button, TextTarget = button, ValueTarget = button };
                break;
            }

            case "toggle":
            {
                var label = new Label { Text = Prop(definition, "text"), VerticalOptions = LayoutOptions.Center };
                ApplyTextProps(label, definition);
                var toggle = new Switch
                {
                    IsToggled = ParseBool(Prop(definition, "checked"), false),
                    VerticalOptions = LayoutOptions.Center,
                };
                var row = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center };
                row.Children.Add(label);
                row.Children.Add(toggle);
                ApplyBoxProps(row, definition);
                node = new WidgetNode { Root = row, TextTarget = label, ValueTarget = toggle };
                break;
            }

            case "slider":
            {
                var min = TryDouble(Prop(definition, "min"), out var parsedMin) ? parsedMin : 0;
                var max = TryDouble(Prop(definition, "max"), out var parsedMax) ? parsedMax : 100;
                if (max <= min) max = min + 100;
                var value = TryDouble(Prop(definition, "value"), out var parsedValue) ? parsedValue : min;

                if (Prop(definition, "step").Length > 0)
                    WarnOnce("step:" + definition.Id, "安卓端 Slider 不支持 step，已按连续值处理。");

                var slider = new Slider
                {
                    Minimum = min,
                    Maximum = max,
                    Value = Math.Clamp(value, min, max),
                };
                ApplyBoxProps(slider, definition);
                node = new WidgetNode { Root = slider, ValueTarget = slider };
                break;
            }

            case "input":
            {
                var entry = new Entry { Text = Prop(definition, "text") };
                ApplyTextProps(entry, definition);
                ApplyBoxProps(entry, definition);
                node = new WidgetNode { Root = entry, TextTarget = entry, ValueTarget = entry };
                break;
            }

            case "select":
            {
                var picker = new Picker();
                foreach (var option in ParseOptions(Prop(definition, "options"))) picker.Items.Add(option);

                var wanted = Prop(definition, "value");
                if (wanted.Length == 0) wanted = Prop(definition, "text");
                var index = -1;
                for (var i = 0; i < picker.Items.Count; i++)
                {
                    if (!string.Equals(picker.Items[i], wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    index = i;
                    break;
                }
                if (index < 0 && picker.Items.Count > 0) index = 0;
                picker.SelectedIndex = index;

                ApplyTextProps(picker, definition);
                ApplyBoxProps(picker, definition);
                node = new WidgetNode { Root = picker, ValueTarget = picker };
                break;
            }

            default:
                WarnOnce("widget:" + widget, $"组件 {definition.Id} 的 widget 不支持：{widget}，已忽略。");
                return null;
        }

        parts.Add((definition, node));
        return node;
    }

    private static void AddChildren(Layout container, SkinComponentDefinition definition, int depth,
        List<(SkinComponentDefinition, WidgetNode)> parts)
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

            var node = CreateWidget(child, depth + 1, parts);
            if (node == null) continue;

            if (!string.IsNullOrWhiteSpace(child.Class)) SetSkinClasses(node.Root, child.Class);
            container.Children.Add(node.Root);
            Owned.Add(node.Root);
        }
    }

    // ---------- 绑定 ----------

    private static void ApplyEntry(ComponentEntry entry)
    {
        var host = AndroidSkinScriptHost.Instance;
        foreach (var pair in entry.Bindings)
        {
            var template = pair.Value;
            try
            {
                switch (pair.Key.ToLowerInvariant())
                {
                    case "text":
                        SetText(entry.TextTarget ?? entry.Node, template.Render(host));
                        break;

                    case "value":
                    {
                        var expression = template.SingleExpression;
                        var value = expression != null
                            ? expression.Evaluate(host)
                            : SkinScriptValue.From(template.Render(host));
                        ApplyValue(entry.ValueTarget ?? entry.Node, value);
                        break;
                    }

                    case "visible":
                        entry.Node.IsVisible = EvaluateBool(template, host);
                        break;

                    case "enabled":
                        entry.Node.IsEnabled = EvaluateBool(template, host);
                        break;

                    case "background":
                    {
                        var color = ResolveColor(template.Render(host));
                        if (color != null) entry.Node.BackgroundColor = color;
                        break;
                    }

                    case "foreground":
                    {
                        var color = ResolveColor(template.Render(host));
                        if (color != null) ApplyForeground(entry.TextTarget ?? entry.Node, color);
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

    private static void SetText(VisualElement target, string text)
    {
        switch (target)
        {
            case Label label:
                label.Text = text;
                break;
            case Button button:
                if (!string.Equals(button.Text, text, StringComparison.Ordinal)) button.Text = text;
                break;
            case Entry entry:
                if (!string.Equals(entry.Text, text, StringComparison.Ordinal)) entry.Text = text;
                break;
            case Editor editor:
                if (!string.Equals(editor.Text, text, StringComparison.Ordinal)) editor.Text = text;
                break;
            case Picker picker:
                if (!string.Equals(picker.Title, text, StringComparison.Ordinal)) picker.Title = text;
                break;
        }
    }

    private static void ApplyValue(VisualElement target, SkinScriptValue value)
    {
        switch (target)
        {
            case Switch toggle:
                toggle.IsToggled = value.AsBool();
                break;

            case CheckBox check:
                check.IsChecked = value.AsBool();
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
                if (number > 1 && number <= 100) number /= 100;
                bar.Progress = Math.Clamp(number, 0, 1);
                break;
            }

            case Picker picker:
            {
                var text = value.AsText();
                if (text.Length == 0) break;
                for (var i = 0; i < picker.Items.Count; i++)
                {
                    if (!string.Equals(picker.Items[i], text, StringComparison.OrdinalIgnoreCase)) continue;
                    picker.SelectedIndex = i;
                    break;
                }
                break;
            }

            case Entry entry:
            {
                var text = value.AsText();
                if (!string.Equals(entry.Text, text, StringComparison.Ordinal)) entry.Text = text;
                break;
            }

            case Label label:
                label.Text = value.AsText();
                break;
        }
    }

    // ---------- 事件 ----------

    private static void AttachEvents(SkinComponentDefinition definition, WidgetNode node, string id)
    {
        var events = definition.Events;
        if (events == null || events.Count == 0) return;

        var target = node.ValueTarget ?? node.Root;
        var count = 0;
        foreach (var pair in events)
        {
            if (++count > SkinRules.MaxComponentEvents) break;

            var name = (pair.Key ?? string.Empty).Trim();
            if (!SkinRules.IsKnownComponentEvent(name)) continue;

            if (!SkinScriptEngine.TryParse(pair.Value, out var program, out var error))
            {
                WarnOnce($"event:{id}:{name}", $"组件 {id} 的 {name} 脚本无效（{error}），已跳过。");
                continue;
            }

            var isClick = name.Equals("click", StringComparison.OrdinalIgnoreCase);
            switch (target)
            {
                case Button button when isClick:
                    button.Clicked += (_, _) => RunScript(program);
                    break;

                case Switch toggle when !isClick:
                    toggle.Toggled += (_, e) => RunChanged(definition, e.Value ? "true" : "false", program);
                    break;

                case CheckBox check when !isClick:
                    check.CheckedChanged += (_, e) => RunChanged(definition, e.Value ? "true" : "false", program);
                    break;

                case Slider slider when !isClick:
                    slider.ValueChanged += (_, e) => RunChanged(definition, FormatNumber(e.NewValue), program);
                    break;

                case Entry entryInput when !isClick:
                    entryInput.TextChanged += (_, e) => RunChanged(definition, e.NewTextValue ?? string.Empty, program);
                    break;

                case Picker picker when !isClick:
                    picker.SelectedIndexChanged += (_, _) =>
                        RunChanged(definition, picker.SelectedItem as string ?? string.Empty, program);
                    break;
            }
        }
    }

    private static void RunScript(SkinScriptProgram program)
    {
        if (_refreshing || _handling) return;

        _handling = true;
        try
        {
            AndroidSkinScriptHost.Run(program);
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
            AndroidSkinScriptHost.Run(program);
        }
        finally
        {
            _handling = false;
        }
    }

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
            AndroidSkinScriptHost.SetSetting(name, value);
        }
        else if (kind == "vars")
        {
            AndroidSkinScriptHost.SetVariable(name, value);
            AndroidSkinScriptHost.NotifyBindingsChanged();
        }
    }

    // ---------- 宿主匹配 ----------

    private static bool HostMatches(Page page, VisualElement element, string host)
    {
        var text = (host ?? string.Empty).Trim();
        if (text.StartsWith('#'))
        {
            var name = text[1..];
            if (string.Equals(element.StyleId, name, StringComparison.OrdinalIgnoreCase)) return true;

            var found = page.FindByName<VisualElement>(name);
            return found != null && ReferenceEquals(found, element);
        }

        var dot = text.IndexOf('.');
        var typeName = dot < 0 ? text : text[..dot];
        if (!TypeMatches(element.GetType(), typeName)) return false;
        if (dot < 0) return true;

        return HasSkinClass(element, text[(dot + 1)..]);
    }

    private static bool TypeMatches(Type type, string name)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            names.Add(current.Name);

        if (HostAliases.TryGetValue(name, out var targets))
        {
            foreach (var target in targets)
                if (names.Contains(target)) return true;
            return false;
        }

        return names.Contains(name);
    }

    private static bool HasVirtualizingAncestor(Element element)
    {
        var current = element.Parent;
        for (var guard = 0; current != null && guard < 256; guard++)
        {
            if (current is ItemsView) return true;
            current = current.Parent;
        }
        return false;
    }

    private static bool IsAttached(VisualElement node)
    {
        if (node == null) return false;
        return node.Parent != null;
    }

    private static IEnumerable<Element> Walk(Element root)
    {
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Element>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            yield return node;

            if (node is not IVisualTreeElement tree) continue;
            foreach (var child in tree.GetVisualChildren())
                if (child is Element childElement) stack.Push(childElement);
        }
    }

    // ---------- 属性 ----------

    private static void ApplyTextProps(VisualElement element, SkinComponentDefinition definition)
    {
        var foreground = ResolveColor(Prop(definition, "foreground"));
        if (foreground != null) ApplyForeground(element, foreground);

        if (MauiSkinStyler.TryParseLength(Prop(definition, "font-size"), out var size))
        {
            switch (element)
            {
                case Label label: label.FontSize = size; break;
                case Button button: button.FontSize = size; break;
                case Entry entry: entry.FontSize = size; break;
                case Editor editor: editor.FontSize = size; break;
                case Picker picker: picker.FontSize = size; break;
            }
        }

        var weight = Prop(definition, "font-weight");
        if (weight.Length > 0) ApplyFontWeight(element, weight);
    }

    private static void ApplyFontWeight(VisualElement element, string raw)
    {
        var text = (raw ?? string.Empty).Trim().ToLowerInvariant();
        var bold = text.Contains("bold", StringComparison.Ordinal) || text is "600" or "700" or "800" or "900";

        var attributes = element switch
        {
            Label label => label.FontAttributes,
            Button button => button.FontAttributes,
            Entry entry => entry.FontAttributes,
            Editor editor => editor.FontAttributes,
            _ => FontAttributes.None,
        };
        var next = bold ? attributes | FontAttributes.Bold : attributes & ~FontAttributes.Bold;

        switch (element)
        {
            case Label label: label.FontAttributes = next; break;
            case Button button: button.FontAttributes = next; break;
            case Entry entry: entry.FontAttributes = next; break;
            case Editor editor: editor.FontAttributes = next; break;
        }
    }

    private static void ApplyForeground(VisualElement element, Color color)
    {
        switch (element)
        {
            case Label label: label.TextColor = color; break;
            case Button button: button.TextColor = color; break;
            case Entry entry: entry.TextColor = color; break;
            case Editor editor: editor.TextColor = color; break;
            case Picker picker: picker.TextColor = color; break;
        }
    }

    private static void ApplyBorderProps(Border border, SkinComponentDefinition definition)
    {
        var background = ResolveColor(Prop(definition, "background"));
        if (background != null) border.BackgroundColor = background;

        var stroke = ResolveColor(Prop(definition, "border-color"));
        if (stroke != null)
        {
            border.Stroke = new SolidColorBrush(stroke);
            if (border.StrokeThickness <= 0) border.StrokeThickness = 1;
        }

        if (MauiSkinStyler.TryParseLength(Prop(definition, "corner-radius"), out var radius))
            border.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(radius) };

        if (MauiSkinStyler.TryParseThickness(Prop(definition, "padding"), out var padding))
            border.Padding = padding;
    }

    private static void ApplyBorderProps(Button button, SkinComponentDefinition definition)
    {
        var stroke = ResolveColor(Prop(definition, "border-color"));
        if (stroke != null && button.BorderWidth <= 0) button.BorderWidth = 1;
        if (stroke != null) button.BorderColor = stroke;

        if (MauiSkinStyler.TryParseLength(Prop(definition, "corner-radius"), out var radius))
            button.CornerRadius = (int)Math.Round(radius);
    }

    private static void ApplyBoxProps(View element, SkinComponentDefinition definition)
    {
        var background = ResolveColor(Prop(definition, "background"));
        if (background != null) element.BackgroundColor = background;

        if (MauiSkinStyler.TryParseThickness(Prop(definition, "margin"), out var margin)) element.Margin = margin;
        if (MauiSkinStyler.TryParseLength(Prop(definition, "width"), out var width)) element.WidthRequest = width;
        if (MauiSkinStyler.TryParseLength(Prop(definition, "height"), out var height)) element.HeightRequest = height;

        if (double.TryParse(Prop(definition, "opacity"), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var opacity))
            element.Opacity = Math.Clamp(opacity, 0, 1);

        if (MauiSkinStyler.TryParseThickness(Prop(definition, "padding"), out var padding))
            switch (element)
            {
                case Border border: border.Padding = padding; break;
                case Button button: button.Padding = padding; break;
                case Layout layout: layout.Padding = padding; break;
            }
    }

    /// <summary>CSS 样式类写入 StyleClass；卸载时随节点移除一起失效。</summary>
    internal static void SetSkinClasses(VisualElement element, string classes)
    {
        foreach (var name in (classes ?? string.Empty).Split(new[] { ' ', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
            if (!element.StyleClass.Contains(name)) element.StyleClass.Add(name);
    }

    private static bool HasSkinClass(VisualElement element, string name)
    {
        foreach (var item in element.StyleClass)
            if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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

    private static Color? ResolveColor(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = MauiSkinStyler.ResolveSkinValue(raw).Trim();
        return MauiSkinStyler.TryParseMauiColor(text, out var color) ? color : null;
    }

    private static ImageSource? LoadImage(string relative)
    {
        var text = (relative ?? string.Empty).Trim();
        if (text.Length == 0 || !SkinRules.IsSafeRelativePath(text)) return null;

        var folder = AndroidSkinStore.CurrentFolder;
        if (folder.Length == 0) return null;

        try
        {
            var file = System.IO.Path.Combine(AndroidSkinStore.SkinsRoot, folder,
                text.Replace('\\', '/').Replace('/', System.IO.Path.DirectorySeparatorChar));
            return File.Exists(file) ? ImageSource.FromFile(file) : null;
        }
        catch (Exception ex)
        {
            LogService.Error("读取声明式组件图片失败", ex);
            return null;
        }
    }

    private static bool TryDouble(string raw, out double value)
    {
        value = 0;
        var text = MauiSkinStyler.ResolveSkinValue(raw).Trim();
        var percent = text.EndsWith('%');
        if (percent) text = text[..^1];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (percent) value /= 100;
        return true;
    }

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

    private static List<string> ParseOptions(string raw)
    {
        var options = new List<string>();
        foreach (var part in (raw ?? string.Empty).Split(new[] { ',', '|' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var text = part.Trim();
            if (text.Length == 0 || options.Contains(text)) continue;
            options.Add(text);
        }
        return options;
    }

    private static string FormatNumber(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

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

    private static void WarnOnce(string key, string message)
    {
        if (!Warned.Add(key)) return;
        LogService.Warn(message);
    }
}
