using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DoLLauncher.Core.Import;
using Shapes = System.Windows.Shapes;
using DoLLauncher.Controls;
using DoLLauncher.Core.Browser;
using DoLLauncher.Core.Search;
using DoLLauncher.Core.Theme;
using DoLLauncher.Core.Versions;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using SubtitleTextHelper = DoLLauncher.Core.Ui.SubtitleText;

namespace DoLLauncher;

public partial class MainWindow
{
    // ---------- 模组市场详情 ----------

    /// <summary>匹配模式当前是否为「标签范围」下的二级菜单形态。</summary>
    private bool _matchItemsTagScope;

    private bool _tagPopupHooked;
    private Popup? _tagPartialPopup;
    private ComboBoxItem? _pendingPartialItem;

    /// <summary>游戏本体进度条完成后的延迟收起序号，新下载开始时会作废旧任务。</summary>
    private int _gameProgressDismissSeq;

    /// <summary>详情窗打开后按需拉取仓库 Star（有 24 小时缓存则直接用），取到就追加到信息行末尾。</summary>
    private async Task AppendStarAsync(TextBlock target, string repo)
    {
        var stars = await ModStarService.TryGetAsync(repo);
        if (stars == null) return;
        target.Text += $"　·　★ {FormatStars(stars.Value)}";
    }

    private static string FormatStars(int stars) =>
        stars >= 10000 ? $"{stars / 10000.0:0.#} 万" : stars.ToString();

    // ---------- 市场多选与版本选择记忆 ----------

    /// <summary>市场行的勾选键：市场 id 优先，回退到名称。</summary>
    private static string MarketKey(MarketMod mod) => mod.Id.Length > 0 ? mod.Id : mod.Name;

    /// <summary>勾选中的模组键；即使被搜索过滤掉，也仍会参与批量安装。</summary>
    private readonly HashSet<string> _marketChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每个模组上次选的 Release / 包型 / 包文件名，只放内存，不落盘。</summary>
    private readonly Dictionary<string, MarketPick> _marketPicks = new(StringComparer.OrdinalIgnoreCase);

    private sealed record MarketPick(string ReleaseTag, string EditionId, string AssetName);

    private void MarketCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: MarketItemVm item } box) return;
        item.IsChecked = box.IsChecked == true;
        var key = MarketKey(item.Mod);
        if (item.IsChecked) _marketChecked.Add(key);
        else _marketChecked.Remove(key);
        UpdateMarketBatchHint();
    }

    private void UpdateMarketBatchHint()
    {
        if (MarketBatchHint == null) return;
        MarketBatchHint.Text = _marketChecked.Count > 0
            ? $"已勾选 {_marketChecked.Count} 个，点「下载并安装」可批量安装"
            : "";
    }

    /// <summary>按模组记住当前下拉框的选择（Release / 包型 / 包文件名）。</summary>
    private void RememberMarketPick()
    {
        if (MarketList.SelectedItem is not MarketItemVm item) return;
        if (MarketVersionBox.SelectedItem is not GameRelease release) return;
        var edition = MarketEditionBox.SelectedItem as MarketEdition;
        var editionId = edition?.Id ?? "";
        var asset = (MarketAssetBox.SelectedItem as ModAssetOption)?.AssetName ?? "";
        var key = MarketKey(item.Mod);
        // 切回某个模组时资产列表还没拉回来，这时不能把上次记住的包文件名冲掉。
        if (asset.Length == 0 && _marketPicks.TryGetValue(key, out var previous) &&
            previous.ReleaseTag == release.Tag && previous.EditionId == editionId)
            asset = previous.AssetName;
        _marketPicks[key] = new MarketPick(release.Tag, editionId, asset);
    }

    /// <summary>Release 列表里按上次记住的 tag 找下标，找不到就用最新（0）。</summary>
    private int ReleaseIndexFor(MarketMod mod)
    {
        if (!_marketPicks.TryGetValue(MarketKey(mod), out var pick)) return 0;
        for (var i = 0; i < _marketReleases.Count; i++)
        {
            if (string.Equals(_marketReleases[i].Tag, pick.ReleaseTag, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    /// <summary>Release 缓存只留最近的一小部分，避免长时间浏览后内存一直涨。</summary>
    private void CacheMarketReleases(string repo, List<GameRelease> list)
    {
        if (list.Count == 0) return;
        if (_releaseCache.Count >= 32 && !_releaseCache.ContainsKey(repo))
        {
            var oldest = _releaseCache.Keys.FirstOrDefault();
            if (oldest != null) _releaseCache.Remove(oldest);
        }
        _releaseCache[repo] = list;
    }

    private void MarketList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MarketList.SelectedItem is MarketItemVm item) ShowMarketDetail(item.Mod);
    }

    private void ShowMarketDetail(MarketMod mod)
    {
        var win = new Window
        {
            Title = "模组详情",
            Width = 820,
            Height = 680,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("BgBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White,
            Foreground = TryFindResource("TextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
        };
        ThemeService.Attach(win);
        AppDialog.AttachDialogChrome(win, 820);
        UiAnimations.AttachWindow(win);

        var root = new DockPanel { Margin = new Thickness(18) };

        // 底部：安装目标版本 / 历史版本 / 下载 / 关闭
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);

        var actions = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var status = new TextBlock
        {
            Style = TryFindResource("MutedText") as Style,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var install = new Button
        {
            Content = "下载并安装",
            Style = TryFindResource("PrimaryButton") as Style,
            MinWidth = 110,
        };

        if (mod.Repo.Length > 0)
        {
            var homepage = "https://github.com/" + mod.Repo;
            var openHome = new Button
            {
                Content = "打开主页",
                Style = TryFindResource("FlatButton") as Style,
                MinWidth = 92,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = homepage,
            };
            openHome.Click += (_, _) =>
            {
                try
                {
                    PlatformServices.Links.OpenUrl(homepage);
                }
                catch
                {
                    // 打不开浏览器时忽略
                }
            };
            right.Children.Add(openHome);
        }

        right.Children.Add(install);

        var targetBox = new ComboBox
        {
            Style = TryFindResource("FlatCombo") as Style,
            ItemContainerStyle = TryFindResource("FlatComboItem") as Style,
            ItemTemplate = TryFindResource("VersionComboTemplate") as DataTemplate,
            Width = 200,
            ToolTip = "选择要把模组装到哪个版本",
        };
        targetBox.ItemsSource = _targets;
        targetBox.SelectedItem = MarketTargetBox.SelectedItem ?? _targets.FirstOrDefault();

        var versionBox = new ComboBox
        {
            Style = TryFindResource("FlatCombo") as Style,
            ItemContainerStyle = TryFindResource("FlatComboItem") as Style,
            ItemTemplate = TryFindResource("ReleaseComboTemplate") as DataTemplate,
            Width = 150,
            IsEnabled = false,
            ToolTip = "选择历史 Release，默认最新",
        };

        // 具体资产版本（例如 AU 女体模型的 0.8.7 / 0.9.3）
        var assetBox = new ComboBox
        {
            Style = TryFindResource("FlatCombo") as Style,
            ItemContainerStyle = TryFindResource("FlatComboItem") as Style,
            ItemTemplate = TryFindResource("EditionComboTemplate") as DataTemplate,
            Width = 110,
            ToolTip = "这个包型下的具体版本，默认选最新",
        };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        left.Children.Add(new TextBlock
        {
            Text = "安装到版本", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), Opacity = 0.7,
        });
        left.Children.Add(targetBox);
        left.Children.Add(new TextBlock
        {
            Text = "Release", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 8, 0), Opacity = 0.7,
        });
        left.Children.Add(versionBox);
        left.Children.Add(new TextBlock
        {
            Text = "包版本", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 8, 0), Opacity = 0.7,
        });
        left.Children.Add(assetBox);

        // 这一行控件较多，超宽时裁剪在页脚内，按住 Shift + 滚轮可左右移动
        var leftScroller = new ScrollViewer
        {
            Style = TryFindResource("RowScroller") as Style,
            Content = left,
        };
        actions.Children.Add(right);
        actions.Children.Add(leftScroller);
        footer.Children.Add(actions);
        footer.Children.Add(status);

        // 头部信息
        ComboBox? editionBox = null;
        // 右上角：切换查看仓库 README 或所选 Release 的更新日志
        var sourceBox = new ComboBox
        {
            Style = TryFindResource("FlatCombo") as Style,
            ItemContainerStyle = TryFindResource("FlatComboItem") as Style,
            Width = 160,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "切换查看仓库 README 或当前所选 Release 的更新日志",
        };
        sourceBox.Items.Add("仓库 README");
        sourceBox.Items.Add("Release 更新日志");
        sourceBox.SelectedIndex = 0;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = mod.Name, FontSize = 20, FontWeight = FontWeights.SemiBold });
        var metaText = new TextBlock
        {
            Text = $"版本 {(mod.Version.Length > 0 ? mod.Version : "未知")}　·　作者 {(mod.Author.Length > 0 ? mod.Author : "未知")}" +
                   (mod.Category.Length > 0 ? $"　·　{mod.Category}" : "") +
                   (mod.Repo.Length > 0 ? $"　·　{mod.Repo}" : ""),
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };
        header.Children.Add(metaText);
        // 仓库 Star 按需拉取，取到后追加到这一行末尾
        if (mod.Repo.Length > 0) _ = AppendStarAsync(metaText, mod.Repo);
        if (mod.Description.Length > 0)
        {
            header.Children.Add(new TextBlock
            {
                Text = mod.Description,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        if (mod.Editions.Count > 1)
        {
            editionBox = new ComboBox
            {
                Style = TryFindResource("FlatCombo") as Style,
                ItemContainerStyle = TryFindResource("FlatComboItem") as Style,
                ItemTemplate = TryFindResource("EditionComboTemplate") as DataTemplate,
                Width = 260,
                ToolTip = "同一个模组的不同包型 / 包体系列，安装前请选对。",
            };
            editionBox.ItemsSource = mod.Editions;
            editionBox.SelectedIndex = 0;

            var editionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0),
            };
            editionRow.Children.Add(new TextBlock
            {
                Text = "包型",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Opacity = 0.7,
            });
            editionRow.Children.Add(editionBox);
            header.Children.Add(editionRow);
        }

        // 换了 Release 或包型后重新拉取这一包型可选的资产版本
        async Task ReloadAssetVersionsAsync()
        {
            assetBox.ItemsSource = null;
            var release = versionBox.SelectedItem as GameRelease;
            if (release == null) return;
            try
            {
                var list = await DownloadService.FetchModAssetVersionsAsync(
                    mod, editionBox?.SelectedItem as MarketEdition, release.Tag);
                if (!ReferenceEquals(versionBox.SelectedItem, release)) return;
                assetBox.ItemsSource = list;
                if (list.Count > 0) assetBox.SelectedIndex = 0;
            }
            catch
            {
                // 拿不到具体版本列表时退回自动挑选
            }
        }

        versionBox.SelectionChanged += (_, _) => _ = ReloadAssetVersionsAsync();
        if (editionBox != null) editionBox.SelectionChanged += (_, _) => _ = ReloadAssetVersionsAsync();

        if (mod.Installed)
        {
            header.Children.Add(new TextBlock
            {
                Text = mod.BadgeText,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = TryFindResource("AccentBrush") as System.Windows.Media.Brush,
            });
        }

        // 头部左侧信息 + 右上角 README / 更新日志切换
        var headerPanel = new Grid();
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerPanel.Children.Add(header);
        Grid.SetColumn(sourceBox, 1);
        headerPanel.Children.Add(sourceBox);
        DockPanel.SetDock(headerPanel, Dock.Top);

        var readme = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Document = MarkdownService.Render("（正在读取 README……）"),
        };
        // 阅读控件外观与更新提示弹窗保持一致
        var readmeBorder = new Border
        {
            Background = TryFindResource("PanelBrush") as System.Windows.Media.Brush,
            BorderBrush = TryFindResource("BorderBrush") as System.Windows.Media.Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = readme,
        };

        void ShowChangelog()
        {
            var release = versionBox.SelectedItem as GameRelease;
            readme.Document = MarkdownService.Render(release == null
                ? "（正在读取 Release 信息……）"
                : release.Body.Trim().Length > 0
                    ? release.Body
                    : "（该版本没有提供更新日志）");
        }

        sourceBox.SelectionChanged += (_, _) =>
        {
            if (sourceBox.SelectedIndex == 1)
            {
                ShowChangelog();
                return;
            }
            readme.Document = MarkdownService.Render("（正在读取 README……）");
            _ = LoadMarketReadmeAsync(mod, readme);
        };

        // 处于更新日志模式时，切换 Release 同步刷新内容
        versionBox.SelectionChanged += (_, _) =>
        {
            if (sourceBox.SelectedIndex == 1) ShowChangelog();
        };

        install.Click += async (_, _) =>
        {
            var target = targetBox.SelectedItem as VersionVm;
            if (target == null || target.Info.IsFileMode)
            {
                status.Text = "请先选择一个磁盘版本。";
                return;
            }

            install.IsEnabled = false;
            status.Text = "准备中…";
            try
            {
                await InstallMarketModAsync(mod, target, versionBox.SelectedItem as GameRelease,
                    t => status.Text = t, editionBox?.SelectedItem as MarketEdition,
                    (assetBox.SelectedItem as ModAssetOption)?.AssetName);
                if (status.Text == "准备中…") status.Text = "";
            }
            finally
            {
                install.IsEnabled = true;
            }
        };

        root.Children.Add(footer);
        root.Children.Add(headerPanel);
        root.Children.Add(readmeBorder);
        win.Content = root;
        // 去掉「关闭」按钮后，Esc 直接关窗
        win.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape) win.Close();
        };

        _ = LoadMarketReadmeAsync(mod, readme);
        _ = LoadMarketReleasesAsync(mod, versionBox, status);
        win.ShowDialog();
    }

    private async Task LoadMarketReadmeAsync(MarketMod mod, RichTextBox box)
    {
        var key = mod.Id.Length > 0 ? mod.Id : mod.Name;
        if (_readmeCache.TryGetValue(key, out var cached))
        {
            box.Document = MarkdownService.Render(cached.Length > 0 ? cached : "（这个模组没有提供 README）");
            return;
        }

        try
        {
            var text = await DownloadService.FetchReadmeAsync(mod);
            _readmeCache[key] = text;
            box.Document = MarkdownService.Render(text.Length > 0 ? text : "（这个模组没有提供 README）");
        }
        catch (Exception ex)
        {
            box.Document = MarkdownService.Render("（读取 README 失败：" + ex.Message + "）");
        }
    }

    private async Task LoadMarketReleasesAsync(MarketMod mod, ComboBox box, TextBlock status)
    {
        try
        {
            var releases = await DownloadService.FetchModReleasesAsync(mod);
            box.ItemsSource = releases;
            box.IsEnabled = releases.Count > 0;
            if (releases.Count > 0) box.SelectedIndex = 0;
        }
        catch
        {
            status.Text = "没有取到历史版本，将直接安装最新版。";
        }
    }

    private string? PromptText(string title, string label, string initial)
    {
        var win = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ThemeService.Attach(win);
        // 自适应高度的小窗口要在尺寸稳定后再对齐 / 居中，否则会偏出主窗口中心
        AppDialog.AttachDialogChrome(win, 440);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 14) };
        panel.Children.Add(box);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ok = new Button { Content = "确定", Padding = new Thickness(18, 5, 18, 5), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(8, 0, 0, 0) };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        panel.Children.Add(row);

        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); win.Close(); };
        cancel.Click += (_, _) => win.Close();
        win.Content = panel;
        box.Focus();
        box.SelectAll();
        win.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    // ---------- 下载中心 ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (VersionPage == null || DownloadPage == null || BackupPage == null || StoryPage == null
            || DiagnosticPage == null || SettingsPage == null) return;
        var showDownload = NavDownload.IsChecked == true;
        var showBackup = NavBackup.IsChecked == true;
        var showStory = NavStory.IsChecked == true;
        var showDiagnostic = NavDiagnostic.IsChecked == true;
        var showSettings = NavSettings.IsChecked == true;

        VersionPage.Visibility = !showDownload && !showBackup && !showStory && !showDiagnostic && !showSettings
            ? Visibility.Visible : Visibility.Collapsed;
        DownloadPage.Visibility = showDownload ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = showBackup ? Visibility.Visible : Visibility.Collapsed;
        StoryPage.Visibility = showStory ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticPage.Visibility = showDiagnostic ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;

        var shown = showSettings ? SettingsPage
            : showDiagnostic ? DiagnosticPage
            : showStory ? StoryPage
            : showBackup ? BackupPage
            : showDownload ? DownloadPage : VersionPage;
        UiAnimations.ShowPage(shown);

        if (showDownload && !_gameLoaded) _ = RefreshGameReleasesAsync();
        if (showBackup) RefreshBackups();
        if (showStory) OnStoryPageShown();
        if (showDiagnostic)
        {
            SyncDiagTarget();
            LoadBackupSettings();
            RefreshGameLogStatus();
            UpdateDiagModeUi();
        }
        else
        {
            StopDiagLiveTimer();
        }
    }

    private void DownloadNav_Checked(object sender, RoutedEventArgs e)
    {
        if (GamePane == null || ModPane == null || PackPane == null) return;
        var showMod = DownloadNavMod.IsChecked == true;
        var showPack = DownloadNavPack.IsChecked == true;
        GamePane.Visibility = !showMod && !showPack ? Visibility.Visible : Visibility.Collapsed;
        ModPane.Visibility = showMod ? Visibility.Visible : Visibility.Collapsed;
        PackPane.Visibility = showPack ? Visibility.Visible : Visibility.Collapsed;
        MarketTargetPanel.Visibility = showMod ? Visibility.Visible : Visibility.Collapsed;
        UiAnimations.ShowPage(showMod ? ModPane : showPack ? PackPane : GamePane);
        if (showMod && !_marketLoaded) _ = RefreshMarketAsync();
        if (showPack && !_packLoaded) _ = RefreshPackAsync();
    }

    private void SyncMarketTarget()
    {
        var current = (MarketTargetBox.SelectedItem as VersionVm)?.Info.Folder;
        if (current != null && _targets.Any(v => v.Info.Folder == current)) return;
        var preferred = _selected?.Info.Folder;
        MarketTargetBox.SelectedItem =
            _targets.FirstOrDefault(v => v.Info.Folder == preferred) ?? _targets.FirstOrDefault();
        RefreshMarketStatus();
    }

    private void RefreshMarket_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadBusy) return;
        // 手动刷新绕过 CDN / 本地缓存，确保拿到最新的自有市场列表
        if (ModPane.Visibility == Visibility.Visible) _ = RefreshMarketAsync(forceRefresh: true);
        else if (PackPane.Visibility == Visibility.Visible) _ = RefreshPackAsync(forceReload: true);
        else _ = RefreshGameReleasesAsync();
    }

    /// <summary>按设置里的「启动时预取索引」时序，在后台拉取游戏本体、模组市场与整合包索引。</summary>
    private async Task PrefetchIndexesAsync()
    {
        var cfg = AppConfig.Load();
        var mode = cfg.General.Prefetch;
        if (mode == "none") return;

        if (mode == "parallel")
        {
            await Task.WhenAll(RefreshMarketAsync(), RefreshGameReleasesAsync(), RefreshPackAsync());
            return;
        }

        // 兼容旧配置；新配置统一用 custom + prefetchOrder
        var order = mode == "modFirst"
            ? new List<string> { "market", "game", "pack" }
            : mode == "gameFirst"
                ? new List<string> { "game", "market", "pack" }
                : ParsePrefetchOrder(cfg.General.PrefetchOrder);

        foreach (var key in order)
        {
            switch (key)
            {
                case "market":
                    await RefreshMarketAsync();
                    break;
                case "pack":
                    await RefreshPackAsync();
                    break;
                default:
                    await RefreshGameReleasesAsync();
                    break;
            }
        }
    }

    private static List<string> ParsePrefetchOrder(string? value)
    {
        var known = new[] { "game", "market", "pack" };
        var list = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(known.Contains)
            .Distinct()
            .ToList();
        foreach (var key in known)
            if (!list.Contains(key)) list.Add(key);
        return list;
    }

    private async Task RefreshGameReleasesAsync()
    {
        if (_gameLoading) return;
        _gameLoading = true;
        var source = DownloadService.CurrentGameSource();
        GameStatusText.Text = "正在获取 Release 列表…";
        try
        {
            var list = await DownloadService.FetchGameReleasesAsync(source);
            _gameReleases.Clear();
            foreach (var release in list.Where(r => r.HasInstallableAsset))
                _gameReleases.Add(release);
            if (_gameReleases.Count > 0) GameReleaseList.SelectedIndex = 0;
            _gameLoaded = true;
            GameStatusText.Text = $"共 {_gameReleases.Count} 个 Release";
            Log($"已获取 {_gameReleases.Count} 个 Release（{source.Name}）");
        }
        catch (Exception ex)
        {
            _gameLoaded = false;
            GameStatusText.Text = "获取失败：" + ex.Message;
            LogError("获取本体列表失败", ex);
        }
        finally
        {
            _gameLoading = false;
        }
    }

    private void GameSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GameSourceBox.SelectedItem is not GameSource source) return;
        var cfg = AppConfig.Load();
        if (cfg.General.GameSource != source.Id)
        {
            cfg.General.GameSource = source.Id;
            AppConfig.Save(cfg);
        }
        ApplyGameSourceUi(source);
        if (_gameLoaded)
        {
            _gameLoaded = false;
            _ = RefreshGameReleasesAsync();
        }
    }

    /// <summary>按游戏源的形态调整说明与选项：单 HTML 源的附带模组改从汉化仓库按版本获取。</summary>
    private void ApplyGameSourceUi(GameSource source)
    {
        GameSourceHintText.Text = source.Hint;
        var isZip = source.IsZip;
        PolyfillCheck.IsEnabled = isZip;
        I18nCheck.IsEnabled = true;
        ImagePackCheck.IsEnabled = true;
        PolyfillCheck.ToolTip = isZip ? null : "该游戏源只有单个 HTML，不提供这个选项";
        var extraTip = isZip ? null : "该源从汉化仓库获取与游戏版本匹配的附带模组";
        I18nCheck.ToolTip = extraTip;
        ImagePackCheck.ToolTip = extraTip;
    }

    /// <summary>拉取 Lyra 整合包最新 Release，并把资产展开成可选组合。</summary>
    private async Task RefreshPackAsync(bool forceReload = false)
    {
        if (_packLoading || (_packLoaded && !forceReload)) return;
        _packLoading = true;
        PackStatusText.Text = "正在获取 Lyra 整合包列表…";
        try
        {
            var releases = await DownloadService.FetchLyraReleasesAsync();
            var release = releases.FirstOrDefault(r => r.Assets.Count > 0);
            _packOptions.Clear();

            if (release != null)
            {
                foreach (var asset in release.Assets.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var label = asset.Name;
                    var detail = $"{asset.SizeText} · {release.Tag}";
                    if (LyraPackName.TryParse(asset.Name, out var info))
                    {
                        label = $"Lyra {info.ComboLabel} · 游戏 {info.GameVersion} · 汉化 {info.ChsVersion}";
                        detail = $"{asset.SizeText} · {asset.Name}";
                    }

                    _packOptions.Add(new PackOption
                    {
                        Label = label,
                        Detail = detail,
                        Asset = asset,
                        Release = release,
                    });
                }
            }

            _packLoaded = true;
            PackStatusText.Text = _packOptions.Count > 0
                ? $"共 {_packOptions.Count} 个整合包"
                : "没有找到 Lyra 整合包";
            if (_packOptions.Count > 0) PackVariantList.SelectedIndex = 0;
            Log($"已获取 {_packOptions.Count} 个 Lyra 整合包（{release?.Tag ?? "无 Release"}）");
        }
        catch (Exception ex)
        {
            _packLoaded = false;
            PackStatusText.Text = "获取失败：" + ex.Message;
            LogError("获取 Lyra 整合包列表失败", ex);
        }
        finally
        {
            _packLoading = false;
        }
    }

    private void PackType_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 目前只有 Lyra 一个来源；保留事件以便后续扩展
    }

    private async void DownloadPack_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadBusy) return;
        if (PackVariantList.SelectedItem is not PackOption option)
        {
            AppDialog.Info("请先选择一个整合包版本。", "整合包下载");
            return;
        }

        if (!AppDialog.Confirm(
                $"将下载「{option.Label}」（{option.Asset.SizeText}）并安装为独立版本。\n\n" +
                "安装后可以选择把普通模组解压到 mods/，ModLoader 必需组件会保留在内置。继续吗？",
                "下载整合包", "开始下载")) return;

        SetDownloadBusy(true);
        PackProgress.Visibility = Visibility.Visible;
        PackProgress.Value = 0;
        PackStatusText.Text = "0%";
        try
        {
            Log($"开始下载整合包：{option.Asset.Name}");
            var folder = await DownloadService.InstallLyraAsync(option.Asset, Log, p =>
            {
                PackProgress.Value = p;
                PackStatusText.Text = $"{p:P0}";
            });
            PackProgress.Value = 1;
            PackStatusText.Text = "安装完成";
            Log($"整合包安装完成：versions\\{folder}");
            ReloadVersions(folder);
            NavVersions.IsChecked = true;

            var versionDir = Path.Combine(Paths.VersionsDir, folder);
            var embedded = await Task.Run(() => LyraPackService.ReadEmbeddedMods(versionDir, Log));
            var extractable = embedded.Count(m => !m.IsBuiltin);
            if (extractable > 0 && AppDialog.Confirm(
                    $"整合包已安装，其中 {extractable} 个普通模组可以解压到 mods/ 以便启停和排序。\n" +
                    $"ModLoader 必需组件（{embedded.Count - extractable} 个）会保留在内置。现在解压吗？",
                    "解压内置模组", "解压到 mods"))
            {
                var result = await Task.Run(() => LyraPackService.ExtractToMods(versionDir, Log));
                ReloadVersions(folder);
                Log($"内置模组已解压：{result.Extracted} 个；{result.Skipped} 个必需组件保留内置。");
            }
        }
        catch (OperationCanceledException)
        {
            PackStatusText.Text = "已取消";
            Log("整合包下载已取消");
        }
        catch (Exception ex)
        {
            PackStatusText.Text = "安装失败";
            LogError("整合包下载/安装失败", ex);
            AppDialog.Error(ex.Message, "下载失败");
        }
        finally
        {
            SetDownloadBusy(false);
        }
    }

    private async Task RefreshMarketAsync(bool forceRefresh = false)
    {
        if (_marketLoading) return;
        _marketLoading = true;
        MarketStatusText.Text = "正在获取市场索引…";
        try
        {
            var catalog = await DownloadService.FetchMarketAsync(Log, forceRefresh);
            _marketCatalog = catalog;
            _marketAll.Clear();
            foreach (var mod in catalog.Mods.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
                _marketAll.Add(mod);

            _categories.Clear();
            _categories.Add("全部分类");
            foreach (var category in catalog.Categories) _categories.Add(category);
            if (MarketCategoryBox.SelectedItem == null) MarketCategoryBox.SelectedIndex = 0;

            RefreshMarketStatus();
            ApplyMarketFilter();
            _marketLoaded = true;
            MarketStatusText.Text = "";
            MarketHintText.Text = $"索引来自 ModHub 市场，共 {_marketAll.Count} 个模组；已安装的会标出状态，缺少的前置可以自动补装。";
            Log($"市场索引就绪：{_marketAll.Count} 个模组" +
                (catalog.GeneratedAt.Length > 0 ? $"（生成于 {catalog.GeneratedAt}）" : ""));
            // 索引到位后才能把本地模组对到市场条目，这里补做一次可能冲突检查
            _ = CheckConflictsAsync();
        }
        catch (Exception ex)
        {
            _marketLoaded = false;
            MarketStatusText.Text = "获取失败：" + ex.Message;
            LogError("获取市场索引失败", ex);
        }
        finally
        {
            _marketLoading = false;
        }
    }

    /// <summary>搜索框输入防抖：停止输入一小段时间后才真正筛选，避免每个字符都重算。</summary>
    private DispatcherTimer? _marketSearchDebounce;

    /// <summary>筛选序号：异步算完后若已有更新的一次请求，就丢弃旧结果。</summary>
    private int _marketFilterSeq;

    private async void ApplyMarketFilter()
    {
        if (MarketCountText == null) return;
        var seq = ++_marketFilterSeq;
        // 参数与数据快照都在 UI 线程取，后台只做纯计算
        var category = MarketCategoryBox.SelectedItem as string;
        var filter = MarketFilterBox.SelectedIndex;
        var match = ParseMatchMode(TagOf(MarketMatchBox));
        var scope = ParseSearchScope(TagOf(MarketScopeBox));
        var sort = ParseSortMode(TagOf(MarketSortBox));
        var descending = MarketSortDescCheck?.IsChecked == true;
        var keyword = MarketSearchBox.Text;
        var snapshot = _marketAll.ToList();

        var result = await Task.Run(() =>
        {
            var kept = new List<MarketMod>();
            foreach (var mod in MarketSearch.Filter(snapshot, keyword, match, scope))
            {
                if (category != null && category != "全部分类" &&
                    !mod.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
                if (filter == 1 && mod.Installed) continue;
                if (filter == 2 && !mod.UpdateAvailable) continue;
                kept.Add(mod);
            }
            return MarketSearch.Sort(kept, sort, descending);
        });

        // 期间又有新的筛选请求，放弃这次结果
        if (seq != _marketFilterSeq) return;

        _marketView.Clear();
        foreach (var mod in result)
            _marketView.Add(new MarketItemVm(mod, _marketChecked.Contains(MarketKey(mod))));

        MarketCountText.Text = $"{_marketView.Count} / {_marketAll.Count} 个";
        if (_marketView.Count > 0 && MarketList.SelectedItem == null) MarketList.SelectedIndex = 0;
    }

    private void LoadMarketSearchPrefs()
    {
        _loadingMarketPrefs = true;
        try
        {
            var g = AppConfig.Load().General;
            SelectComboTag(MarketScopeBox, g.MarketSearchScope);
            RebuildMatchItems(g.MarketMatchMode);
            SelectComboTag(MarketSortBox, g.MarketSortMode);
            MarketSortDescCheck.IsChecked = g.MarketSortDesc;
        }
        finally
        {
            _loadingMarketPrefs = false;
        }
    }

    private void SaveMarketSearchPrefs()
    {
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.MarketMatchMode = TagOf(MarketMatchBox);
            cfg.General.MarketSearchScope = TagOf(MarketScopeBox);
            cfg.General.MarketSortMode = TagOf(MarketSortBox);
            cfg.General.MarketSortDesc = MarketSortDescCheck.IsChecked == true;
            AppConfig.Save(cfg);
        }
        catch (Exception ex)
        {
            LogError("保存搜索设置失败", ex);
        }
    }

    private void MarketAdvanced_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMarketPrefs) return;

        // 范围切到 / 切离「标签」时，匹配模式的可选项会变化（标签范围下部分匹配是多选一的二级菜单）
        var tagScope = ParseSearchScope(TagOf(MarketScopeBox)) == MarketSearchScope.Tags;
        if (tagScope != _matchItemsTagScope)
        {
            _loadingMarketPrefs = true;
            try
            {
                RebuildMatchItems(tagScope ? "tagany" : "contains");
            }
            finally
            {
                _loadingMarketPrefs = false;
            }
        }

        SaveMarketSearchPrefs();
        ApplyMarketFilter();
    }

    /// <summary>
    /// 重建匹配模式下拉：普通范围只有「部分匹配 / 完全匹配」；标签范围下
    /// 「部分匹配」带一个二级菜单，可选「含全部标签 / 含任一标签」。
    /// </summary>
    private void RebuildMatchItems(string? preferredTag)
    {
        var tagScope = ParseSearchScope(TagOf(MarketScopeBox)) == MarketSearchScope.Tags;
        _matchItemsTagScope = tagScope;

        var exact = new ComboBoxItem { Content = "完全匹配", Tag = "exact" };
        var partial = tagScope
            ? CreateTagPartialItem(preferredTag == "tagall")
            : new ComboBoxItem { Content = "部分匹配", Tag = "contains" };

        MarketMatchBox.Items.Clear();
        MarketMatchBox.Items.Add(partial);
        MarketMatchBox.Items.Add(exact);

        MarketMatchBox.SelectedItem = preferredTag == "exact" ? exact : partial;

        // 悬停到其它项、或收起下拉时关掉二级菜单
        exact.MouseEnter += (_, _) =>
        {
            if (_tagPartialPopup != null) _tagPartialPopup.IsOpen = false;
        };
        if (!_tagPopupHooked)
        {
            _tagPopupHooked = true;
            MarketMatchBox.DropDownClosed += (_, _) =>
            {
                if (_tagPartialPopup != null) _tagPartialPopup.IsOpen = false;
            };
        }

        if (_tagPartialPopup != null) _tagPartialPopup.IsOpen = false;
    }

    /// <summary>
    /// 标签范围下的「部分匹配」项：下拉里由 SubmenuComboItem 样式画一个 ›，
    /// Content 保持纯文本，收起后右上角选中项只显示文字，不会带箭头。
    /// </summary>
    private ComboBoxItem CreateTagPartialItem(bool all)
    {
        var item = new ComboBoxItem
        {
            Content = TagPartialText(all),
            Tag = all ? "tagall" : "tagany",
            Style = TryFindResource("SubmenuComboItem") as Style,
        };
        item.MouseEnter += (_, _) => ShowTagPartialMenu(item);
        // 鼠标不方便悬停时（例如触控板/键盘操作），点一下也能展开二级菜单，且不收起下拉
        item.PreviewMouseLeftButtonUp += (_, e) =>
        {
            ShowTagPartialMenu(item);
            e.Handled = true;
        };
        return item;
    }

    private static string TagPartialText(bool all) => all ? "部分匹配 · 含全部" : "部分匹配 · 含任一";

    /// <summary>悬停「部分匹配」项时，在右侧弹出二级菜单。</summary>
    private void ShowTagPartialMenu(ComboBoxItem item)
    {
        if (_tagPartialPopup == null)
        {
            var panel = new StackPanel();
            panel.Children.Add(CreateTagPartialOption("含全部搜索标签",
                "结果的标签包含所有搜索标签，允许还有其它标签", true));
            panel.Children.Add(CreateTagPartialOption("含任一搜索标签",
                "结果只要命中任意一个搜索标签就算匹配", false));

            _tagPartialPopup = new Popup
            {
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Placement = PlacementMode.Right,
                HorizontalOffset = 4,
                Child = new Border
                {
                    Background = TryFindResource("PanelBrush") as Brush ?? Brushes.White,
                    BorderBrush = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(4),
                    Child = panel,
                },
            };
        }

        _pendingPartialItem = item;
        _tagPartialPopup.PlacementTarget = item;
        _tagPartialPopup.IsOpen = true;
    }

    /// <summary>
    /// 二级菜单里的一行。用 Border 而不是 Button：FlatButton 模板会把内容居中，
    /// 两行文字宽度不同时看起来就不齐了，这里直接左对齐并自己处理悬停反馈。
    /// </summary>
    private Border CreateTagPartialOption(string title, string description, bool all)
    {
        var content = new StackPanel();
        var titleText = new TextBlock { Text = title };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        content.Children.Add(titleText);
        var descText = new TextBlock
        {
            Text = description,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        descText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        content.Children.Add(descText);

        var hoverBrush = TryFindResource("NavHoverBrush") as Brush ?? Brushes.Transparent;
        var row = new Border
        {
            Child = content,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
        };
        row.MouseEnter += (_, _) => row.Background = hoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => SelectTagPartial(all);
        return row;
    }

    private void SelectTagPartial(bool all)
    {
        if (_tagPartialPopup != null) _tagPartialPopup.IsOpen = false;
        MarketMatchBox.IsDropDownOpen = false;
        if (_pendingPartialItem == null) return;

        _pendingPartialItem.Tag = all ? "tagall" : "tagany";
        _pendingPartialItem.Content = TagPartialText(all);

        _loadingMarketPrefs = true;
        try
        {
            MarketMatchBox.SelectedItem = _pendingPartialItem;
        }
        finally
        {
            _loadingMarketPrefs = false;
        }

        SaveMarketSearchPrefs();
        ApplyMarketFilter();
    }

    private void MarketSortDesc_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingMarketPrefs) return;
        SaveMarketSearchPrefs();
        ApplyMarketFilter();
    }

    private static string TagOf(ComboBox? box) => (box?.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private static void SelectComboTag(ComboBox box, string? tag)
    {
        var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == tag);
        if (item != null) box.SelectedItem = item;
        else if (box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private static MarketMatchMode ParseMatchMode(string tag) => tag switch
    {
        "exact" => MarketMatchMode.Exact,
        "tagall" => MarketMatchMode.TagAll,
        "tagany" => MarketMatchMode.TagAny,
        _ => MarketMatchMode.Contains,
    };

    private static MarketSearchScope ParseSearchScope(string tag) => tag switch
    {
        "title" => MarketSearchScope.Title,
        "author" => MarketSearchScope.Author,
        "description" => MarketSearchScope.Description,
        "tag" => MarketSearchScope.Tags,
        _ => MarketSearchScope.All,
    };

    private static MarketSortMode ParseSortMode(string tag) => tag switch
    {
        "updated" => MarketSortMode.UpdateDate,
        "name" => MarketSortMode.Name,
        _ => MarketSortMode.Default,
    };

    /// <summary>搜索框防抖：连续输入时只在停顿后触发一次筛选。</summary>
    private void MarketSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_marketSearchDebounce == null)
        {
            _marketSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _marketSearchDebounce.Tick += (_, _) =>
            {
                _marketSearchDebounce!.Stop();
                ApplyMarketFilter();
            };
        }
        _marketSearchDebounce.Stop();
        _marketSearchDebounce.Start();
    }

    private void MarketCategory_Changed(object sender, SelectionChangedEventArgs e) => ApplyMarketFilter();

    private void MarketFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyMarketFilter();

    private void MarketTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        RefreshMarketStatus();
        ApplyMarketFilter();
    }

    /// <summary>用目标版本磁盘上的模组刷新“已安装 / 可更新”标记。</summary>
    private void RefreshMarketStatus()
    {
        if (_marketAll.Count == 0) return;
        var target = MarketTargetBox.SelectedItem as VersionVm;
        var installed = target != null && !target.Info.IsFileMode
            ? ModService.Load(target.Info.FullPath)
            : new List<ModInfo>();

        foreach (var mod in _marketAll)
        {
            var version = DownloadService.FindInstalledVersion(mod, installed);
            mod.InstalledVersion = version;
            mod.UpdateAvailable = version.Length > 0 && VersionUtil.Compare(version, mod.Version) < 0;
        }
    }

    private void MarketList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _marketReleases.Clear();
        MarketEditionBox.ItemsSource = null;
        MarketEditionPanel.Visibility = Visibility.Collapsed;
        MarketAssetBox.ItemsSource = null;
        _assetVersionToken++;

        var mod = (MarketList.SelectedItem as MarketItemVm)?.Mod;
        if (mod == null)
        {
            // 点空白处取消选中后，顺带清掉上一个模组的版本提示
            MarketStatusText.Text = "";
            return;
        }

        _marketPicks.TryGetValue(MarketKey(mod), out var remembered);
        if (mod.Editions.Count > 1)
        {
            MarketEditionBox.ItemsSource = mod.Editions;
            var editionIndex = 0;
            if (remembered != null)
            {
                var match = mod.Editions.FindIndex(x =>
                    string.Equals(x.Id, remembered.EditionId, StringComparison.OrdinalIgnoreCase));
                if (match >= 0) editionIndex = match;
            }
            MarketEditionBox.SelectedIndex = editionIndex;
            MarketEditionPanel.Visibility = Visibility.Visible;
        }

        if (mod.Installed && mod.Dependencies.Count > 0)
        {
            MarketStatusText.Text = "前置：" + string.Join("、", mod.Dependencies.Select(d => d.Id));
        }
        else
        {
            MarketStatusText.Text = "";
        }

        if (_releaseCache.TryGetValue(mod.Repo, out var cached))
        {
            foreach (var release in cached) _marketReleases.Add(release);
            if (_marketReleases.Count > 0) MarketVersionBox.SelectedIndex = ReleaseIndexFor(mod);
            return;
        }

        // 选中模组后自动拉取该模组的版本列表，不用再点「加载版本」
        if (mod.Repo.Length > 0) _ = LoadModReleasesAsync(mod, true);
    }

    private async void LoadModReleases_Click(object sender, RoutedEventArgs e)
    {
        var mod = (MarketList.SelectedItem as MarketItemVm)?.Mod;
        if (mod == null) return;
        await LoadModReleasesAsync(mod, false);
    }

    private async Task LoadModReleasesAsync(MarketMod mod, bool auto)
    {
        if (mod.Repo.Length == 0)
        {
            if (!auto) MarketStatusText.Text = "这个模组没有配置 GitHub 仓库。";
            return;
        }

        if (!auto)
        {
            MarketVersionBtn.IsEnabled = false;
            MarketStatusText.Text = "正在获取版本列表…";
        }

        try
        {
            if (!_releaseCache.TryGetValue(mod.Repo, out var list))
            {
                list = await DownloadService.FetchModReleasesAsync(mod);
                CacheMarketReleases(mod.Repo, list);
            }

            // 等待期间选中项可能已经变了，变了就不再覆盖下拉框
            if ((MarketList.SelectedItem as MarketItemVm)?.Mod is not { } current ||
                current.Repo != mod.Repo) return;

            _marketReleases.Clear();
            foreach (var release in list) _marketReleases.Add(release);
            if (_marketReleases.Count > 0)
            {
                MarketVersionBox.SelectedIndex = ReleaseIndexFor(mod);
                MarketStatusText.Text = $"共 {_marketReleases.Count} 个版本";
            }
            else
            {
                MarketStatusText.Text = "没有可下载的 Release";
            }
        }
        catch (Exception ex)
        {
            MarketStatusText.Text = "获取版本失败：" + ex.Message;
            LogError("获取模组版本失败", ex);
        }
        finally
        {
            if (!auto) MarketVersionBtn.IsEnabled = true;
        }
    }

    private async void DownloadGame_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadBusy) return;
        var release = GameReleaseList.SelectedItem as GameRelease;
        if (release == null)
        {
            AppDialog.Info("请先在上面的列表选择一个 Release。", "下载中心");
            return;
        }

        var usePolyfill = release.Source.IsZip && PolyfillCheck.IsChecked == true;
        if (release.PickAsset(usePolyfill) == null)
        {
            AppDialog.Info(release.Source.IsZip ? "这个 Release 没有可用的游戏压缩包。" : "这个 Release 没有可用的 HTML 文件。",
                "下载中心");
            return;
        }

        var extras = new List<string>();
        if (I18nCheck.IsChecked == true) extras.Add("ModI18N");
        if (ImagePackCheck.IsChecked == true) extras.Add("图片包");
        if (!AppDialog.Confirm(
                $"将下载并安装「{release.Title}」" + (usePolyfill ? "（polyfill）" : "") + "。\n" +
                (extras.Count > 0 ? "同时安装：" + string.Join("、", extras) + "。\n" : "") +
                "\n下载体积较大，安装完成后会出现在左侧版本列表。继续吗？",
                "下载游戏本体", "开始下载")) return;

        SetDownloadBusy(true);
        _gameProgressDismissSeq++;
        GameProgress.Visibility = Visibility.Visible;
        GameProgress.Value = 0;
        GameStatusText.Text = "0%";
        try
        {
            Log($"开始下载本体：{release.Title}");
            var folder = await DownloadService.InstallGameAsync(release, usePolyfill,
                I18nCheck.IsChecked == true, ImagePackCheck.IsChecked == true,
                Log, p =>
                {
                    GameProgress.Value = p;
                    GameStatusText.Text = $"{p:P0}";
                });
            GameProgress.Value = 1;
            GameStatusText.Text = "安装完成";
            Log($"本体安装完成：versions\\{folder}");
            ReloadVersions(folder);
            NavVersions.IsChecked = true;
            ScheduleGameProgressDismiss();
        }
        catch (OperationCanceledException)
        {
            GameStatusText.Text = "已取消";
            Log("本体下载已取消");
        }
        catch (Exception ex)
        {
            GameStatusText.Text = "安装失败";
            LogError("本体下载/安装失败", ex);
            AppDialog.Error(ex.Message, "下载失败");
        }
        finally
        {
            SetDownloadBusy(false);
        }
    }

    /// <summary>本体安装完成后 3 秒收起进度条并归零；期间若开始新下载则放弃。</summary>
    private async void ScheduleGameProgressDismiss()
    {
        var seq = ++_gameProgressDismissSeq;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (seq != _gameProgressDismissSeq) return;
        GameProgress.Value = 0;
        GameProgress.Visibility = Visibility.Collapsed;
    }

    private async void DownloadMod_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadBusy) return;

        var target = MarketTargetBox.SelectedItem as VersionVm;
        if (target == null || target.Info.IsFileMode)
        {
            AppDialog.Info("请先在左侧「安装到版本」里选中一个磁盘版本。", "下载中心");
            return;
        }

        // 有勾选项时走批量安装；被搜索过滤掉的勾选项也照样安装。
        // 顺序按当前排序，过滤掉的排在后面。
        var checkedMods = _marketView.Select(v => v.Mod)
            .Where(m => _marketChecked.Contains(MarketKey(m)))
            .ToList();
        foreach (var m in _marketAll)
        {
            if (_marketChecked.Contains(MarketKey(m)) && !checkedMods.Contains(m)) checkedMods.Add(m);
        }
        if (checkedMods.Count > 0)
        {
            await InstallCheckedMarketModsAsync(checkedMods, target);
            return;
        }

        var mod = (MarketList.SelectedItem as MarketItemVm)?.Mod;
        if (mod == null)
        {
            AppDialog.Info("请先在列表里选择一个模组。", "下载中心");
            return;
        }

        await InstallMarketModAsync(mod, target, MarketVersionBox.SelectedItem as GameRelease, null,
            MarketEditionBox.SelectedItem as MarketEdition,
            (MarketAssetBox.SelectedItem as ModAssetOption)?.AssetName);
    }

    private void MarketVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RememberMarketPick();
        _ = RefreshMarketAssetVersionsAsync();
    }

    private void MarketEdition_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RememberMarketPick();
        _ = RefreshMarketAssetVersionsAsync();
    }

    private void MarketAsset_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RememberMarketPick();

    /// <summary>按当前选中的 Release / 包型，加载可选的具体资产版本（优先上次记住的包文件名）。</summary>
    private async Task RefreshMarketAssetVersionsAsync()
    {
        var mod = (MarketList.SelectedItem as MarketItemVm)?.Mod;
        var release = MarketVersionBox.SelectedItem as GameRelease;
        var token = ++_assetVersionToken;

        MarketAssetBox.ItemsSource = null;
        if (mod == null || mod.Repo.Length == 0 || release == null) return;

        try
        {
            var list = await DownloadService.FetchModAssetVersionsAsync(
                mod, MarketEditionBox.SelectedItem as MarketEdition, release.Tag);
            if (token != _assetVersionToken ||
                (MarketList.SelectedItem as MarketItemVm)?.Mod != mod) return;
            MarketAssetBox.ItemsSource = list;
            if (list.Count > 0)
            {
                var want = _marketPicks.TryGetValue(MarketKey(mod), out var pick) &&
                           string.Equals(pick.ReleaseTag, release.Tag, StringComparison.OrdinalIgnoreCase)
                    ? pick.AssetName
                    : "";
                var index = 0;
                if (want.Length > 0)
                {
                    var match = list.FindIndex(a =>
                        string.Equals(a.AssetName, want, StringComparison.OrdinalIgnoreCase));
                    if (match >= 0) index = match;
                }
                MarketAssetBox.SelectedIndex = index;
            }
            RememberMarketPick();
        }
        catch (Exception ex)
        {
            LogError("获取包版本失败", ex);
        }
    }

    private async Task InstallMarketModAsync(MarketMod mod, VersionVm target, GameRelease? preferred,
        Action<string>? status, MarketEdition? edition = null, string? assetName = null)
    {
        void SetStatus(string text)
        {
            MarketStatusText.Text = text;
            status?.Invoke(text);
        }

        SetDownloadBusy(true);
        InstallCheckResult check;
        try
        {
            SetStatus("正在检查前置与冲突…");
            check = await DownloadService.CheckInstallAsync(mod, _marketCatalog ?? new MarketCatalog(),
                target.Info.FullPath, target.Info.GameVersion, Log, edition);
        }
        catch (Exception ex)
        {
            LogError("安装前检查失败", ex);
            check = new InstallCheckResult();
        }

        var dialog = new InstallCheckDialog(mod, target.Info, check) { Owner = this };
        SetStatus(string.Empty);
        if (dialog.ShowDialog() != true)
        {
            SetDownloadBusy(false);
            return;
        }

        // 先前置、后本体；用户在对话框里取消勾选的前置不会安装
        var plan = dialog.SelectedDependencies.Concat(new[] { mod }).ToList();

        MarketProgress.Visibility = Visibility.Visible;
        MarketProgress.Value = 0;
        SetStatus("0%");
        MarkInternalModsChange();
        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                var item = plan[i];
                SetStatus($"正在安装 {i + 1}/{plan.Count}：{item.Name}");
                Log($"开始下载模组：{item.Title}" + (item == mod && preferred != null ? $"（{preferred.Tag}）" : ""));
                var tag = item == mod && preferred != null ? preferred.Tag : null;
                var useEdition = item == mod ? edition : null;
                var useAsset = item == mod ? assetName : null;
                var file = await DownloadService.InstallModAsync(item, target.Info.FullPath, Log, p =>
                {
                    var overall = (i + p) / Math.Max(1, plan.Count);
                    MarketProgress.Value = overall;
                    SetStatus($"{overall:P0}　{ item.Name}");
                }, tag, useEdition, useAsset);
                Log("模组安装完成：" + file);
            }

            RefreshMarketStatus();
            ApplyMarketFilter();
            if (_selected != null && _selected.Info.Folder == target.Info.Folder)
                LoadMods(_selected.Info);
            // 放在刷新列表之后，避免 SelectionChanged 把完成提示清掉
            SetStatus($"安装完成（{plan.Count} 个）");
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消");
            Log("模组下载已取消");
        }
        catch (Exception ex)
        {
            SetStatus("安装失败");
            LogError("模组下载/安装失败", ex);
            AppDialog.Error(ex.Message, "下载失败");
        }
        finally
        {
            // 装完就把进度条收起来，只留下面那行状态文字
            MarketProgress.Value = 0;
            MarketProgress.Visibility = Visibility.Collapsed;
            SetDownloadBusy(false);
        }
    }

    // ---------- 批量安装 ----------

    private sealed record MarketInstallRequest(MarketMod Mod, InstallCheckResult Check,
        string ReleaseTag, MarketEdition? Edition, string AssetName);

    /// <summary>批量安装：逐个检查前置，合成一个检查窗口，再按顺序安装。</summary>
    private async Task InstallCheckedMarketModsAsync(List<MarketMod> mods, VersionVm target)
    {
        SetDownloadBusy(true);
        try
        {
            var requests = new List<MarketInstallRequest>();
            foreach (var mod in mods)
            {
                MarketStatusText.Text = $"正在检查「{mod.Name}」…";
                var pick = await ResolveMarketPickAsync(mod);
                InstallCheckResult check;
                try
                {
                    check = await DownloadService.CheckInstallAsync(mod, _marketCatalog ?? new MarketCatalog(),
                        target.Info.FullPath, target.Info.GameVersion, Log, pick.Edition);
                }
                catch (Exception ex)
                {
                    LogError("安装前检查失败", ex);
                    check = new InstallCheckResult();
                }
                requests.Add(new MarketInstallRequest(mod, check, pick.ReleaseTag, pick.Edition, pick.AssetName));
            }

            requests = RemoveNestedDependencyRequests(requests);
            var dialog = new InstallCheckDialog(
                requests.Select(r => new InstallCheckItem(r.Mod, r.Check)).ToList(),
                target.Info) { Owner = this };
            MarketStatusText.Text = "";
            if (dialog.ShowDialog() != true) return;

            await RunMarketInstallAsync(requests, target);
        }
        finally
        {
            SetDownloadBusy(false);
        }
    }

    /// <summary>显式勾选的模组同时是另一个勾选模组的前置时，从顶层移除，避免重复列两次。</summary>
    private static List<MarketInstallRequest> RemoveNestedDependencyRequests(List<MarketInstallRequest> requests)
    {
        var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            foreach (var dependency in request.Check.Dependencies.Where(d => d.Selected))
                nested.Add(MarketKey(dependency.Mod));
        }

        var kept = requests.Where(r => !nested.Contains(MarketKey(r.Mod))).ToList();
        return kept.Count > 0 ? kept : requests;
    }

    /// <summary>把内存里记住的 Release / 包型 / 包文件名，对齐到刚拉到的列表；没有记录则取最新。</summary>
    private async Task<(string ReleaseTag, MarketEdition? Edition, string AssetName)> ResolveMarketPickAsync(
        MarketMod mod)
    {
        _marketPicks.TryGetValue(MarketKey(mod), out var pick);
        if (mod.Repo.Length == 0) return (pick?.ReleaseTag ?? "", null, pick?.AssetName ?? "");

        if (!_releaseCache.TryGetValue(mod.Repo, out var releases))
        {
            try
            {
                releases = await DownloadService.FetchModReleasesAsync(mod);
                CacheMarketReleases(mod.Repo, releases);
            }
            catch (Exception ex)
            {
                LogError("获取模组版本失败", ex);
                return (pick?.ReleaseTag ?? "", null, pick?.AssetName ?? "");
            }
        }

        var release = releases.FirstOrDefault(r => pick != null &&
                string.Equals(r.Tag, pick.ReleaseTag, StringComparison.OrdinalIgnoreCase))
            ?? releases.FirstOrDefault();
        if (release == null) return (pick?.ReleaseTag ?? "", null, pick?.AssetName ?? "");

        var edition = mod.Editions.FirstOrDefault(e => pick != null &&
                string.Equals(e.Id, pick.EditionId, StringComparison.OrdinalIgnoreCase))
            ?? mod.Editions.FirstOrDefault();

        var asset = "";
        try
        {
            var list = await DownloadService.FetchModAssetVersionsAsync(mod, edition, release.Tag);
            asset = list.FirstOrDefault(a => pick != null &&
                        string.Equals(a.AssetName, pick.AssetName, StringComparison.OrdinalIgnoreCase))?.AssetName
                ?? list.FirstOrDefault()?.AssetName ?? "";
        }
        catch (Exception ex)
        {
            LogError("获取包版本失败", ex);
        }

        _marketPicks[MarketKey(mod)] = new MarketPick(release.Tag, edition?.Id ?? "", asset);
        return (release.Tag, edition, asset);
    }

    private async Task RunMarketInstallAsync(List<MarketInstallRequest> requests, VersionVm target)
    {
        // 每个模组先装它勾选的前置，再装本体
        var plan = new List<(MarketMod Mod, MarketInstallRequest? Request)>();
        foreach (var request in requests)
        {
            foreach (var dependency in request.Check.Dependencies.Where(d => d.Selected))
                plan.Add((dependency.Mod, null));
            plan.Add((request.Mod, request));
        }

        void SetStatus(string text) => MarketStatusText.Text = text;

        MarketProgress.Visibility = Visibility.Visible;
        MarketProgress.Value = 0;
        SetStatus("0%");
        MarkInternalModsChange();
        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                var (item, request) = plan[i];
                SetStatus($"正在安装 {i + 1}/{plan.Count}：{item.Name}");
                Log($"开始下载模组：{item.Title}" +
                    (request is { ReleaseTag.Length: > 0 } ? $"（{request.ReleaseTag}）" : ""));
                var file = await DownloadService.InstallModAsync(item, target.Info.FullPath, Log, p =>
                {
                    var overall = (i + p) / Math.Max(1, plan.Count);
                    MarketProgress.Value = overall;
                    SetStatus($"{overall:P0}　{item.Name}");
                }, request?.ReleaseTag, request?.Edition, request?.AssetName);
                Log("模组安装完成：" + file);
            }

            _marketChecked.Clear();
            RefreshMarketStatus();
            ApplyMarketFilter();
            UpdateMarketBatchHint();
            if (_selected != null && _selected.Info.Folder == target.Info.Folder)
                LoadMods(_selected.Info);
            // 放在刷新列表之后，避免 SelectionChanged 把完成提示清掉
            SetStatus($"安装完成（{plan.Count} 个）");
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消");
            Log("模组下载已取消");
        }
        catch (Exception ex)
        {
            SetStatus("安装失败");
            LogError("模组下载/安装失败", ex);
            AppDialog.Error(ex.Message, "下载失败");
        }
        finally
        {
            MarketProgress.Value = 0;
            MarketProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDownloadTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateDownloadEmptyHint();
        if (e.OldItems != null)
        {
            foreach (var old in e.OldItems.OfType<DownloadTaskVm>()) old.Finished -= OnDownloadTaskFinished;
        }
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is not { Count: > 0 } items) return;
        if (items[0] is not DownloadTaskVm task) return;
        task.Finished += OnDownloadTaskFinished;
        DownloadTaskList.SelectedItem = task;
        DownloadTaskList.ScrollIntoView(task);
    }

    /// <summary>下载列表为空时显示灰色空状态提示。</summary>
    private void UpdateDownloadEmptyHint()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateDownloadEmptyHint));
            return;
        }
        DownloadEmptyHint.Visibility = DownloadService.DownloadTasks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DownloadTaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems != null)
        {
            foreach (var old in e.RemovedItems.OfType<DownloadTaskVm>())
                old.PropertyChanged -= Task_CanCancelChanged;
        }
        if (e.AddedItems != null)
        {
            foreach (var added in e.AddedItems.OfType<DownloadTaskVm>())
                added.PropertyChanged += Task_CanCancelChanged;
        }
        UpdateCancelButton();
    }

    /// <summary>选中任务能取消时才显示底部按钮；完成 / 已取消时隐藏。</summary>
    private void UpdateCancelButton()
    {
        CancelDownloadBtn.Visibility = DownloadTaskList.SelectedItem is DownloadTaskVm { CanCancel: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Task_CanCancelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadTaskVm.CanCancel)) UpdateCancelButton();
    }

    /// <summary>完成或被取消的任务先留 3 秒，再移除条目。</summary>
    private void OnDownloadTaskFinished(object? sender, EventArgs e)
    {
        if (sender is not DownloadTaskVm task) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnDownloadTaskFinished(task, EventArgs.Empty)));
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RetireTaskItem(task);
        };
        timer.Start();
    }

    /// <summary>移除条目：开动画时向中心缩小并淡出；关闭动画时直接消失。</summary>
    private void RetireTaskItem(DownloadTaskVm task)
    {
        if (!UiAnimations.Active ||
            DownloadTaskList.ItemContainerGenerator.ContainerFromItem(task) is not ListBoxItem item)
        {
            DownloadService.DownloadTasks.Remove(task);
            return;
        }

        var scale = new ScaleTransform(1, 1);
        item.RenderTransformOrigin = new Point(0.5, 0.5);
        item.RenderTransform = scale;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(200);
        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => RemoveTaskWithShift(task);
        item.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.86, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.86, duration) { EasingFunction = ease });
    }

    /// <summary>移除后让下面的条目从原位平移到新位置（Y 过渡），避免生硬跳变。</summary>
    private void RemoveTaskWithShift(DownloadTaskVm task)
    {
        var items = DownloadService.DownloadTasks;
        var index = items.IndexOf(task);
        if (index < 0) return;

        var before = new List<(DownloadTaskVm Item, double Y)>();
        for (var i = index + 1; i < items.Count; i++)
        {
            if (DownloadTaskList.ItemContainerGenerator.ContainerFromItem(items[i]) is not ListBoxItem li) continue;
            before.Add((items[i], li.TransformToAncestor(DownloadTaskList).Transform(new Point(0, 0)).Y));
        }

        items.Remove(task);
        if (!UiAnimations.Active || before.Count == 0) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var (entry, oldY) in before)
            {
                if (DownloadTaskList.ItemContainerGenerator.ContainerFromItem(entry) is not ListBoxItem li) continue;
                var delta = oldY - li.TransformToAncestor(DownloadTaskList).Transform(new Point(0, 0)).Y;
                if (Math.Abs(delta) < 0.5) continue;
                if (li.RenderTransform is not TranslateTransform shift)
                {
                    shift = new TranslateTransform();
                    li.RenderTransform = shift;
                }
                shift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
            }
        }), DispatcherPriority.Loaded);
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadTaskList.SelectedItem is not DownloadTaskVm task)
        {
            AppDialog.Info("请先在下载列表里选择一个任务。", "下载列表");
            return;
        }
        if (!task.CanCancel)
        {
            AppDialog.Info($"「{task.Name}」当前状态是「{task.State}」，不能取消。", "下载列表");
            return;
        }
        task.Cancel();
        Log($"已取消下载：{task.Name}");
    }

    private void SetDownloadBusy(bool busy)
    {
        _downloadBusy = busy;
        GameDownloadBtn.IsEnabled = !busy;
        MarketDownloadBtn.IsEnabled = !busy;
        PackDownloadBtn.IsEnabled = !busy;
        RefreshMarketBtn.IsEnabled = !busy;
    }

}
