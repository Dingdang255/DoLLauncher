using System.IO;
using System.Text;
using DoLLauncher.Maui.Settings;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class SettingsPage : ContentPage
{
    private readonly List<string> _logLines = new();
    private long _logOffset;
    private IDispatcherTimer? _logTimer;
    private bool _logExpanded;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_logExpanded) StartLogPolling();
    }

    protected override void OnDisappearing()
    {
        StopLogPolling();
        base.OnDisappearing();
    }

    private async void OnCategoryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string category) return;
        Page? page = category switch
        {
            "网络下载" => new NetworkSettingsPage(),
            "数据与索引" => new DataIndexSettingsPage(),
            "问题检查" => new ProblemSettingsPage(),
            "游戏" => new GameSettingsPage(),
            "界面与外观" => new AppearanceSettingsPage(),
            "日志与诊断" => new LogSettingsPage(),
            "版本信息" => new VersionInfoPage(),
            "关于" => new AboutPage(),
            _ => null,
        };
        if (page != null) await Navigation.PushAsync(page);
    }

    private void OnToggleLogClicked(object? sender, EventArgs e) => SetLogExpanded(!_logExpanded);

    private void OnToggleLog(object? sender, TappedEventArgs e) => SetLogExpanded(!_logExpanded);

    private void SetLogExpanded(bool expanded)
    {
        _logExpanded = expanded;
        LogEditor.IsVisible = expanded;
        LogToggleButton.Text = expanded ? "收起" : "展开";
        if (expanded)
        {
            StartLogPolling();
            PollLog();
        }
        else
        {
            StopLogPolling();
        }
    }

    private void StartLogPolling()
    {
        if (_logTimer == null)
        {
            _logTimer = Dispatcher.CreateTimer();
            _logTimer.Interval = TimeSpan.FromSeconds(1);
            _logTimer.Tick += (_, _) => PollLog();
        }
        if (!_logTimer.IsRunning) _logTimer.Start();
    }

    private void StopLogPolling()
    {
        if (_logTimer is { IsRunning: true }) _logTimer.Stop();
    }

    /// <summary>LogService 没有订阅接口，这里按字节偏移增量读取日志文件模拟实时记录。</summary>
    private void PollLog()
    {
        try
        {
            var path = LogService.FilePath;
            if (!File.Exists(path)) return;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _logOffset)
            {
                _logOffset = 0;
                _logLines.Clear();
            }
            if (stream.Length == _logOffset) return;

            stream.Seek(_logOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            _logOffset = stream.Position;

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) continue;
                _logLines.Add(line);
            }
            while (_logLines.Count > 200) _logLines.RemoveAt(0);

            LogEditor.Text = string.Join("\n", _logLines);
        }
        catch
        {
            // 读取失败不影响设置页操作
        }
    }

    private async void OnOpenLogViewer(object? sender, EventArgs e) =>
        await TextViewerPage.ShowAsync("运行记录", LogEditor.Text, "最近 200 行启动器日志");

    private void OnClearLog(object? sender, EventArgs e)
    {
        _logLines.Clear();
        LogEditor.Text = string.Empty;
        try
        {
            var path = LogService.FilePath;
            _logOffset = File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            _logOffset = 0;
        }
    }
}
