using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using DoLLauncher.Core.Data;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.ViewModels;

/// <summary>诊断页视图模型：兼容性检查、诊断包、日志分析与日志设置。</summary>
public sealed class DiagnosticViewModel : INotifyPropertyChanged
{
    private readonly DispatcherTimer _liveTimer;

    private VersionItemVm? _selectedTarget;
    private OptionItem _selectedMode = null!;
    private DiagLogFileVm? _selectedLogFile;
    private string _output = "点「兼容性检查」查看依赖与版本结果；手动诊断选择日志文件后点「分析」，实时诊断会自动统计当前游戏实例。";
    private string _liveHint = string.Empty;
    private string _logStatusText = "最近一次日志：本次运行还没有收到游戏页面上报（启动游戏后自动采集）。";
    private bool _busy;
    private bool _active;
    private int _analyzeToken;

    private int _keep = 20;
    private OptionItem _selectedMax = null!;
    private OptionItem _selectedOverflow = null!;
    private OptionItem _selectedVerbosity = null!;
    private double _queueKb = 4;
    private double _flushMs = 1000;

    private string _logText = string.Empty;

    public DiagnosticViewModel()
    {
        ModeOptions = new List<OptionItem>
        {
            new("manual", "手动诊断"),
            new("live", "实时诊断"),
        };
        KeepOptions = new List<int> { 5, 10, 20, 50 };
        MaxOptions = new List<OptionItem>
        {
            new("0", "无限制"),
            new("10", "10 MB"),
            new("30", "30 MB"),
            new("100", "100 MB"),
        };
        OverflowOptions = new List<OptionItem>
        {
            new("newfile", "写新文件（-2、-3…）"),
            new("stop", "停止写入"),
        };
        VerbosityOptions = new List<OptionItem>
        {
            new("standard", "标准"),
            new("detailed", "详细（含堆栈）"),
        };

        var general = AppConfig.Load().General;
        _selectedMode = Pick(ModeOptions, "manual");
        _keep = general.GameLogKeep is 5 or 10 or 20 or 50 ? general.GameLogKeep : 20;
        _selectedMax = Pick(MaxOptions, general.GameLogMaxMb.ToString());
        _selectedOverflow = Pick(OverflowOptions, general.GameLogOverflow);
        _selectedVerbosity = Pick(VerbosityOptions, general.LogVerbosity);
        _queueKb = Math.Clamp(general.LogQueueKb, 1, 64);
        _flushMs = Math.Clamp(general.LogFlushMs, 200, 5000);

        CompatCheckCommand = new RelayCommand(_ => RunCompatCheck(), _ => SelectedTarget != null);
        ExportCommand = new RelayCommand(_ => _ = ExportAsync(), _ => SelectedTarget != null && !_busy);
        OpenLauncherLogCommand = new RelayCommand(_ => _ = OpenLauncherLogAsync());
        OpenGameLogCommand = new RelayCommand(_ => _ = OpenGameLogAsync(), _ => SelectedTarget != null);
        AnalyzeCommand = new RelayCommand(_ => _ = AnalyzeAsync(), _ => SelectedTarget != null);
        ClearCachesCommand = new RelayCommand(_ => _ = ClearCachesAsync(), _ => SelectedTarget != null && !_busy);
        RefreshCommand = new RelayCommand(_ => RefreshAll(), _ => SelectedTarget != null);

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveTimer.Tick += (_, _) => RefreshLiveDiagnostics();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<VersionItemVm> Targets { get; } = new();

    public ObservableCollection<DiagLogFileVm> LogFiles { get; } = new();

    public List<OptionItem> ModeOptions { get; }

    public List<int> KeepOptions { get; }

    public List<OptionItem> MaxOptions { get; }

    public List<OptionItem> OverflowOptions { get; }

    public List<OptionItem> VerbosityOptions { get; }

    public ICommand CompatCheckCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand OpenLauncherLogCommand { get; }

    public ICommand OpenGameLogCommand { get; }

    public ICommand AnalyzeCommand { get; }

    public ICommand ClearCachesCommand { get; }

    public ICommand RefreshCommand { get; }

    public OptionItem SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!Set(ref _selectedMode, value)) return;
            OnPropertyChanged(nameof(IsManualMode));
            OnPropertyChanged(nameof(IsLiveMode));
            if (IsLiveMode)
            {
                if (_active) _liveTimer.Start();
                RefreshLiveDiagnostics();
            }
            else
            {
                _liveTimer.Stop();
                LiveHint = string.Empty;
                RefreshLogFiles();
            }
        }
    }

    public bool IsManualMode => _selectedMode.Key != "live";

    public bool IsLiveMode => !IsManualMode;

    public VersionItemVm? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!Set(ref _selectedTarget, value)) return;
            RaiseCommands();
            RefreshAll();
        }
    }

    public DiagLogFileVm? SelectedLogFile
    {
        get => _selectedLogFile;
        set => Set(ref _selectedLogFile, value);
    }

    public string Output
    {
        get => _output;
        private set => Set(ref _output, value);
    }

    public string LiveHint
    {
        get => _liveHint;
        private set => Set(ref _liveHint, value);
    }

    public string LogStatusText
    {
        get => _logStatusText;
        private set => Set(ref _logStatusText, value);
    }

    public int Keep
    {
        get => _keep;
        set
        {
            if (!Set(ref _keep, value)) return;
            SaveGeneral(g => g.GameLogKeep = value, "设置：游戏日志保留份数 = " + value);
        }
    }

    public OptionItem SelectedMax
    {
        get => _selectedMax;
        set
        {
            if (!Set(ref _selectedMax, value)) return;
            var mb = int.TryParse(value.Key, out var parsed) ? parsed : 0;
            SaveGeneral(g => g.GameLogMaxMb = mb, "设置：单份游戏日志上限 = " + value.Label);
        }
    }

    public OptionItem SelectedOverflow
    {
        get => _selectedOverflow;
        set
        {
            if (!Set(ref _selectedOverflow, value)) return;
            SaveGeneral(g => g.GameLogOverflow = value.Key, "设置：游戏日志超限行为 = " + value.Label);
        }
    }

    public OptionItem SelectedVerbosity
    {
        get => _selectedVerbosity;
        set
        {
            if (!Set(ref _selectedVerbosity, value)) return;
            SaveGeneral(g => g.LogVerbosity = value.Key, "设置：日志详细度 = " + value.Label);
            try { LogService.Configure(value.Key); } catch { }
        }
    }

    public double QueueKb
    {
        get => _queueKb;
        set
        {
            if (!Set(ref _queueKb, value)) return;
            OnPropertyChanged(nameof(QueueKbText));
        }
    }

    public string QueueKbText => (int)Math.Round(_queueKb) + " KB";

    public double FlushMs
    {
        get => _flushMs;
        set
        {
            if (!Set(ref _flushMs, value)) return;
            OnPropertyChanged(nameof(FlushMsText));
        }
    }

    public string FlushMsText => (int)Math.Round(_flushMs) + " 毫秒";

    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    public void SaveLogUpload()
    {
        var queue = (int)Math.Round(QueueKb);
        var flush = (int)Math.Round(FlushMs);
        SaveGeneral(g =>
        {
            g.LogQueueKb = queue;
            g.LogFlushMs = flush;
        }, $"设置：日志上报队列 {queue} KB / 合并间隔 {flush} 毫秒");
    }

    public void SyncTargets(IEnumerable<VersionItemVm> versions, string? preferredFolder)
    {
        var disk = versions.Where(v => !v.Info.IsFileMode).ToList();
        var current = preferredFolder ?? SelectedTarget?.Info.Folder;
        var same = disk.Count == Targets.Count &&
                   disk.Select((v, i) => ReferenceEquals(v, Targets[i])).All(x => x);
        if (!same)
        {
            Targets.Clear();
            foreach (var vm in disk) Targets.Add(vm);
        }
        SelectedTarget = Targets.FirstOrDefault(v => v.Info.Folder == current) ?? Targets.FirstOrDefault();
    }

    public void EnsureLoaded() => RefreshAll();

    public void Activate()
    {
        _active = true;
        if (IsLiveMode) _liveTimer.Start();
    }

    public void Deactivate()
    {
        _active = false;
        _liveTimer.Stop();
    }

    private void RefreshAll()
    {
        RefreshGameLogStatus();
        if (IsLiveMode) RefreshLiveDiagnostics();
        else RefreshLogFiles();
    }

    private void RefreshLogFiles()
    {
        var version = SelectedTarget?.Info;
        var previous = SelectedLogFile?.Path;
        var list = new List<DiagLogFileVm>();
        if (version != null)
        {
            try
            {
                var dir = GameLogService.Dir(version.FullPath);
                if (Directory.Exists(dir))
                {
                    list = new DirectoryInfo(dir).GetFiles()
                        .OrderByDescending(f => f.LastWriteTime)
                        .Select(f => new DiagLogFileVm(f.Name, f.FullName,
                            $"{f.LastWriteTime:yyyy-MM-dd HH:mm} · {f.Length / 1024.0:0.#} KB"))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                LogService.Error("读取日志目录失败", ex);
            }
        }

        if (LogFiles.Count == list.Count &&
            LogFiles.Select(f => f.Path).SequenceEqual(list.Select(f => f.Path), StringComparer.OrdinalIgnoreCase))
            return;

        LogFiles.Clear();
        foreach (var item in list) LogFiles.Add(item);
        SelectedLogFile = LogFiles.FirstOrDefault(f => f.Path == previous) ?? LogFiles.FirstOrDefault();
    }

    private void RefreshGameLogStatus()
    {
        var version = SelectedTarget?.Info;
        LogStatusText = version == null
            ? "最近一次日志：请选择一个版本。"
            : GameLogService.StatusSummary(version.FullPath);
    }

    private void RunCompatCheck()
    {
        var version = SelectedTarget?.Info;
        if (version == null) return;
        try
        {
            Output = string.Join(Environment.NewLine, DiagnosticService.Compatibility(version));
            Log("兼容性检查完成。");
        }
        catch (Exception ex)
        {
            Output = "检查失败：" + ex.Message;
            LogError("兼容性检查失败", ex);
        }
    }

    private async Task ExportAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null || _busy) return;

        var target = PlatformServices.FilePicker.PickSaveFile(
            "保存诊断包", "Zip 压缩包 (*.zip)|*.zip",
            $"dol-diagnostic-{version.Folder}-{DateTime.Now:yyyyMMdd-HHmm}.zip", ".zip");
        if (target == null) return;

        Busy = true;
        try
        {
            var path = await Task.Run(() => DiagnosticService.ExportPackage(version, target, Log));
            Log("诊断包已导出：" + path);
            await PlatformServices.Dialogs.InfoAsync("诊断包已导出：\n" + path, "导出诊断包");
        }
        catch (Exception ex)
        {
            LogError("导出诊断包失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导出失败：" + ex.Message, "导出诊断包");
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenLauncherLogAsync()
    {
        try
        {
            if (!File.Exists(LogService.FilePath))
            {
                await PlatformServices.Dialogs.InfoAsync("还没有启动器日志文件。", "启动器日志");
                return;
            }
            PlatformServices.Links.OpenPath(LogService.FilePath);
        }
        catch (Exception ex)
        {
            await PlatformServices.Dialogs.ErrorAsync("打开日志失败：" + ex.Message, "启动器日志");
        }
    }

    private async Task OpenGameLogAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null) return;
        try
        {
            var log = DiagnosticService.FindLatestLog(version.FullPath);
            if (log == null)
            {
                await PlatformServices.Dialogs.InfoAsync(
                    "还没有游戏日志。\n启动一次游戏后，日志会写到版本目录的 logs\\game-日期.log。", "游戏日志");
                return;
            }
            PlatformServices.Links.OpenPath(log);
        }
        catch (Exception ex)
        {
            await PlatformServices.Dialogs.ErrorAsync("打开游戏日志失败：" + ex.Message, "游戏日志");
        }
    }

    private async Task AnalyzeAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null) return;

        if (IsLiveMode)
        {
            RefreshLiveDiagnostics();
            return;
        }

        var path = SelectedLogFile?.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            await PlatformServices.Dialogs.WarnAsync("请先在顶部选择一个日志文件。", "分析日志");
            return;
        }

        var token = ++_analyzeToken;
        Output = string.Empty;
        try
        {
            await GameLogAnalysis.AnalyzeFileStreamAsync(version, path, block =>
            {
                if (token != _analyzeToken) return;
                void Append()
                {
                    Output += block + Environment.NewLine;
                }
                if (Dispatcher.UIThread.CheckAccess()) Append();
                else Dispatcher.UIThread.Post(Append);
            });
            Log("游戏日志分析完成：" + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Output += "分析失败：" + ex.Message;
            LogError("分析游戏日志失败", ex);
        }
    }

    private async Task ClearCachesAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null || _busy) return;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                "将删除该版本除存档库（saves/details）以外的所有 IndexedDB：\n\n" +
                "- Mod 图片缓存、翻译缓存、模组包缓存\n- Mod 的本地设置与缓存\n\n" +
                "存档不会被删除，但此操作不可撤销。\n\n继续？",
                "清空 Mod 缓存（第 1 次确认）", "继续"))
            return;
        if (!await PlatformServices.Dialogs.ConfirmAsync(
                "再次确认：确定清空 Mod 缓存吗？\n\n" +
                "建议先关闭正在运行的游戏页面；清空会在下次启动该版本时自动执行，并自动重新加载一次页面。",
                "清空 Mod 缓存（第 2 次确认）", "确认清空"))
            return;

        try
        {
            SlotSaveStore.RequestCacheClear(version.FullPath);
            Log("已登记清空 Mod 缓存（保留存档库）：" + version.Folder);
            await PlatformServices.Dialogs.InfoAsync(
                "已登记。\n\n下次启动该版本时会先清空除存档库以外的 IndexedDB，然后自动重新加载页面。\n" +
                "如果游戏正在运行，请先关闭再重新启动。", "清空 Mod 缓存");
        }
        catch (Exception ex)
        {
            LogError("登记清空 Mod 缓存失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("登记失败：" + ex.Message, "清空 Mod 缓存");
        }
    }

    private void RefreshLiveDiagnostics()
    {
        if (!IsLiveMode || !_active) return;

        var version = SelectedTarget?.Info;
        if (version == null)
        {
            LiveHint = "未选择 http 版本";
            Output = "实时诊断需要先选择一个非 file 模式的版本。";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("== 实时诊断 ==");
        sb.AppendLine($"版本：{version.DisplayName}（{version.Folder}）");

        if (!ServerManager.IsRunning(version.Folder))
        {
            LiveHint = "游戏未运行";
            sb.AppendLine("状态：游戏未运行");
            sb.AppendLine();
            sb.AppendLine("启动该版本后，这里会自动统计当前浏览器实例的日志（每 2 秒刷新），");
            sb.AppendLine("并针对 warning / error / fatal 级别的条目生成诊断分析。");
            Output = sb.ToString();
            return;
        }

        var live = GameLogService.GetLive(version.FullPath);
        LiveHint = live == null ? "游戏运行中 · 等待日志上报" : "游戏运行中 · 每 2 秒刷新";
        sb.AppendLine("状态：游戏运行中（只统计当前浏览器实例，不展示原始日志）");
        sb.AppendLine();

        if (live == null)
        {
            sb.AppendLine("当前浏览器实例还没有日志上报：游戏页刚打开或控制台暂无输出。");
        }
        else
        {
            sb.AppendLine($"日志文件：{live.File}");
            sb.AppendLine($"最近写入：{live.LastWriteAt?.ToString("HH:mm:ss") ?? "—"}");
            if (live.Stopped) sb.AppendLine("注意：浏览器端已停止日志上报，统计可能不再增长。");
            sb.AppendLine();
            sb.AppendLine("== 统计 ==");
            sb.AppendLine($"Information：{live.Information} 条");
            sb.AppendLine($"Warning：{live.Warning} 条");
            sb.AppendLine($"Error：{live.Error} 条");
            sb.AppendLine($"Fatal：{live.Fatal} 条");
            sb.AppendLine();
            sb.AppendLine("== 诊断分析 ==");
            if (live.Findings.Count == 0)
            {
                sb.AppendLine("当前实例没有 warning / error / fatal 级别的异常条目。");
            }
            else
            {
                var index = 0;
                foreach (var finding in live.Findings)
                {
                    index++;
                    var span = finding.First == finding.Last
                        ? finding.First.ToString("HH:mm:ss")
                        : $"{finding.First:HH:mm:ss} ~ {finding.Last:HH:mm:ss}";
                    sb.AppendLine($"--- 问题 {index}（{finding.Level}，{finding.Count} 次，{span}）---");
                    if (finding.Hints.Count == 0)
                    {
                        sb.AppendLine("可能原因：没有匹配到已知模式，请结合日志上下文判断。");
                    }
                    else
                    {
                        foreach (var hint in finding.Hints) sb.AppendLine("可能原因：" + hint);
                    }
                    sb.AppendLine();
                }
            }
        }

        var text = sb.ToString();
        if (Output != text) Output = text;
    }

    private void SaveGeneral(Action<GeneralSettings> mutate, string message)
    {
        try
        {
            var cfg = AppConfig.Load();
            mutate(cfg.General);
            AppConfig.Save(cfg);
            Log(message);
        }
        catch (Exception ex)
        {
            LogService.Error("保存日志设置失败", ex);
        }
    }

    private bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            RaiseCommands();
        }
    }

    private void RaiseCommands()
    {
        (CompatCheckCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenGameLogCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AnalyzeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCachesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Log(string message)
    {
        try { LogService.Append(message); } catch { }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        void Append()
        {
            var text = string.IsNullOrEmpty(LogText) ? line : LogText + Environment.NewLine + line;
            var lines = text.Split(Environment.NewLine);
            if (lines.Length > 300) text = string.Join(Environment.NewLine, lines[^300..]);
            LogText = text;
        }
        if (Dispatcher.UIThread.CheckAccess()) Append();
        else Dispatcher.UIThread.Post(Append);
    }

    private void LogError(string context, Exception ex)
    {
        Log(context + "：" + ex.Message);
        try { LogService.Error(context, ex); } catch { }
    }

    private static OptionItem Pick(List<OptionItem> options, string? key) =>
        options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase)) ?? options[0];

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>诊断页日志文件下拉项。</summary>
public sealed class DiagLogFileVm
{
    public DiagLogFileVm(string name, string path, string detail)
    {
        Name = name;
        Path = path;
        Detail = detail;
    }

    public string Name { get; }

    public string Path { get; }

    public string Detail { get; }
}
