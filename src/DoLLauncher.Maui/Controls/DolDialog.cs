using Microsoft.Maui.Controls.Shapes;

namespace DoLLauncher.Maui.Controls;

/// <summary>弹窗语义：决定左侧色条颜色（信息蓝 / 警告橙 / 错误红）。</summary>
internal enum DolDialogKind
{
    Info,
    Warning,
    Error,
}

/// <summary>弹窗结果：按钮下标（-1 表示被返回键 / 遮罩关闭）与输入框内容。</summary>
internal sealed record DolDialogResult(int Index, string? Input);

/// <summary>
/// 应用内统一弹窗：主题背景 + 左侧语义色条 + 圆角按钮 + 淡入动画，
/// 视觉规范与 WPF 端自定义弹窗保持一致，替换系统 Alert / ActionSheet / Prompt。
/// 实现方式是在当前页面的内容上盖一层半透明遮罩，因此不会新开 Activity、也不会丢页面状态。
/// </summary>
internal static class DolDialog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly Color InfoBar = Color.FromArgb("#1370F3");
    private static readonly Color WarningBar = Color.FromArgb("#E8A33D");
    private static readonly Color ErrorBar = Color.FromArgb("#D64545");

    /// <summary>单按钮提示。</summary>
    public static async Task AlertAsync(string title, string message, DolDialogKind kind = DolDialogKind.Info)
    {
        await ShowAsync(title, message, new[] { "确定" }, kind, primaryIndex: 0);
    }

    /// <summary>二选一确认；取消 / 返回键 / 点遮罩都返回 false。</summary>
    public static async Task<bool> ConfirmAsync(string title, string message,
        string confirmText = "确定", bool danger = false, string cancelText = "取消")
    {
        var result = await ShowAsync(title, message, new[] { cancelText, confirmText },
            danger ? DolDialogKind.Error : DolDialogKind.Info,
            primaryIndex: 1, dangerIndex: danger ? 1 : -1, allowDismiss: true);
        return result.Index == 1;
    }

    /// <summary>多选一（对应原生的 ActionSheet）；返回下标，取消返回 -1。</summary>
    public static async Task<int> ChooseAsync(string title, string message,
        IReadOnlyList<string> options, DolDialogKind kind = DolDialogKind.Warning,
        int primaryIndex = -1, int dangerIndex = -1)
    {
        var result = await ShowAsync(title, message, options, kind,
            primaryIndex, dangerIndex, allowDismiss: true);
        return result.Index;
    }

    /// <summary>带输入框的弹窗；返回输入文本，取消返回 null。</summary>
    public static async Task<string?> PromptAsync(string title, string message,
        string placeholder = "", string? initialValue = null, int maxLength = 200,
        string okText = "确定", string cancelText = "取消")
    {
        var result = await ShowAsync(title, message, new[] { cancelText, okText },
            DolDialogKind.Info, primaryIndex: 1, allowDismiss: true,
            inputPlaceholder: placeholder, inputValue: initialValue, maxLength: maxLength);
        return result.Index == 1 ? result.Input ?? string.Empty : null;
    }

    /// <summary>
    /// 弹出并等待结果。按钮数量为 1~2 时横向排列，多于 2 个时纵向排列（长文案不会被挤掉）。
    /// </summary>
    public static async Task<DolDialogResult> ShowAsync(string title, string message,
        IReadOnlyList<string> buttons, DolDialogKind kind = DolDialogKind.Info,
        int primaryIndex = -1, int dangerIndex = -1, bool allowDismiss = false,
        string? inputPlaceholder = null, string? inputValue = null, int maxLength = 200)
    {
        await Gate.WaitAsync();
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(() =>
                ShowCoreAsync(title, message, buttons, kind, primaryIndex, dangerIndex,
                    allowDismiss, inputPlaceholder, inputValue, maxLength));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<DolDialogResult> ShowCoreAsync(string title, string message,
        IReadOnlyList<string> buttons, DolDialogKind kind, int primaryIndex, int dangerIndex,
        bool allowDismiss, string? inputPlaceholder, string? inputValue, int maxLength)
    {
        var page = ResolvePage();
        if (page == null) return new DolDialogResult(-1, null);

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var cardBg = Color.FromArgb(dark ? "#2A2A2A" : "#FFFFFF");
        var plainBg = Color.FromArgb(dark ? "#3C3C3C" : "#E6E6E6");
        var plainText = Color.FromArgb(dark ? "#E8E8E8" : "#333333");
        var hintText = Color.FromArgb(dark ? "#A6A6A6" : "#808080");
        var accent = ReadResourceColor("AccentColor", "#1370F3");
        var accentOn = ReadResourceColor("AccentOnColor", "#FFFFFF");
        var barColor = kind switch
        {
            DolDialogKind.Warning => WarningBar,
            DolDialogKind.Error => ErrorBar,
            _ => InfoBar,
        };

        // 结果在 UI 线程上恢复，调用方（设置保存、页面跳转等）无需再切线程。
        var tcs = new TaskCompletionSource<DolDialogResult>();
        Entry? input = null;
        var closed = false;
        // 按钮在卡片构建时就已存在，真正的关闭逻辑稍后赋值（局部函数捕获了后面才初始化的变量）。
        Action<int>? closeHandler = null;

        // ---- 卡片内容 ----
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(18, 16, 18, 14) };

        stack.Children.Add(new Label
        {
            Text = title,
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            TextColor = plainText,
            LineBreakMode = LineBreakMode.WordWrap,
        });

        if (!string.IsNullOrWhiteSpace(message))
        {
            var messageLabel = new Label
            {
                Text = message,
                FontSize = 13,
                TextColor = hintText,
                LineBreakMode = LineBreakMode.WordWrap,
                LineHeight = 1.3,
            };
            var pageHeight = page.Height > 0 ? page.Height : 600;
            stack.Children.Add(new ScrollView
            {
                Content = messageLabel,
                MaximumHeightRequest = Math.Clamp(pageHeight * 0.45, 140, 360),
            });
        }

        if (inputPlaceholder != null)
        {
            input = new Entry
            {
                Placeholder = inputPlaceholder,
                Text = inputValue ?? string.Empty,
                MaxLength = maxLength,
                FontSize = 14,
                Margin = new Thickness(0, 2, 0, 0),
            };
            stack.Children.Add(input);
        }

        stack.Children.Add(BuildButtons(buttons, primaryIndex, dangerIndex, accent, accentOn,
            plainBg, plainText, input != null, i => closeHandler?.Invoke(i)));

        var body = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(4)),
                new ColumnDefinition(GridLength.Star),
            },
        };
        var bar = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = barColor,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14, 0, 0, 14) },
        };
        body.Children.Add(bar);
        Grid.SetColumn(bar, 0);
        body.Children.Add(stack);
        Grid.SetColumn(stack, 1);

        var pageWidth = page.Width > 0 ? page.Width : 900;
        var card = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            BackgroundColor = cardBg,
            WidthRequest = Math.Clamp(pageWidth * 0.66, 320, 560),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = body,
        };

        var dim = new BoxView
        {
            // 页面里已经有半透明遮罩时不再叠一层，否则两层相乘会接近纯黑；
            // 但必须保留 1/255 的不透明度，否则遮罩在 Android 上不可命中。
            Color = HasExistingDimLayer(page.Content)
                ? Color.FromArgb("#01000000")
                : Color.FromArgb("#4D000000"),
            // 全局隐式 BoxView 样式会把 BackgroundColor 设成不透明近黑，盖住半透明的 Color，
            // 看起来就是纯黑遮罩；这里必须显式清成透明。
            BackgroundColor = Colors.Transparent,
            InputTransparent = false,
        };
        var overlay = new Grid { BackgroundColor = Colors.Transparent, InputTransparent = false };
        overlay.Children.Add(dim);
        overlay.Children.Add(card);

        // ---- 挂到当前页面内容上 ----
        var original = page.Content;
        var wrapper = new Grid();
        // 直接替换 Content 会把原内容从页面上摘下来，不需要先置空。
        page.Content = wrapper;
        if (original != null) wrapper.Children.Add(original);
        wrapper.Children.Add(overlay);

        var previousBack = BackRouter.Handler;
        BackRouter.Handler = () =>
        {
            Close(-1);
            return true;
        };

        // 遮罩必须始终挂一个手势识别器：没有手势时 BoxView 不消费触摸，
        // 点在卡片外会穿透到下层界面（allowDismiss=false 时尤其明显）。
        var dimTap = new TapGestureRecognizer();
        dimTap.Tapped += (_, _) =>
        {
            if (allowDismiss) Close(-1);
        };
        dim.GestureRecognizers.Add(dimTap);

        void Close(int index)
        {
            if (closed) return;
            closed = true;
            BackRouter.Handler = previousBack;
            try
            {
                wrapper.Children.Remove(overlay);
                if (original != null)
                {
                    wrapper.Children.Remove(original);
                    page.Content = original;
                }
                else
                {
                    page.Content = null!;
                }
            }
            catch (Exception ex)
            {
                DoLLauncher.Services.LogService.Error("关闭自定义弹窗失败", ex);
            }
            tcs.TrySetResult(new DolDialogResult(index, input?.Text));
        }

        closeHandler = Close;

        // ---- 淡入 ----
        if (UiMotion.Enabled)
        {
            overlay.Opacity = 0;
            card.Scale = 0.96;
            _ = overlay.FadeToAsync(1, 120, Easing.CubicOut);
            _ = card.ScaleToAsync(1, 140, Easing.CubicOut);
        }

        if (input != null)
        {
            // 有输入框时直接把键盘焦点给它，省一次点击。
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                try { input.Focus(); } catch { }
            });
        }

        return await tcs.Task;
    }

    private static View BuildButtons(IReadOnlyList<string> buttons, int primaryIndex, int dangerIndex,
        Color accent, Color accentOn, Color plainBg, Color plainText, bool inputMode, Action<int> close)
    {
        Button Make(string text, int index)
        {
            var primary = index == primaryIndex;
            var danger = index == dangerIndex;
            var button = new Button
            {
                Text = text,
                FontSize = 13,
                HeightRequest = 36,
                MinimumHeightRequest = 36,
                CornerRadius = 9,
                Padding = new Thickness(16, 0),
                BackgroundColor = danger ? ErrorBar : primary ? accent : plainBg,
                TextColor = primary || danger ? accentOn : plainText,
            };
            button.Clicked += (_, _) => close(index);
            return button;
        }

        // 输入框弹窗的按钮固定一行，右对齐。
        var horizontal = buttons.Count <= 2 || inputMode;
        if (horizontal)
        {
            var holder = new HorizontalStackLayout { HorizontalOptions = LayoutOptions.End, Spacing = 8 };
            for (var i = 0; i < buttons.Count; i++)
                holder.Children.Add(Make(buttons[i], i));
            return holder;
        }

        // 三个及以上：纵向整行按钮，长文案不会被挤压。
        var column = new VerticalStackLayout { Spacing = 8 };
        for (var i = 0; i < buttons.Count; i++)
        {
            var b = Make(buttons[i], i);
            b.HorizontalOptions = LayoutOptions.Fill;
            column.Children.Add(b);
        }
        return new ScrollView
        {
            Content = column,
            MaximumHeightRequest = 300,
        };
    }

    private static ContentPage? ResolvePage()
    {
        var root = Application.Current?.Windows.FirstOrDefault()?.Page;
        return root is NavigationPage nav ? nav.CurrentPage as ContentPage : root as ContentPage;
    }

    /// <summary>页面里是否已经有铺满内容区的半透明遮罩（导出/忙碌遮罩等）。</summary>
    private static bool HasExistingDimLayer(VisualElement? root)
    {
        if (root == null || root.Width <= 0 || root.Height <= 0) return false;

        var stack = new Stack<IVisualTreeElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.GetVisualChildren())
            {
                if (child is not VisualElement ve) continue;
                stack.Push(ve);

                if (!ve.IsVisible) continue;
                if (ve.Width < root.Width * 0.6 || ve.Height < root.Height * 0.6) continue;

                var color = ve is BoxView box ? box.Color : ve.BackgroundColor;
                if (color != null && color.Alpha >= 0.25f && color.Alpha <= 0.95f) return true;
            }
        }
        return false;
    }

    private static Color ReadResourceColor(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color)
            return color;
        return Color.FromArgb(fallback);
    }
}
