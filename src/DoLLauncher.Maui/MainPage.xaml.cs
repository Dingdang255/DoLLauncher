using System.Collections.ObjectModel;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class MainPage : ContentPage
{
    private readonly ObservableCollection<VersionInfo> _versions = new();
    private bool _loading;
    private bool _suppressNextSelection;

    public MainPage()
    {
        InitializeComponent();
        VersionList.ItemsSource = _versions;
        RootLabel.Text = Paths.Root;
    }

    private async Task ReloadAsync()
    {
        // 自动刷新与下拉刷新可能重叠，直接结束下拉动画避免一直转圈。
        if (_loading)
        {
            Refresher.IsRefreshing = false;
            return;
        }
        _loading = true;
        try
        {
            var list = await Task.Run(VersionService.Scan);
            _versions.Clear();
            foreach (var item in list.Where(v => v.Mode == "http")) _versions.Add(item);
        }
        catch (Exception ex)
        {
            LogService.Error("扫描版本失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("扫描版本失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
            Refresher.IsRefreshing = false;
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e) => await ReloadAsync();

    private async void OnVersionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not VersionInfo version) return;
        VersionList.SelectedItem = null;

        // 「模组」等快捷按钮点击时可能顺带选中卡片，这里吞掉那一次选中，避免误进详情页。
        if (_suppressNextSelection)
        {
            _suppressNextSelection = false;
            return;
        }

        await Navigation.PushAsync(new VersionDetailPage(version));
    }

    private async void OnModsClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: VersionInfo version }) return;
        _suppressNextSelection = true;
        VersionList.SelectedItem = null;
        await Navigation.PushAsync(new ModsPage(version));
    }

    private async void OnBackupClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: VersionInfo version }) return;
        _suppressNextSelection = true;
        VersionList.SelectedItem = null;
        await Navigation.PushAsync(new BackupPage(version));
    }

    private async void OnDiagnosticsClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: VersionInfo version }) return;
        _suppressNextSelection = true;
        VersionList.SelectedItem = null;
        await Navigation.PushAsync(new DiagnosticsPage(version));
    }

    private async void OnStoryClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: VersionInfo version }) return;
        _suppressNextSelection = true;
        VersionList.SelectedItem = null;
        await Navigation.PushAsync(new StoryPage(version));
    }

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage());
    }

    private async void OnDownloadClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new DownloadPage());
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 从模组页返回时恢复正常的选中行为。
        _suppressNextSelection = false;
        // 删除 / 导入版本后回到列表，重新扫描一次。
        _ = ReloadAsync();
    }

    private async void OnImportClicked(object? sender, EventArgs e)
    {
        List<FileResult> picked;
        try
        {
            var files = await FilePicker.Default.PickMultipleAsync(
                new PickOptions { PickerTitle = "选择版本包 / 模组包（可多选）" });
            picked = files?.OfType<FileResult>().ToList() ?? new List<FileResult>();
        }
        catch (Exception ex)
        {
            LogService.Error("选择导入文件失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开文件选择器：" + ex.Message);
            return;
        }
        if (picked.Count == 0) return;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"将处理 {picked.Count} 个包：\n版本包会新建版本，模组包会让你选择要导入的版本。\n\n继续吗？",
                "导入版本 / 模组包", "开始导入"))
            return;

        SetBusy(true);
        var imported = 0;
        var skipped = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var file in picked)
            {
                var name = string.IsNullOrWhiteSpace(file.FileName) ? "(未命名)" : file.FileName;
                string? tempZip = null;
                try
                {
                    tempZip = await CopyToCacheAsync(file);
                    var inspection = await Task.Run(() => PackService.Inspect(tempZip));

                    if (inspection.IsUnknown)
                    {
                        failed.Add($"{name}：不是可识别的版本包或模组包");
                        continue;
                    }

                    if (inspection.IsModPack)
                    {
                        var target = await PickTargetVersionAsync(name);
                        if (target == null)
                        {
                            skipped.Add($"{name}：未选择目标版本");
                            continue;
                        }

                        // 与单文件导入一致：交给 PackService 处理包内顺序与启停状态。
                        var packPath = tempZip;
                        var result = await Task.Run(() => PackService.Import(packPath, inspection, target.FullPath,
                            msg => LogService.Append("[导入模组] " + msg)));
                        LogService.Append($"[导入模组] {name} → {target.DisplayName}：成功 {result.ModCount} 个");
                        imported++;
                        if (result.FailedMods.Count > 0)
                            skipped.Add($"{name}：{result.FailedMods.Count} 个模组未通过校验");
                        continue;
                    }

                    // 版本包：逐个导入为新版本。
                    var gamePack = tempZip;
                    var info = await Task.Run(() => PackService.Import(gamePack, inspection, null,
                        msg => LogService.Append("[导入] " + msg)));
                    LogService.Append($"[导入] 完成：{info.NewVersionFolder}");
                    imported++;
                }
                catch (Exception ex)
                {
                    LogService.Error($"导入 {name} 失败", ex);
                    failed.Add($"{name}：{ex.Message}");
                }
                finally
                {
                    if (tempZip != null)
                    {
                        try { File.Delete(tempZip); } catch { }
                    }
                }
            }
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadAsync();
        await PlatformServices.Dialogs.InfoAsync(BuildImportSummary(imported, skipped, failed), "导入结果");
    }

    /// <summary>让用户选择模组包要导入到哪个版本；返回 null 表示放弃。</summary>
    private async Task<VersionInfo?> PickTargetVersionAsync(string displayName)
    {
        var versions = _versions.ToList();
        if (versions.Count == 0)
        {
            await PlatformServices.Dialogs.WarnAsync(
                "还没有可接收模组的版本，请先导入或下载游戏本体。", "导入模组包");
            return null;
        }

        var options = versions.Select((v, i) => $"{i + 1}. {v.DisplayName}").ToArray();
        var choice = await DisplayActionSheetAsync($"「{displayName}」是模组包，导入到哪个版本？",
            "取消", null!, options);
        if (string.IsNullOrEmpty(choice)) return null;
        var index = Array.IndexOf(options, choice);
        return index < 0 ? null : versions[index];
    }

    /// <summary>多文件导入的统一汇总：成功 / 跳过（含原因）/ 失败。</summary>
    private static string BuildImportSummary(int imported, List<string> skipped, List<string> failed)
    {
        var text = $"成功 {imported} 个，跳过 {skipped.Count} 个，失败 {failed.Count} 个。";
        if (skipped.Count > 0) text += "\n\n跳过：\n" + FormatResultList(skipped);
        if (failed.Count > 0) text += "\n\n失败：\n" + FormatResultList(failed);
        return text;
    }

    private static string FormatResultList(List<string> items)
    {
        const int max = 6;
        var text = string.Join("\n", items.Take(max).Select(s => "· " + s));
        return items.Count > max ? text + $"\n…另有 {items.Count - max} 个" : text;
    }

    private void SetBusy(bool busy)
    {
        BusyOverlay.IsVisible = busy;
        Refresher.IsEnabled = !busy;
    }

    /// <summary>SAF 返回的是 content:// URI，PackService 需要真实路径，先复制到应用缓存。</summary>
    private static async Task<string> CopyToCacheAsync(FileResult file)
    {
        var dir = Path.Combine(FileSystem.CacheDirectory, "import");
        Directory.CreateDirectory(dir);
        var name = string.IsNullOrWhiteSpace(file.FileName) ? "pack.zip" : file.FileName;
        var target = Path.Combine(dir, name);
        if (File.Exists(target)) File.Delete(target);

        await using var source = await file.OpenReadAsync();
        await using var dest = File.Create(target);
        await source.CopyToAsync(dest);
        return target;
    }
}
