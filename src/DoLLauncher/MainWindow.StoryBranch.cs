using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoLLauncher.Controls;
using DoLLauncher.Core.Browser;
using DoLLauncher.Core.Story;
using DoLLauncher.Models;
using DoLLauncher.Services;
using Microsoft.Win32;

namespace DoLLauncher;

public partial class MainWindow
{
    private StoryBranchTree? _storyBranch;
    private StoryBranchNode? _selectedBranchNode;
    private string _storyBranchFolder = "";
    private bool _storyBranchLoading;
    private bool _storySimBusy;
    private int _storySimCancel;

    /// <summary>切换版本或点了刷新后重新读取模拟结果。</summary>
    private void StoryBranchRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadStoryBranchAsync();

    private async Task LoadStoryBranchAsync()
    {
        if (!_storyInitialized || _storyBranchLoading) return;

        var version = StoryVersionBox.SelectedItem as VersionVm;
        if (version == null || string.IsNullOrEmpty(version.Info.FullPath))
        {
            SetStoryModMissing(false);
            ClearStoryBranch("没有可用的版本目录。");
            return;
        }

        _storyBranchLoading = true;
        var folder = version.Info.FullPath;
        StoryBranchStatusText.Text = "正在读取模拟结果…";
        try
        {
            var hasMod = await Task.Run(() => HasStoryBranchMod(folder));
            SetStoryModMissing(!hasMod);
            if (!hasMod)
            {
                ClearStoryBranch("未检测到「剧情分支模拟」模组。");
                return;
            }

            var result = await Task.Run(() =>
            {
                var tree = StoryBranchReader.Load(folder, out var error);
                return (Tree: tree, Error: error);
            });

            if (_storyBranchFolder != folder) _storyBranch = null;
            _storyBranchFolder = folder;

            if (result.Tree == null)
            {
                ClearStoryBranch(result.Error ?? "没有找到模拟结果。");
                return;
            }

            _storyBranch = result.Tree;
            BuildStoryBranchTree(result.Tree);
        }
        catch (Exception ex)
        {
            ClearStoryBranch("读取模拟结果失败：" + ex.Message);
        }
        finally
        {
            _storyBranchLoading = false;
        }
    }

    private void ClearStoryBranch(string message)
    {
        _storyBranch = null;
        _selectedBranchNode = null;
        StoryBranchGraph.SetTree(null, false);
        StoryBranchExportImageBtn.IsEnabled = false;
        StoryBranchExportJsonBtn.IsEnabled = false;
        StoryBranchDetailPanel.Children.Clear();
        StoryBranchEmptyText.Text = message;
        StoryBranchEmptyText.Visibility = Visibility.Visible;
        StoryBranchStatusText.Text = message;
    }

    // ---------- 未安装模组时的安装引导 ----------

    private const string StoryBranchModRepoUrl = "https://github.com/Dingdang255/StoryBranchSim";
    private bool _storyModGuideBuilt;

    /// <summary>所选版本是否已安装并启用「剧情分支模拟」模组。</summary>
    private static bool HasStoryBranchMod(string versionDir)
    {
        try
        {
            return ModService.Load(versionDir).Any(mod =>
                mod.Enabled && ModService.NameKeys(mod)
                    .Any(key => key.Contains("storybranchsim", StringComparison.Ordinal)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>没有可用模组时，把右侧主体换成安装引导。</summary>
    private void SetStoryModMissing(bool missing)
    {
        if (StoryBranchPanel == null || StoryModMissingPanel == null) return;
        StoryBranchPanel.Visibility = missing ? Visibility.Collapsed : Visibility.Visible;
        StoryModMissingPanel.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        if (missing) BuildStoryModGuide();
    }

    private void BuildStoryModGuide()
    {
        if (_storyModGuideBuilt || StoryModActionPanel == null) return;
        _storyModGuideBuilt = true;

        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        var pale = TryFindResource("AccentPaleBrush") as Brush ?? Brushes.Transparent;

        StoryModActionPanel.Children.Add(MakeStoryModButton("前往模组下载",
            BuildIconChip("\uE896", pale, accent), StoryModDownload_Click));
        StoryModActionPanel.Children.Add(MakeStoryModButton("打开 GitHub 主页",
            BuildProviderIcon("GitHub"), StoryModGithub_Click));
    }

    /// <summary>沿用致谢名单里的域名图标按钮样式：圆角小图标 + 强调色文字。</summary>
    private FrameworkElement MakeStoryModButton(string label, FrameworkElement icon, RoutedEventHandler onClick)
    {
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;

        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 9, 0),
        };
        inner.Children.Add(icon);
        inner.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 1, 0),
        });

        var button = new Button
        {
            Style = TryFindResource("CreditLinkButton") as Style,
            Height = 28,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = label,
            Content = inner,
        };
        button.Click += onClick;
        return button;
    }

    private static FrameworkElement BuildIconChip(string glyph, Brush background, Brush foreground)
    {
        var chip = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            Background = background,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };
        chip.Child = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return chip;
    }

    private void StoryModDownload_Click(object sender, RoutedEventArgs e)
    {
        var version = StoryVersionBox.SelectedItem as VersionVm;
        NavDownload.IsChecked = true;
        DownloadNavMod.IsChecked = true;

        if (version == null) return;
        var target = _targets.FirstOrDefault(v =>
            string.Equals(v.Info.Folder, version.Info.Folder, StringComparison.OrdinalIgnoreCase));
        if (target != null) MarketTargetBox.SelectedItem = target;
    }

    private void StoryModGithub_Click(object sender, RoutedEventArgs e) => OpenExternal(StoryBranchModRepoUrl);


    private void BuildStoryBranchTree(StoryBranchTree tree)
    {
        StoryBranchDetailPanel.Children.Clear();

        if (tree.Root == null)
        {
            ClearStoryBranch("模拟结果里没有根节点。");
            return;
        }

        StoryBranchGraph.SetTree(tree, StoryBranchSuccessOnly.IsChecked == true);
        StoryBranchGraph.SelectNode(tree.Root.Id);
        StoryBranchEmptyText.Visibility = Visibility.Collapsed;
        StoryBranchExportImageBtn.IsEnabled = true;
        StoryBranchExportJsonBtn.IsEnabled = true;
        ShowStoryBranchDetail(tree.Root);

        var (success, failed, merged, skipped) = tree.CountByStatus();
        var parts = new List<string>
        {
            "起点：" + (tree.RootPassage.Length > 0 ? tree.RootPassage : "—"),
        };
        if (tree.SaveSlot.Length > 0) parts.Add("存档槽：" + tree.SaveSlot);
        parts.Add($"{success} 成功 / {failed} 失败 / {merged} 合并" + (skipped > 0 ? $" / {skipped} 跳过" : ""));
        parts.Add("全局种子：" + (tree.GlobalSeed.Raw.Length > 0 ? tree.GlobalSeed.Raw : "—"));
        if (tree.Truncated) parts.Add("已按预算截断");
        if (!tree.StateIntact) parts.Add("警告：模拟后玩家状态可能已变化");
        if (tree.VersionMismatch) parts.Add(tree.VersionWarning);
        parts.Add("写入时间：" + tree.ModifiedAt.ToString("yyyy-MM-dd HH:mm"));
        StoryBranchStatusText.Text = string.Join(" · ", parts);
    }

    private void StoryBranchFilter_Changed(object sender, RoutedEventArgs e)
    {
        StoryBranchGraph.SetSuccessOnly(StoryBranchSuccessOnly.IsChecked == true);
    }

    /// <summary>由启动器发起一次完整模拟；模拟页开着时同一按钮变成「停止模拟」。</summary>
    private async void StoryBranchRun_Click(object sender, RoutedEventArgs e)
    {
        var versionVm = StoryVersionBox.SelectedItem as VersionVm;
        if (versionVm == null)
        {
            AppDialog.Warn("请先在剧情页选择要模拟的版本。");
            return;
        }

        if (_storySimBusy ||
            (SimulationTracker.HasPage(versionVm.Info.Folder) && IsSimPageOpen(versionVm.Info.Folder)))
        {
            StopSimulation(versionVm);
            return;
        }
        if (!TryReadSimOptions(out var depth, out var budget, out var seed)) return;
        if (!ConfirmHeavy("模拟", depth, budget)) return;

        var slotVm = StorySlotBox.SelectedItem as StorySlotVm;
        if (slotVm == null)
        {
            AppDialog.Warn("请先在剧情页选择一个存档槽。");
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            slot = slotVm.Slot.SlotId,
            slotLabel = slotVm.DisplayName,
            depth,
            budget,
            seed
        });
        await ExecuteSimAsync(versionVm, payload, false, "完整模拟");
    }

    /// <summary>该版本是否还有打开着的页面（模拟或普通游戏都算）。</summary>
    private static bool IsSimPageOpen(string folder) =>
        ServerManager.GetPageState(folder)?.HasPages == true;

    /// <summary>停止模拟：取消轮询并关闭这个版本的页面。</summary>
    private void StopSimulation(VersionVm versionVm)
    {
        _storySimCancel++;
        try { BrowserService.Close(versionVm.Info.FullPath); } catch { }
        SimulationTracker.Clear(versionVm.Info.Folder);
        SimulationTracker.UnmarkPage(versionVm.Info.Folder);
        StoryBranchRunStatus.Text = "已停止模拟并关闭页面。";
        _storySimBusy = false;
        StoryBranchRunBtn.IsEnabled = true;
        RefreshVersionStatuses();
        UpdateStorySimButton();
    }

    /// <summary>根据是否有模拟页开着，在「开始模拟 / 停止模拟」之间切换。</summary>
    private void UpdateStorySimButton()
    {
        if (StoryBranchRunBtn == null) return;
        var vm = StoryVersionBox.SelectedItem as VersionVm;
        var pageOpen = vm != null && !vm.Info.IsFileMode &&
                       SimulationTracker.HasPage(vm.Info.Folder) && IsSimPageOpen(vm.Info.Folder);
        var stop = _storySimBusy || pageOpen;
        StoryBranchRunBtn.Content = stop ? "停止模拟" : "开始模拟";
        StoryBranchRunBtn.Tag = stop ? "\uE71A" : "\uE768";
        // 没有存档槽时不允许开始模拟；已开着模拟页时仍可点「停止模拟」
        StoryBranchRunBtn.IsEnabled = stop || StorySlotBox?.SelectedItem != null;

        if (StoryBranchContinueBtn != null)
        {
            var node = _selectedBranchNode;
            var can = !_storySimBusy && pageOpen && node != null && node.Children.Count == 0 &&
                      (node.Status == StoryBranchStatus.Success || node.Status == StoryBranchStatus.Root);
            StoryBranchContinueBtn.IsEnabled = can;
        }
    }

    /// <summary>从当前选中的分支节点继续展开（复用还开着的模拟页）。</summary>
    private async void StoryBranchContinue_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBranchNode == null)
        {
            AppDialog.Warn("请先在左侧分支图里选择一个节点。");
            return;
        }
        if (_selectedBranchNode.Children.Count > 0 ||
            (_selectedBranchNode.Status != StoryBranchStatus.Success &&
             _selectedBranchNode.Status != StoryBranchStatus.Root))
        {
            AppDialog.Warn("只能从还没有分支的末端节点继续，请选分支图最外层的节点。");
            return;
        }
        var versionVm = StoryVersionBox.SelectedItem as VersionVm;
        if (versionVm == null)
        {
            AppDialog.Warn("请先在剧情页选择要模拟的版本。");
            return;
        }
        if (!TryReadSimOptions(out var depth, out var budget, out var seed)) return;
        if (!ConfirmHeavy("继续模拟", depth, budget)) return;

        var payload = JsonSerializer.Serialize(new
        {
            fromNode = _selectedBranchNode.Id,
            depth,
            budget,
            seed
        });
        await ExecuteSimAsync(versionVm, payload, true, "从节点 " + _selectedBranchNode.Id + " 继续模拟");
    }

    /// <summary>把整棵分支图（含节点与汇入箭头）渲染成 PNG 保存到本地。</summary>
    private void StoryBranchExportImage_Click(object sender, RoutedEventArgs e)
    {
        if (_storyBranch?.Root == null)
        {
            AppDialog.Warn("还没有可导出的模拟结果。");
            return;
        }

        StoryBranchGraph.UpdateLayout();
        var width = StoryBranchGraph.ActualWidth;
        var height = StoryBranchGraph.ActualHeight;
        if (width < 1 || height < 1)
        {
            AppDialog.Warn("分支图还没有完成布局，请稍后再试。");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出分支图",
            Filter = "PNG 图片|*.png",
            FileName = "branch-graph-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // 用离屏控件按完整内容尺寸重新排版，避免截取当前滚动视口或缩放导致模糊
            var view = new StoryBranchGraphView
            {
                TextBrush = (Brush)FindResource("TextBrush"),
                MutedBrush = (Brush)FindResource("MutedBrush"),
                AccentBrush = (Brush)FindResource("AccentBrush"),
            };
            view.SetTree(_storyBranch, StoryBranchSuccessOnly.IsChecked == true);
            if (_selectedBranchNode != null) view.SelectNode(_selectedBranchNode.Id);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var content = view.DesiredSize;
            if (content.Width < 1 || content.Height < 1)
            {
                AppDialog.Warn("分支图还没有完成布局，请稍后再试。");
                return;
            }
            view.Arrange(new Rect(0, 0, content.Width, content.Height));
            view.UpdateLayout();

            // 3 倍渲染；图片过大时逐级降低，避免超出位图上限
            double scale = 3;
            while (scale > 1 && (content.Width * scale > 16000 || content.Height * scale > 16000)) scale -= 1;

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(content.Width * scale), (int)Math.Ceiling(content.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);

            var background = TryFindResource("CardBrush") as Brush ?? Brushes.Black;
            var back = new DrawingVisual();
            using (var dc = back.RenderOpen())
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, content.Width, content.Height));
            }
            bitmap.Render(back);
            bitmap.Render(view);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(dlg.FileName);
            encoder.Save(stream);
            StoryBranchRunStatus.Text = "已导出分支图：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            AppDialog.Error("导出图片失败：" + ex.Message);
        }
    }

    /// <summary>导出完整节点详情 JSON（当前版本的模拟结果文件）。</summary>
    private void StoryBranchExportJson_Click(object sender, RoutedEventArgs e)
    {
        var vm = StoryVersionBox.SelectedItem as VersionVm;
        if (vm == null)
        {
            AppDialog.Warn("请先在剧情页选择版本。");
            return;
        }
        var source = StoryBranchReader.PathOf(vm.Info.FullPath);
        if (!File.Exists(source))
        {
            AppDialog.Warn("这个版本还没有模拟结果文件。");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出模拟结果",
            Filter = "JSON 文件|*.json",
            FileName = "branch-result-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var text = File.ReadAllText(source);
            try
            {
                using var doc = JsonDocument.Parse(text);
                text = JsonSerializer.Serialize(doc.RootElement,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                // 原文件不是合法 JSON 时按原样导出
            }
            File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
            StoryBranchRunStatus.Text = "已导出模拟结果：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            AppDialog.Error("导出结果失败：" + ex.Message);
        }
    }

    private bool TryReadSimOptions(out int depth, out int budget, out string seed)
    {
        depth = ParsePositive(StoryBranchDepthBox.Text, 3);
        budget = ParsePositive(StoryBranchBudgetBox.Text, 150);
        seed = StoryBranchSeedBox.Text.Trim();
        if (seed.Length > 0 && !seed.All(char.IsDigit))
        {
            AppDialog.Warn("种子必须是纯数字，留空则随机生成。");
            return false;
        }
        return true;
    }

    /// <summary>解析正整数，非法或小于 1 时回退默认值（不设上限）。</summary>
    private static int ParsePositive(string text, int fallback) =>
        int.TryParse(text, out var value) && value >= 1 ? value : fallback;

    /// <summary>深度/预算超过经验值时提醒模拟可能很慢，仍允许继续。</summary>
    private static bool ConfirmHeavy(string action, int depth, int budget)
    {
        if (depth <= 6 && budget <= 400) return true;
        return AppDialog.Confirm(
            $"这次{action}的参数比较大：\n深度 {depth} 层，节点预算 {budget} 个。\n\n" +
            "节点可能很多或层次很深，模拟会明显变慢，甚至长时间没有响应。\n仍然" + action + "吗？",
            "模拟量较大", "仍然" + action);
    }

    /// <summary>
    /// 下发模拟命令并轮询状态。游戏页模拟完不关闭，下一次模拟（含从节点继续）直接复用该页面。
    /// </summary>
    private async Task ExecuteSimAsync(VersionVm versionVm, string payload, bool requireOpenPage, string label)
    {
        var version = versionVm.Info;
        if (_storySimBusy) return;
        if (!File.Exists(version.HtmlPath))
        {
            AppDialog.Warn("这个版本目录里找不到游戏本体 HTML，无法启动。");
            return;
        }

        _storySimBusy = true;
        var token = ++_storySimCancel;
        StoryBranchRunBtn.IsEnabled = true;
        StoryBranchRunStatus.Text = label + "：正在准备…";
        UpdateStorySimButton();
        SimulationTracker.Set(version.Folder);
        RefreshVersionStatuses();
        try
        {
            ModService.Apply(version.FullPath);
            var server = ServerManager.EnsureStarted(version);

            var hasPage = ServerManager.GetPageState(version.Folder)?.HasPages == true;
            if (requireOpenPage && !hasPage)
            {
                StoryBranchRunStatus.Text = "继续模拟失败：之前那个游戏页面已经关闭，请先重新完整模拟一次。";
                return;
            }
            if (!hasPage)
            {
                BrowserService.OpenGame(version, server.GameUrl);
                Log($"已启动 {version.DisplayName}：{server.GameUrl}");
            }
            else
            {
                StoryBranchRunStatus.Text = label + "：复用已打开的游戏页面…";
            }
            SimulationTracker.MarkPage(version.Folder);
            UpdateStorySimButton();

            var baseUrl = $"http://127.0.0.1:{server.Port}";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var post = await http.PostAsync(baseUrl + "/__launcher/api/story-sim/run",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            if (!post.IsSuccessStatusCode)
            {
                StoryBranchRunStatus.Text = "下发命令失败：HTTP " + (int)post.StatusCode;
                return;
            }

            StoryBranchRunStatus.Text = "等待游戏页面加载并模拟…";
            for (var i = 0; i < 450; i++)
            {
                await Task.Delay(2000);
                if (token != _storySimCancel)
                {
                    StoryBranchRunStatus.Text = "已停止模拟。";
                    return;
                }
                string text;
                try
                {
                    text = await http.GetStringAsync(baseUrl + "/__launcher/api/story-sim/status");
                }
                catch
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("status", out var st)) continue;
                var state = st.TryGetProperty("state", out var se) ? se.GetString() : "";
                var message = st.TryGetProperty("message", out var me) ? me.GetString() : "";
                if (state == "queued" || state == "running")
                {
                    StoryBranchRunStatus.Text = string.IsNullOrWhiteSpace(message) ? "模拟中…" : message;
                    continue;
                }
                if (state == "done")
                {
                    var root = st.TryGetProperty("root", out var re) ? re.GetString() : "";
                    var nodes = st.TryGetProperty("nodes", out var ne) ? ne.GetInt32() : 0;
                    var summary = label + "完成：" + (root ?? "") + " · " + nodes + " 节点";
                    if (st.TryGetProperty("counts", out var counts))
                    {
                        summary += $"（{counts.GetProperty("success").GetInt32()} 成功 / " +
                                   $"{counts.GetProperty("failed").GetInt32()} 失败 / " +
                                   $"{counts.GetProperty("merged").GetInt32()} 合并 / " +
                                   $"{counts.GetProperty("skipped").GetInt32()} 跳过）";
                    }
                    StoryBranchRunStatus.Text = summary;
                    await LoadStoryBranchAsync();
                    return;
                }
                if (state == "error")
                {
                    StoryBranchRunStatus.Text = "模拟失败：" + (message ?? "未知原因");
                    return;
                }
            }
            StoryBranchRunStatus.Text = "等待超时，请确认游戏页面已经打开。";
        }
        catch (Exception ex)
        {
            StoryBranchRunStatus.Text = "启动失败：" + ex.Message;
            AppDialog.Error("启动模拟失败：" + ex.Message);
        }
        finally
        {
            _storySimBusy = false;
            StoryBranchRunBtn.IsEnabled = true;
            SimulationTracker.Clear(version.Folder);
            RefreshVersionStatuses();
            UpdateStorySimButton();
        }
    }

    /// <summary>让版本列表刷新「模拟中 / 运行中」等状态文字。</summary>
    private void RefreshVersionStatuses()
    {
        foreach (var vm in _versions) vm.Refresh();
        foreach (var vm in _storyVersions) vm.Refresh();
    }

    private static string StatusText(StoryBranchStatus status) => status switch
    {
        StoryBranchStatus.Root => "当前",
        StoryBranchStatus.Failed => "失败",
        StoryBranchStatus.Skipped => "跳过",
        StoryBranchStatus.Merged => "合并",
        _ => "成功",
    };

    private void ShowStoryBranchDetail(StoryBranchNode node)
    {
        _selectedBranchNode = node;
        var panel = StoryBranchDetailPanel;
        panel.Children.Clear();

        AddBranchLine(panel, "节点", node.Id + (node.Depth > 0 ? $"（第 {node.Depth} 层）" : "（起点）"));
        AddBranchLine(panel, "选项", node.DisplayTitle);
        AddBranchLine(panel, "段落", $"{node.FromPassage} → {node.ResultPassage}");
        if (node.ToPassage.Length > 0 && node.ToPassage != node.ResultPassage)
        {
            AddBranchLine(panel, "跳转链", $"{node.ToPassage} → {node.ResultPassage}");
        }

        var status = StatusText(node.Status);
        if (node.Error.Length > 0) status += "：" + node.Error;
        AddBranchLine(panel, "状态", status);

        var seed = node.Seed.Raw.Length > 0 ? node.Seed.Raw : "—";
        seed += node.Seed.IsNodeOverride ? "（节点覆盖）" : "（全局）";
        seed += node.RandomCalls > 0 ? $" · 随机 {node.RandomCalls} 次" : " · 未使用随机";
        AddBranchLine(panel, "种子", seed);

        AddBranchLine(panel, "耗时", node.DurationMs + " ms");
        if (node.CutByDepth) AddBranchLine(panel, "截断", "已达到深度上限，未继续展开");
        if (node.MergedInto.Length > 0) AddBranchLine(panel, "合并", "该时间线已收束至节点 " + node.MergedInto + "（同一段落）");

        if (StoryBranchContinueBtn != null) StoryBranchContinueBtn.Content = "继续模拟";
        UpdateStorySimButton();

        if (node.Warnings.Count > 0)
        {
            AddBranchSection(panel, "副作用与提示");
            foreach (var warning in node.Warnings)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "· " + warning,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 1),
                    Foreground = (Brush)FindResource("MutedBrush"),
                });
            }
        }

        var diffTitle = $"变量差异（{node.Diff.Count} 项";
        if (node.DiffOmitted > 0) diffTitle += $"，另有 {node.DiffOmitted} 项未列出";
        diffTitle += "）";
        AddBranchSection(panel, diffTitle);

        if (node.Diff.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "无变量变化。",
                FontSize = 11,
                Foreground = (Brush)FindResource("MutedBrush"),
            });
            return;
        }

        foreach (var change in node.Diff)
        {
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

            var head = new DockPanel();
            var kind = new TextBlock
            {
                Text = change.Kind switch
                {
                    "added" => "新增",
                    "removed" => "删除",
                    _ => "变更",
                },
                FontSize = 10,
                Foreground = change.Kind switch
                {
                    "added" => new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                    "removed" => new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                    _ => new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                },
            };
            DockPanel.SetDock(kind, Dock.Right);
            head.Children.Add(kind);
            head.Children.Add(new TextBlock
            {
                Text = change.Key,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("AccentBrush"),
            });
            row.Children.Add(head);

            row.Children.Add(new TextBlock
            {
                Text = (change.Before ?? "∅") + " → " + (change.After ?? "∅"),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBrush"),
            });

            panel.Children.Add(row);
        }
    }

    private void AddBranchLine(StackPanel panel, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("MutedBrush"),
        };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextBrush"),
        };
        Grid.SetColumn(valueBlock, 1);

        row.Children.Add(labelBlock);
        row.Children.Add(valueBlock);
        panel.Children.Add(row);
    }

    private void AddBranchSection(StackPanel panel, string title)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4),
            Foreground = (Brush)FindResource("AccentBrush"),
        });
    }
}
