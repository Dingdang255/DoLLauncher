using System.Collections.ObjectModel;
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
    // ---------- 模组市场详情 ----------

    /// <summary>匹配模式当前是否为「标签范围」下的二级菜单形态。</summary>
    private bool _matchItemsTagScope;

    private bool _tagPopupHooked;
    private Popup? _tagPartialPopup;
    private ComboBoxItem? _pendingPartialItem;

    /// <summary>详情窗打开后按需拉取仓库 Star（有 24 小时缓存则直接用），取到就追加到信息行末尾。</summary>
    private async Task AppendStarAsync(TextBlock target, string repo)
    {
        var stars = await ModStarService.TryGetAsync(repo);
        if (stars == null) return;
        target.Text += $"　·　★ {FormatStars(stars.Value)}";
    }

    private static string FormatStars(int stars) =>
        stars >= 10000 ? $"{stars / 10000.0:0.#} 万" : stars.ToString();

    private void MarketList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MarketList.SelectedItem is MarketMod mod) ShowMarketDetail(mod);
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
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(homepage)
                    {
                        UseShellExecute = true,
                    });
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
            ToolTip = "选择历史版本，留空则安装最新版",
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
            Text = "版本", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 8, 0), Opacity = 0.7,
        });
        left.Children.Add(versionBox);

        actions.Children.Add(right);
        actions.Children.Add(left);
        footer.Children.Add(actions);
        footer.Children.Add(status);

        // 头部信息
        ComboBox? editionBox = null;
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
        if (mod.Installed)
        {
            header.Children.Add(new TextBlock
            {
                Text = mod.BadgeText,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = TryFindResource("AccentBrush") as System.Windows.Media.Brush,
            });
        }
        DockPanel.SetDock(header, Dock.Top);

        var readme = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 6, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Document = MarkdownService.Render("（正在读取 README……）"),
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
                    t => status.Text = t, editionBox?.SelectedItem as MarketEdition);
                if (status.Text == "准备中…") status.Text = "";
            }
            finally
            {
                install.IsEnabled = true;
            }
        };

        root.Children.Add(footer);
        root.Children.Add(header);
        root.Children.Add(readme);
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
        if (VersionPage == null || DownloadPage == null || BackupPage == null
            || DiagnosticPage == null || SettingsPage == null) return;
        var showDownload = NavDownload.IsChecked == true;
        var showBackup = NavBackup.IsChecked == true;
        var showDiagnostic = NavDiagnostic.IsChecked == true;
        var showSettings = NavSettings.IsChecked == true;

        VersionPage.Visibility = !showDownload && !showBackup && !showDiagnostic && !showSettings
            ? Visibility.Visible : Visibility.Collapsed;
        DownloadPage.Visibility = showDownload ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = showBackup ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticPage.Visibility = showDiagnostic ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;

        var shown = showSettings ? SettingsPage
            : showDiagnostic ? DiagnosticPage
            : showBackup ? BackupPage
            : showDownload ? DownloadPage : VersionPage;
        UiAnimations.ShowPage(shown);

        if (showDownload && !_gameLoaded) _ = RefreshGameReleasesAsync();
        if (showBackup) RefreshBackups();
        if (showDiagnostic)
        {
            SyncDiagTarget();
            LoadBackupSettings();
            RefreshGameLogStatus();
        }
    }

    private void DownloadNav_Checked(object sender, RoutedEventArgs e)
    {
        if (GamePane == null || ModPane == null) return;
        var showMod = DownloadNavMod.IsChecked == true;
        GamePane.Visibility = showMod ? Visibility.Collapsed : Visibility.Visible;
        ModPane.Visibility = showMod ? Visibility.Visible : Visibility.Collapsed;
        MarketTargetPanel.Visibility = showMod ? Visibility.Visible : Visibility.Collapsed;
        UiAnimations.ShowPage(showMod ? ModPane : GamePane);
        if (showMod && !_marketLoaded) _ = RefreshMarketAsync();
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
        if (ModPane.Visibility == Visibility.Visible) _ = RefreshMarketAsync();
        else _ = RefreshGameReleasesAsync();
    }

    /// <summary>按设置里的「启动时预取索引」时序，在后台拉取游戏本体与模组市场索引。</summary>
    private async Task PrefetchIndexesAsync()
    {
        var mode = AppConfig.Load().General.Prefetch;
        if (mode == "none") return;
        if (mode == "modFirst")
        {
            await RefreshMarketAsync();
            await RefreshGameReleasesAsync();
            return;
        }
        if (mode == "gameFirst")
        {
            await RefreshGameReleasesAsync();
            await RefreshMarketAsync();
            return;
        }
        await Task.WhenAll(RefreshMarketAsync(), RefreshGameReleasesAsync());
    }

    private async Task RefreshGameReleasesAsync()
    {
        if (_gameLoading) return;
        _gameLoading = true;
        GameStatusText.Text = "正在获取 Release 列表…";
        try
        {
            var list = await DownloadService.FetchGameReleasesAsync();
            _gameReleases.Clear();
            foreach (var release in list.Where(r => r.StandardAsset != null || r.PolyfillAsset != null))
                _gameReleases.Add(release);
            if (_gameReleases.Count > 0) GameReleaseList.SelectedIndex = 0;
            _gameLoaded = true;
            GameStatusText.Text = $"共 {_gameReleases.Count} 个 Release";
            Log($"已获取 {_gameReleases.Count} 个 Release");
        }
        catch (Exception ex)
        {
            _gameLoaded = false;
            GameStatusText.Text = "获取失败：" + ex.Message;
            Log("获取本体列表失败：" + ex.Message);
        }
        finally
        {
            _gameLoading = false;
        }
    }

    private async Task RefreshMarketAsync()
    {
        if (_marketLoading) return;
        _marketLoading = true;
        MarketStatusText.Text = "正在获取市场索引…";
        try
        {
            var catalog = await DownloadService.FetchMarketAsync(Log);
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
            Log("获取市场索引失败：" + ex.Message);
        }
        finally
        {
            _marketLoading = false;
        }
    }

    private void ApplyMarketFilter()
    {
        if (MarketCountText == null) return;
        var category = MarketCategoryBox.SelectedItem as string;
        var filter = MarketFilterBox.SelectedIndex;
        var match = ParseMatchMode(TagOf(MarketMatchBox));
        var scope = ParseSearchScope(TagOf(MarketScopeBox));
        var sort = ParseSortMode(TagOf(MarketSortBox));
        var descending = MarketSortDescCheck?.IsChecked == true;

        var kept = new List<MarketMod>();
        foreach (var mod in MarketSearch.Filter(_marketAll, MarketSearchBox.Text, match, scope))
        {
            if (category != null && category != "全部分类" &&
                !mod.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
            if (filter == 1 && mod.Installed) continue;
            if (filter == 2 && !mod.UpdateAvailable) continue;
            kept.Add(mod);
        }

        _marketView.Clear();
        foreach (var mod in MarketSearch.Sort(kept, sort, descending))
            _marketView.Add(mod);

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
            Log("保存搜索设置失败：" + ex.Message);
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

    private void MarketSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyMarketFilter();

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

        var mod = MarketList.SelectedItem as MarketMod;
        if (mod == null)
        {
            // 点空白处取消选中后，顺带清掉上一个模组的版本提示
            MarketStatusText.Text = "";
            return;
        }

        if (mod.Editions.Count > 1)
        {
            MarketEditionBox.ItemsSource = mod.Editions;
            MarketEditionBox.SelectedIndex = 0;
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
            if (_marketReleases.Count > 0) MarketVersionBox.SelectedIndex = 0;
            return;
        }

        // 选中模组后自动拉取该模组的版本列表，不用再点「加载版本」
        if (mod.Repo.Length > 0) _ = LoadModReleasesAsync(mod, true);
    }

    private async void LoadModReleases_Click(object sender, RoutedEventArgs e)
    {
        var mod = MarketList.SelectedItem as MarketMod;
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
                _releaseCache[mod.Repo] = list;
            }

            // 等待期间选中项可能已经变了，变了就不再覆盖下拉框
            if (MarketList.SelectedItem is not MarketMod current || current.Repo != mod.Repo) return;

            _marketReleases.Clear();
            foreach (var release in list) _marketReleases.Add(release);
            if (_marketReleases.Count > 0)
            {
                MarketVersionBox.SelectedIndex = 0;
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
            Log("获取模组版本失败：" + ex.Message);
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

        var usePolyfill = PolyfillCheck.IsChecked == true;
        if (release.PickAsset(usePolyfill) == null)
        {
            AppDialog.Info("这个 Release 没有可用的游戏压缩包。", "下载中心");
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
        }
        catch (Exception ex)
        {
            GameStatusText.Text = "安装失败";
            Log("本体下载/安装失败：" + ex.Message);
            AppDialog.Error(ex.Message, "下载失败");
        }
        finally
        {
            SetDownloadBusy(false);
        }
    }

    private async void DownloadMod_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadBusy) return;
        var mod = MarketList.SelectedItem as MarketMod;
        if (mod == null)
        {
            AppDialog.Info("请先在列表里选择一个模组。", "下载中心");
            return;
        }

        var target = MarketTargetBox.SelectedItem as VersionVm;
        if (target == null || target.Info.IsFileMode)
        {
            AppDialog.Info("请先在左侧「安装到版本」里选中一个磁盘版本。", "下载中心");
            return;
        }

        await InstallMarketModAsync(mod, target, MarketVersionBox.SelectedItem as GameRelease, null,
            MarketEditionBox.SelectedItem as MarketEdition);
    }

    private async Task InstallMarketModAsync(MarketMod mod, VersionVm target, GameRelease? preferred,
        Action<string>? status, MarketEdition? edition = null)
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
                target.Info.FullPath, target.Info.GameVersion, Log);
        }
        catch (Exception ex)
        {
            Log("安装前检查失败：" + ex.Message);
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
                var file = await DownloadService.InstallModAsync(item, target.Info.FullPath, Log, p =>
                {
                    var overall = (i + p) / Math.Max(1, plan.Count);
                    MarketProgress.Value = overall;
                    SetStatus($"{overall:P0}　{ item.Name}");
                }, tag, useEdition);
                Log("模组安装完成：" + file);
            }

            RefreshMarketStatus();
            ApplyMarketFilter();
            if (_selected != null && _selected.Info.Folder == target.Info.Folder)
                LoadMods(_selected.Info);
            // 放在刷新列表之后，避免 SelectionChanged 把完成提示清掉
            SetStatus($"安装完成（{plan.Count} 个）");
        }
        catch (Exception ex)
        {
            SetStatus("安装失败");
            Log("模组下载/安装失败：" + ex.Message);
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

    private void SetDownloadBusy(bool busy)
    {
        _downloadBusy = busy;
        GameDownloadBtn.IsEnabled = !busy;
        MarketDownloadBtn.IsEnabled = !busy;
        RefreshMarketBtn.IsEnabled = !busy;
    }

}
