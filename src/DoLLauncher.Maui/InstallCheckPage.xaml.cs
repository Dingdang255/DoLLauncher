using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Maui.Controls.Shapes;

namespace DoLLauncher.Maui;

/// <summary>一个待安装模组：模组本体、检查结果与行内选择的版本信息。</summary>
public sealed class InstallPlan
{
    public MarketMod Mod { get; init; } = new();
    public InstallCheckResult Check { get; init; } = new();
    public GameRelease? Release { get; init; }
    public MarketEdition? Edition { get; init; }
    public string? AssetName { get; init; }
}

/// <summary>安装前检查与安装进度页：先前置、后本体逐个下载安装；支持一次列出多个模组。</summary>
public partial class InstallCheckPage : ContentPage
{
    private static readonly DataTemplate DependencyTemplate = new(() => BuildDependencyRow());
    private static readonly DataTemplate SatisfiedTemplate = new(() => BuildSatisfiedRow());
    private static readonly DataTemplate ConflictTemplate = new(() => BuildConflictRow());
    private static readonly DataTemplate NoteTemplate = new(() => BuildNoteRow());

    private readonly IReadOnlyList<InstallPlan> _plans;
    private readonly VersionInfo _target;
    private readonly Action<double, string>? _onProgress;
    private bool _installing;

    public InstallCheckPage(MarketMod mod, VersionInfo target, InstallCheckResult check,
        GameRelease? release, MarketEdition? edition, string? assetName,
        Action<double, string>? onProgress = null)
        : this(new[]
        {
            new InstallPlan
            {
                Mod = mod,
                Check = check,
                Release = release,
                Edition = edition,
                AssetName = assetName,
            },
        }, target, onProgress)
    {
    }

    public InstallCheckPage(IReadOnlyList<InstallPlan> plans, VersionInfo target,
        Action<double, string>? onProgress = null)
    {
        InitializeComponent();
        _plans = RemoveNestedDependencyPlans(plans);
        _target = target;
        _onProgress = onProgress;

        var multi = _plans.Count > 1;
        TitleLabel.Text = multi
            ? $"批量安装 {_plans.Count} 个模组"
            : $"安装「{_plans[0].Mod.Name}」";

        var subtitle = $"目标版本 {target.DisplayName}";
        if (target.GameVersion is { Length: > 0 } gameVersion) subtitle += $"（游戏 {gameVersion}）";
        SubtitleLabel.Text = subtitle;

        for (var i = 0; i < _plans.Count; i++)
        {
            if (i > 0) PlansList.Children.Add(BuildDivider());
            PlansList.Children.Add(BuildPlanBlock(_plans[i], multi));
        }

        FootHint.Text = multi
            ? "会先装各模组勾选的前置，再逐个安装；同名文件将被覆盖。"
            : _plans[0].Check.Dependencies.Count > 0
                ? "会先装勾选的前置，再装主模组；同名文件将被覆盖。"
                : "同名文件将被覆盖，已启用的模组不会自动重新排序。";
    }

    /// <summary>
    /// 某个显式勾选的模组同时是另一个勾选模组的前置时，从顶层安装列表移除：
    /// 它会在依赖它的模组的「需要补装的前置」里出现，避免重复列两次。
    /// </summary>
    private static List<InstallPlan> RemoveNestedDependencyPlans(IReadOnlyList<InstallPlan> plans)
    {
        var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            foreach (var dependency in plan.Check.Dependencies.Where(d => d.Selected))
                nested.Add(PlanKey(dependency.Mod));
        }

        var kept = plans.Where(p => !nested.Contains(PlanKey(p.Mod))).ToList();
        // 理论上不会为空（模组不会依赖自身），留一层兜底避免索引越界。
        return kept.Count > 0 ? kept : plans.ToList();
    }

    private static string PlanKey(MarketMod mod) =>
        mod.Id.Length > 0 ? mod.Id : DownloadService.NormalizeName(mod.Name);

    /// <summary>安装成功后回传实际安装的模组数量。</summary>
    public event Func<int, Task>? Completed;

    /// <summary>确定后立即返回上一级，安装在后台继续；退出本页不会取消下载。</summary>
    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (_installing) return;
        _installing = true;
        StartButton.IsEnabled = false;

        // 每个模组：先前置、后本体；用户取消勾选的前置不会安装
        var queue = new List<InstallStep>();
        foreach (var plan in _plans)
        {
            foreach (var dependency in plan.Check.Dependencies.Where(d => d.Selected))
                queue.Add(new InstallStep(dependency.Mod, null, null, null));
            queue.Add(new InstallStep(plan.Mod, plan.Release, plan.Edition, plan.AssetName));
        }

        await Navigation.PopAsync();
        _ = RunInstallAsync(queue);
    }

    private async Task RunInstallAsync(List<InstallStep> queue)
    {
        try
        {
            for (var i = 0; i < queue.Count; i++)
            {
                var step = queue[i];
                var index = i;
                // 进度传回宿主页（市场面板）显示，否则安装期间界面没有任何反馈。
                _onProgress?.Invoke(index / (double)Math.Max(1, queue.Count),
                    $"{index + 1}/{queue.Count}　{step.Mod.Name}");
                await DownloadService.InstallModAsync(step.Mod, _target.FullPath,
                    msg => LogService.Append("[市场] " + msg),
                    p => _onProgress?.Invoke((index + p) / Math.Max(1, queue.Count),
                        $"{index + 1}/{queue.Count}　{step.Mod.Name}"),
                    step.Release?.Tag,
                    step.Edition,
                    step.AssetName);
                _onProgress?.Invoke((index + 1.0) / Math.Max(1, queue.Count),
                    $"{index + 1}/{queue.Count}　{step.Mod.Name}");
            }

            LogService.Append($"[市场] 安装完成（{queue.Count} 个）");
            _onProgress?.Invoke(-1, "");
            if (Completed != null)
                await MainThread.InvokeOnMainThreadAsync(async () => await Completed(queue.Count));
        }
        catch (Exception ex)
        {
            LogService.Error("安装市场模组失败", ex);
            _onProgress?.Invoke(-1, "");
            await MainThread.InvokeOnMainThreadAsync(async () =>
                await PlatformServices.Dialogs.ErrorAsync("安装失败：" + ex.Message, "模组市场"));
        }
        finally
        {
            _installing = false;
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }

    private readonly record struct InstallStep(
        MarketMod Mod, GameRelease? Release, MarketEdition? Edition, string? AssetName);

    // ---------- 界面组装 ----------

    private View BuildPlanBlock(InstallPlan plan, bool showHeader)
    {
        var stack = new VerticalStackLayout { Spacing = 10 };

        if (showHeader)
        {
            stack.Children.Add(new Label
            {
                Text = $"安装「{plan.Mod.Name}」",
                FontSize = 15,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.CharacterWrap,
            });

            var detail = DescribePlan(plan);
            if (detail.Length > 0) stack.Children.Add(Hint(detail));
        }

        var check = plan.Check;
        if (check.Dependencies.Count > 0)
        {
            stack.Children.Add(Card(
                SectionTitle("需要补装的前置"),
                Hint("默认已勾选；取消勾选就只安装主模组，前置需要你自己补。"),
                BuildList(check.Dependencies, DependencyTemplate)));
        }

        if (check.Satisfied.Count > 0)
        {
            stack.Children.Add(Card(
                SectionTitle("已满足的前置"),
                BuildList(check.Satisfied, SatisfiedTemplate)));
        }

        if (check.Conflicts.Count > 0)
        {
            stack.Children.Add(Card(
                SectionTitle("与本地模组冲突"),
                BuildList(check.Conflicts, ConflictTemplate)));
        }

        if (check.Notes.Count > 0)
        {
            stack.Children.Add(Card(
                SectionTitle("提示"),
                BuildList(check.Notes, NoteTemplate)));
        }

        if (!check.HasFindings)
            stack.Children.Add(Hint("没有发现缺失前置，也没有与本地已装模组冲突。"));

        return stack;
    }

    private static string DescribePlan(InstallPlan plan)
    {
        var parts = new List<string>();
        if (plan.Release != null) parts.Add("Release " + plan.Release.DisplayName);
        if (plan.Edition != null) parts.Add("包型 " + plan.Edition.Display);
        if (!string.IsNullOrWhiteSpace(plan.AssetName)) parts.Add("包 " + plan.AssetName);
        return string.Join("　·　", parts);
    }

    private static Border Card(params View[] children)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        foreach (var child in children) stack.Children.Add(child);

        var border = new Border
        {
            Padding = 12,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = stack,
        };
        border.SetAppThemeColor(Border.BackgroundColorProperty,
            Color.FromArgb("#F2F2F2"), Color.FromArgb("#2A2A2A"));
        return border;
    }

    private static View BuildList<T>(IReadOnlyList<T> items, DataTemplate template)
    {
        var stack = new VerticalStackLayout { Spacing = 6 };
        BindableLayout.SetItemTemplate(stack, template);
        BindableLayout.SetItemsSource(stack, items);
        return stack;
    }

    private static BoxView BuildDivider()
    {
        var divider = new BoxView { HeightRequest = 1, HorizontalOptions = LayoutOptions.Fill };
        divider.SetAppThemeColor(BoxView.ColorProperty,
            Color.FromArgb("#E0E0E0"), Color.FromArgb("#3C3C3C"));
        return divider;
    }

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontAttributes = FontAttributes.Bold,
    };

    private static Label Hint(string text)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 11,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        label.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));
        return label;
    }

    private static View BuildDependencyRow()
    {
        var check = new CheckBox { VerticalOptions = LayoutOptions.Start };
        check.SetBinding(CheckBox.IsCheckedProperty,
            new Binding(nameof(InstallDependency.Selected), BindingMode.TwoWay));

        var title = new Label
        {
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        title.SetBinding(Label.TextProperty, nameof(InstallDependency.Title));

        var source = new Label { FontSize = 10 };
        source.SetBinding(Label.TextProperty, nameof(InstallDependency.SourceText));
        source.SetDynamicResource(Label.TextColorProperty, "AccentColor");

        var badge = new Border
        {
            Padding = new Thickness(5, 1),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            VerticalOptions = LayoutOptions.Center,
            Content = source,
        };
        badge.SetDynamicResource(Border.BackgroundColorProperty, "AccentPaleColor");

        var titleRow = new HorizontalStackLayout { Spacing = 6 };
        titleRow.Children.Add(title);
        titleRow.Children.Add(badge);

        var detail = new Label
        {
            FontSize = 11,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        detail.SetBinding(Label.TextProperty, nameof(InstallDependency.Detail));
        detail.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));

        var texts = new VerticalStackLayout { Spacing = 3 };
        texts.Children.Add(titleRow);
        texts.Children.Add(detail);

        var row = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
        };
        row.Add(check);
        row.Add(texts);
        Grid.SetColumn(texts, 1);
        return row;
    }

    private static View BuildSatisfiedRow()
    {
        var check = new CheckBox { IsChecked = true, IsEnabled = false, VerticalOptions = LayoutOptions.Center };

        var text = new Label
        {
            FontSize = 11,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        text.SetBinding(Label.TextProperty, ".");
        text.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));

        var row = new Grid
        {
            ColumnSpacing = 4,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
        };
        row.Add(check);
        row.Add(text);
        Grid.SetColumn(text, 1);
        return row;
    }

    private static View BuildConflictRow()
    {
        var title = new Label
        {
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.CharacterWrap,
            TextColor = Color.FromArgb("#C62828"),
        };
        title.SetBinding(Label.TextProperty, nameof(InstallConflict.Title));

        var detail = new Label
        {
            FontSize = 11,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        detail.SetBinding(Label.TextProperty, nameof(InstallConflict.Detail));
        detail.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));

        var stack = new VerticalStackLayout { Spacing = 3 };
        stack.Children.Add(title);
        stack.Children.Add(detail);
        return stack;
    }

    private static View BuildNoteRow()
    {
        var text = new Label
        {
            FontSize = 11,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        text.SetBinding(Label.TextProperty, ".");
        text.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));
        return text;
    }
}
