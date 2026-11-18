using System.Collections.ObjectModel;
using System.ComponentModel;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class ModsPage : ContentPage
{
    private readonly VersionInfo _version;
    private readonly ObservableCollection<ModsRow> _rows = new();
    private bool _loading;
    private bool _importing;
    /// <summary>批量重建列表时置位，避免开关的程序化赋值触发保存。</summary>
    private bool _syncing;

    public ModsPage(VersionInfo version)
    {
        InitializeComponent();
        _version = version;
        Title = version.DisplayName;
        ModList.ItemsSource = _rows;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var mods = await Task.Run(() => ModService.Load(_version.FullPath));
            _syncing = true;
            try
            {
                _rows.Clear();
                foreach (var mod in mods) _rows.Add(new ModsRow(mod));
                RefreshMoveFlags();
            }
            finally
            {
                _syncing = false;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("读取模组列表失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("读取模组列表失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
            Refresher.IsRefreshing = false;
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e) => await ReloadAsync();

    private async void OnModToggled(object? sender, ToggledEventArgs e)
    {
        if (_syncing) return;
        if (sender is not Switch { BindingContext: ModsRow row }) return;
        if (row.Mod.Enabled == e.Value) return;

        row.Mod.Enabled = e.Value;
        await SaveAsync();
    }

    private async void OnMoveUpClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ModsRow row }) return;
        var index = _rows.IndexOf(row);
        if (index <= 0) return;

        _rows.Move(index, index - 1);
        RefreshMoveFlags();
        await SaveAsync();
    }

    private async void OnMoveDownClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ModsRow row }) return;
        var index = _rows.IndexOf(row);
        if (index < 0 || index >= _rows.Count - 1) return;

        _rows.Move(index, index + 1);
        RefreshMoveFlags();
        await SaveAsync();
    }

    /// <summary>首项禁用上移、末项禁用下移，并同步 IsFirst / IsLast。</summary>
    private void RefreshMoveFlags()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.IsFirst = i == 0;
            row.IsLast = i == _rows.Count - 1;
            row.CanMoveUp = !row.IsFirst;
            row.CanMoveDown = !row.IsLast;
        }
    }

    private async Task SaveAsync()
    {
        var mods = _rows.Select(r => r.Mod).ToList();
        try
        {
            await Task.Run(() => ModService.Save(_version.FullPath, mods));
        }
        catch (Exception ex)
        {
            LogService.Error("保存模组状态失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("保存模组状态失败：" + ex.Message);
            await ReloadAsync();
        }
    }

    private async void OnImportClicked(object? sender, EventArgs e)
    {
        if (_importing) return;

        var temps = new List<string>();
        try
        {
            var files = await FilePicker.Default.PickMultipleAsync(
                new PickOptions { PickerTitle = "选择模组包" });
            var picked = files?.OfType<FileResult>().ToList() ?? new List<FileResult>();
            if (picked.Count == 0) return;

            _importing = true;
            SetBusy(true);

            var dir = Path.Combine(FileSystem.CacheDirectory, "import-mods");
            Directory.CreateDirectory(dir);
            foreach (var file in picked)
            {
                var target = UniquePath(dir, string.IsNullOrWhiteSpace(file.FileName) ? "mod.zip" : file.FileName);
                await using (var source = await file.OpenReadAsync())
                await using (var dest = File.Create(target))
                {
                    await source.CopyToAsync(dest);
                }
                temps.Add(target);
            }

            var count = await Task.Run(() => ModService.ImportFiles(_version.FullPath, temps,
                msg => LogService.Append("[模组导入] " + msg)));

            await ReloadAsync();

            var message = $"已导入 {count} 个模组。";
            if (count < picked.Count) message += "\n有文件被跳过，详见日志。";
            await PlatformServices.Dialogs.InfoAsync(message, "导入模组");
        }
        catch (Exception ex)
        {
            LogService.Error("导入模组失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导入模组失败：" + ex.Message);
        }
        finally
        {
            _importing = false;
            SetBusy(false);
            foreach (var path in temps)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }

    private void SetBusy(bool busy)
    {
        BusyOverlay.IsVisible = busy;
        Refresher.IsEnabled = !busy;
        ImportItem.IsEnabled = !busy;
    }

    /// <summary>SAF 复制到缓存的同名文件改为加序号，避免覆盖。</summary>
    private static string UniquePath(string dir, string fileName)
    {
        var target = Path.Combine(dir, fileName);
        if (!File.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 999; i++)
        {
            target = Path.Combine(dir, $"{stem}-{i}{ext}");
            if (!File.Exists(target)) return target;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }
}

/// <summary>模组列表行：包装 ModInfo，并提供排序按钮的可用状态。</summary>
public sealed class ModsRow : INotifyPropertyChanged
{
    private bool _canMoveUp;
    private bool _canMoveDown;

    public ModsRow(ModInfo mod) => Mod = mod;

    public ModInfo Mod { get; }
    public string Title => Mod.Title;
    public string FileName => Mod.FileName;
    public bool Enabled => Mod.Enabled;
    public bool IsFirst { get; set; }
    public bool IsLast { get; set; }

    public bool CanMoveUp
    {
        get => _canMoveUp;
        set
        {
            if (_canMoveUp == value) return;
            _canMoveUp = value;
            OnPropertyChanged(nameof(CanMoveUp));
        }
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        set
        {
            if (_canMoveDown == value) return;
            _canMoveDown = value;
            OnPropertyChanged(nameof(CanMoveDown));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
