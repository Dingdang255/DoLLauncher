using DoLLauncher.Core.Skin;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 把 Core 解析出的 CSS 选择器映射到 MAUI 可视树：元素名走别名表，
/// <c>.class</c> 对应 <c>VisualElement.StyleClass</c>，<c>#name</c> 对应 <c>VisualElement.StyleId</c>。
/// </summary>
internal static class MauiSkinSelector
{
    /// <summary>CSS 元素名 → 可能命中的 MAUI 类型名（含基类名）。</summary>
    private static readonly Dictionary<string, string[]> ElementAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["button"] = new[] { "Button" },
        ["label"] = new[] { "Label" },
        ["textblock"] = new[] { "Label" },
        ["textbox"] = new[] { "Entry", "Editor" },
        ["entry"] = new[] { "Entry" },
        ["editor"] = new[] { "Editor" },
        ["combobox"] = new[] { "Picker" },
        ["picker"] = new[] { "Picker" },
        ["checkbox"] = new[] { "CheckBox" },
        ["switch"] = new[] { "Switch" },
        ["slider"] = new[] { "Slider" },
        ["progressbar"] = new[] { "ProgressBar" },
        ["border"] = new[] { "Border" },
        ["frame"] = new[] { "Border", "Frame" },
        ["grid"] = new[] { "Grid" },
        ["stackpanel"] = new[] { "VerticalStackLayout", "HorizontalStackLayout", "StackLayout" },
        ["verticalstacklayout"] = new[] { "VerticalStackLayout" },
        ["horizontalstacklayout"] = new[] { "HorizontalStackLayout" },
        ["window"] = new[] { "Page" },
        ["page"] = new[] { "Page" },
        ["scrollviewer"] = new[] { "ScrollView" },
        ["scrollview"] = new[] { "ScrollView" },
        ["image"] = new[] { "Image" },
        ["refreshview"] = new[] { "RefreshView" },
        ["collectionview"] = new[] { "CollectionView" },
        ["listview"] = new[] { "ListView" },
        ["contentview"] = new[] { "ContentView" },
        ["activityindicator"] = new[] { "ActivityIndicator" },
    };

    public static bool Matches(SkinSelector selector, VisualElement element)
    {
        if (!MatchesPart(selector, element)) return false;
        if (selector.Ancestors.Count == 0) return true;

        var index = selector.Ancestors.Count - 1;
        var parent = element.Parent as VisualElement;
        while (parent != null && index >= 0)
        {
            if (MatchesPart(selector.Ancestors[index], parent)) index--;
            parent = parent.Parent as VisualElement;
        }
        return index < 0;
    }

    private static bool MatchesPart(SkinSelector selector, VisualElement element)
    {
        if (!selector.Universal && selector.Type.Length > 0 && !TypeMatches(element.GetType(), selector.Type))
            return false;

        if (selector.Name.Length > 0 &&
            !string.Equals(element.StyleId, selector.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (selector.Classes.Count > 0)
        {
            var classes = element.StyleClass;
            if (classes == null || classes.Count == 0) return false;
            foreach (var className in selector.Classes)
            {
                var hit = false;
                foreach (var actual in classes)
                {
                    if (!string.Equals(actual, className, StringComparison.OrdinalIgnoreCase)) continue;
                    hit = true;
                    break;
                }
                if (!hit) return false;
            }
        }

        return true;
    }

    private static bool TypeMatches(Type type, string selectorType)
    {
        var names = TypeNames(type);
        if (ElementAliases.TryGetValue(selectorType, out var targets))
        {
            foreach (var target in targets)
                if (names.Contains(target)) return true;
            return false;
        }
        return names.Contains(selectorType);
    }

    private static HashSet<string> TypeNames(Type type)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            names.Add(current.Name);
        return names;
    }
}
