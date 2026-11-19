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
        string? tempZip = null;
        try
        {
            var file = await FilePicker.Default.PickAsync(
                new PickOptions { PickerTitle = "选择版本包（zip / dolpack）" });
            if (file == null) return;

            SetBusy(true);
            tempZip = await CopyToCacheAsync(file);

            var inspection = await Task.Run(() => PackService.Inspect(tempZip));
            if (inspection.IsModPack)
            {
                await ImportModPackAsync(file.FileName, tempZip, inspection);
                return;
            }
            if (!inspection.IsGame)
            {
                await PlatformServices.Dialogs.ErrorAsync(
                    "无法识别这个包：需要包含 Degrees of Lewdity.html 的版本包。");
                return;
            }
            if (!await PlatformServices.Dialogs.ConfirmAsync(
                    $"将「{file.FileName}」导入为新版本？", "导入版本", "开始导入"))
                return;

            var packPath = tempZip;
            var result = await Task.Run(() => PackService.Import(packPath, inspection, null,
                msg => LogService.Append("[导入] " + msg)));

            LogService.Append($"[导入] 完成：{result.NewVersionFolder}");
            await ReloadAsync();
            await PlatformServices.Dialogs.InfoAsync($"已导入版本：{result.NewVersionFolder}");
        }
        catch (Exception ex)
        {
            LogService.Error("导入版本失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导入失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
            if (tempZip != null)
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }

    /// <summary>模组包不含游戏本体：先选目标版本，再交给 PackService 走模组导入流程。</summary>
    private async Task ImportModPackAsync(string displayName, string packPath,
        PackService.PackInspection inspection)
    {
        var versions = _versions.ToList();
        if (versions.Count == 0)
        {
            await PlatformServices.Dialogs.WarnAsync(
                "还没有可接收模组的版本，请先导入或下载游戏本体。", "导入模组包");
            return;
        }

        var options = versions.Select((v, i) => $"{i + 1}. {v.DisplayName}").ToArray();
        var choice = await DisplayActionSheetAsync("这是模组包，导入到哪个版本？",
            "取消", null!, options);
        if (string.IsNullOrEmpty(choice)) return;
        var index = Array.IndexOf(options, choice);
        if (index < 0) return;
        var target = versions[index];

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"将「{displayName}」导入版本「{target.DisplayName}」？\n包内同名模组会被覆盖。",
                "导入模组包", "导入"))
            return;

        var result = await Task.Run(() => PackService.Import(packPath, inspection, target.FullPath,
            msg => LogService.Append("[导入模组] " + msg)));
        LogService.Append($"[导入模组] {displayName} → {target.DisplayName}：成功 {result.ModCount} 个");

        await ReloadAsync();

        var message = $"已导入 {result.ModCount} 个模组到「{target.DisplayName}」。";
        if (result.FailedMods.Count > 0)
        {
            message += "\n\n跳过：" + string.Join("\n",
                result.FailedMods.Take(5).Select(f => "· " + f));
            if (result.FailedMods.Count > 5)
                message += $"\n…另有 {result.FailedMods.Count - 5} 个";
        }
        await PlatformServices.Dialogs.InfoAsync(message, "导入模组包");
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
