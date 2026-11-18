using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DoLLauncher.Core.Theme;
using DoLLauncher.Core.Ui;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using DoLLauncher.Themes;
using SubtitleTextHelper = DoLLauncher.Core.Ui.SubtitleText;

namespace DoLLauncher.ViewModels;

/// <summary>设置页视图模型：分类导航、外观、浏览器、下载与版本信息。</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private bool _loading = true;
    private string _currentPath = string.Empty;
    private string _subtitle = string.Empty;
    private string _browserStatus = string.Empty;
    private string _versionInfoText = string.Empty;
    private string _updateStatus = string.Empty;
    private bool _isCheckingUpdate;

    private double _accentHue;
    private double _accentSaturation = 100;
    private double _accentLightness;
    private double _storedSaturation;
    private string _accentPreviewHex = string.Empty;

    private double _heartbeatSeconds = 3;
    private double _heartbeatTimeoutSeconds = 10;
    private double _firstGraceSeconds = 30;
    private double _closeDebounceSeconds = 3;
    private IImage? _avatarImage;
    private string _avatarStatusText = string.Empty;

    private OptionItem _selectedDownloadSource = null!;
    private OptionItem _selectedApiSource = null!;
    private OptionItem _selectedMarketSource = null!;
    private OptionItem _selectedPrefetch = null!;
    private OptionItem _selectedVersionGap = null!;
    private OptionItem _selectedTheme = null!;
    private OptionItem _selectedBrowserMode = null!;
    private OptionItem _selectedClosePage = null!;

    public SettingsViewModel()
    {
        OpenCategoryCommand = new RelayCommand(p => SetPath(p as string ?? string.Empty));
        HomeCommand = new RelayCommand(_ => SetPath(string.Empty));
        OpenModInfoCommand = new RelayCommand(_ => SetPath("数据与索引/模组信息"));
        AccentResetCommand = new RelayCommand(_ => ResetAccent());
        UploadAvatarCommand = new RelayCommand(_ => _ = UploadAvatarAsync());
        ResetAvatarCommand = new RelayCommand(_ => ResetAvatar());
        CheckUpdateCommand = new RelayCommand(_ => _ = CheckUpdateAsync(), _ => !_isCheckingUpdate);
        ProjectHomeCommand = new RelayCommand(_ => OpenUrlSafe(Credits?.ProjectUrl));
        FeedbackCommand = new RelayCommand(_ => OpenFeedback());

        Categories = new ObservableCollection<SettingsCategoryItem>
        {
            new("网络下载", "模组下载源、GitHub 接口源：选择下载安装包和访问接口时走的线路。", "\uE774", "网络下载", new RelayCommand(_ => SetPath("网络下载"))),
            new("数据与索引", "模组市场索引源、启动时预取索引、是否把补全的作者信息写回模组包。", "\uE8B7", "数据与索引", new RelayCommand(_ => SetPath("数据与索引"))),
            new("问题检查", "游戏版本差异告警阈值：决定「可能存在问题」里版本不匹配的敏感度。", "\uE7BA", "问题检查", new RelayCommand(_ => SetPath("问题检查"))),
            new("游戏浏览器", "打开游戏页使用的浏览器：独立 Profile 支持关页自动停服，也可改为系统默认浏览器。", "\uE774", "游戏浏览器", new RelayCommand(_ => SetPath("游戏浏览器"))),
            new("界面与外观", "深色 / 亮色 / 跟随系统主题、主题色与启动器副标题。", "\uE790", "界面与外观", new RelayCommand(_ => SetPath("界面与外观"))),
            new("版本信息", "启动器版本号、提交与构建时间，以及检查更新。", "\uE946", "版本信息", new RelayCommand(_ => SetPath("版本信息"))),
        };

        DownloadSourceOptions = BuildOptions(
            ("auto", "自动（测速选最快）"), ("ghproxy", "GH-Proxy 镜像"), ("ghproxycom", "GH-Proxy.com 镜像"),
            ("llkk", "LLKK 镜像"), ("fastgit", "FastGit 镜像"), ("ddlc", "DDLC 镜像"), ("boki", "Boki 镜像"),
            ("ghproxynet", "GHProxy.net 镜像"), ("worker", "ModHub 加速"), ("github", "GitHub 直连"));
        ApiSourceOptions = BuildOptions(
            ("auto", "自动（直连失败走 GH-Proxy）"), ("github", "GitHub 直连"), ("ghproxy", "GH-Proxy 镜像"));
        MarketSourceOptions = BuildOptions(
            ("auto", "自动（主源失败时用备用）"), ("primary", "主源 dol.alseece.top"), ("backup", "备用源 workers.dev"));
        PrefetchOptions = BuildOptions(
            ("parallel", "同时拉取（推荐）"), ("modFirst", "先模组市场，后游戏本体"),
            ("gameFirst", "先游戏本体，后模组市场"), ("none", "启动时不预取"));
        VersionGapOptions = BuildOptions(("1", "相差 1 段（默认）"), ("2", "相差 2 段"), ("3", "相差 3 段"), ("4", "相差 4 段"));
        ThemeOptions = BuildOptions(("system", "跟随系统"), ("light", "亮色"), ("dark", "深色"));
        BrowserModeOptions = BuildOptions(("profile", "独立 Profile（推荐）"), ("default", "系统默认浏览器"), ("custom", "自定义浏览器"));
        ClosePageOptions = BuildOptions(("close", "直接关闭本地服务器"), ("ask", "询问是否保留后台服务"));

        var general = AppConfig.Load().General;
        _selectedDownloadSource = Pick(DownloadSourceOptions, general.ModDownloadSource);
        _selectedApiSource = Pick(ApiSourceOptions, general.GithubApiSource);
        _selectedMarketSource = Pick(MarketSourceOptions, general.MarketSource);
        _selectedPrefetch = Pick(PrefetchOptions, general.Prefetch);
        _selectedVersionGap = Pick(VersionGapOptions, ProblemService.NormalizeThreshold(general.VersionGapThreshold).ToString());
        _selectedTheme = Pick(ThemeOptions, ThemeManager.Normalize(general.ThemeMode));
        _selectedBrowserMode = Pick(BrowserModeOptions, NormalizeBrowserMode(general.BrowserMode));
        _selectedClosePage = Pick(ClosePageOptions,
            (general.ClosePageBehavior ?? string.Empty).Trim().ToLowerInvariant() == "ask" ? "ask" : "close");

        WriteModMetadata = general.WriteModMetadata;
        AutoCheckUpdate = general.AutoCheckUpdate;
        ShowAnimations = general.ShowAnimations;
        LoadAvatar();
        BrowserExecutable = general.BrowserExecutable ?? string.Empty;
        BrowserArguments = general.BrowserArguments ?? string.Empty;
        BrowserUseProfile = general.BrowserUseProfile;
        _subtitle = (general.Subtitle ?? string.Empty).Trim();
        _heartbeatSeconds = Math.Clamp(general.HeartbeatSeconds, 1, 30);
        _heartbeatTimeoutSeconds = Math.Clamp(general.HeartbeatTimeoutSeconds, 5, 180);
        _firstGraceSeconds = Math.Clamp(general.FirstHeartbeatGraceSeconds, 5, 600);
        _closeDebounceSeconds = Math.Clamp(general.CloseDebounceSeconds, 0, 60);
        LoadAccent(general);
        VersionInfoText = BuildInfo.Display;

        try
        {
            Credits = CreditsService.Load();
            foreach (var entry in Credits.Entries)
                CreditItems.Add(new CreditItemVm(entry));
        }
        catch (Exception ex)
        {
            LogService.Error("读取致谢名单失败", ex);
        }

        _loading = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>版本差异阈值变化后由主视图模型重新计算模组问题。</summary>
    public event Action? VersionGapChanged;

    public ObservableCollection<SettingsCategoryItem> Categories { get; }

    public ObservableCollection<CreditItemVm> CreditItems { get; } = new();

    public List<OptionItem> DownloadSourceOptions { get; }

    public List<OptionItem> ApiSourceOptions { get; }

    public List<OptionItem> MarketSourceOptions { get; }

    public List<OptionItem> PrefetchOptions { get; }

    public List<OptionItem> VersionGapOptions { get; }

    public List<OptionItem> ThemeOptions { get; }

    public List<OptionItem> BrowserModeOptions { get; }

    public List<OptionItem> ClosePageOptions { get; }

    public ICommand OpenCategoryCommand { get; }

    public ICommand HomeCommand { get; }

    public ICommand OpenModInfoCommand { get; }

    public ICommand AccentResetCommand { get; }

    public ICommand UploadAvatarCommand { get; }

    public ICommand ResetAvatarCommand { get; }

    public ICommand CheckUpdateCommand { get; }

    public ICommand ProjectHomeCommand { get; }

    public ICommand FeedbackCommand { get; }

    public CreditsFile? Credits { get; }

    public bool IsHome => _currentPath.Length == 0;

    public bool IsNetwork => _currentPath == "网络下载";

    public bool IsData => _currentPath == "数据与索引";

    public bool IsModInfo => _currentPath == "数据与索引/模组信息";

    public bool IsProblem => _currentPath == "问题检查";

    public bool IsBrowser => _currentPath == "游戏浏览器";

    public bool IsAppearance => _currentPath == "界面与外观";

    public bool IsVersion => _currentPath == "版本信息";

    public string CurrentTitle => _currentPath.Length == 0
        ? "设置"
        : string.Join(" › ", _currentPath.Split('/'));

    public string Description => _currentPath switch
    {
        "" => "选择一个分类查看相关设置。",
        "网络下载" => "下载安装包与访问 GitHub 接口时使用的线路，失败会自动回退。",
        "数据与索引" => "模组市场索引的拉取地址与时机，以及模组信息写回。",
        "数据与索引/模组信息" => "是否把索引里补全的作者 / 主页写回模组包。",
        "问题检查" => "「可能存在问题」里版本差异告警的敏感度。",
        "游戏浏览器" => "打开游戏页使用的浏览器与关页停服行为，修改后下次启动游戏生效。",
        "界面与外观" => "主题、主题色与启动器副标题，修改后立即生效。",
        "版本信息" => "当前启动器的版本、提交与构建时间，以及启动时自动检测与手动检查更新。",
        _ => string.Empty,
    };

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (_subtitle == value) return;
            _subtitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleText));
        }
    }

    public string SubtitleText => SubtitleTextHelper.Resolve(_subtitle);

    public OptionItem SelectedDownloadSource
    {
        get => _selectedDownloadSource;
        set
        {
            if (!Set(ref _selectedDownloadSource, value) || _loading) return;
            SaveGeneral(g => g.ModDownloadSource = value.Key, "设置：模组下载源 = " + value.Label);
        }
    }

    public OptionItem SelectedApiSource
    {
        get => _selectedApiSource;
        set
        {
            if (!Set(ref _selectedApiSource, value) || _loading) return;
            SaveGeneral(g => g.GithubApiSource = value.Key, "设置：GitHub 接口源 = " + value.Label);
        }
    }

    public OptionItem SelectedMarketSource
    {
        get => _selectedMarketSource;
        set
        {
            if (!Set(ref _selectedMarketSource, value) || _loading) return;
            SaveGeneral(g => g.MarketSource = value.Key, "设置：模组市场索引源 = " + value.Label);
        }
    }

    public OptionItem SelectedPrefetch
    {
        get => _selectedPrefetch;
        set
        {
            if (!Set(ref _selectedPrefetch, value) || _loading) return;
            SaveGeneral(g => g.Prefetch = value.Key, "设置：启动时预取索引 = " + value.Label);
        }
    }

    public OptionItem SelectedVersionGap
    {
        get => _selectedVersionGap;
        set
        {
            if (!Set(ref _selectedVersionGap, value) || _loading) return;
            var threshold = ProblemService.NormalizeThreshold(int.TryParse(value.Key, out var parsed) ? parsed : 1);
            SaveGeneral(g => g.VersionGapThreshold = threshold, "设置：游戏版本差异告警阈值 = " + threshold);
            VersionGapChanged?.Invoke();
        }
    }

    public OptionItem SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Set(ref _selectedTheme, value) || _loading) return;
            ThemeManager.SetMode(value.Key);
            UpdateAccentPreview();
            LogService.Append("设置：界面主题 = " + value.Label);
        }
    }

    public OptionItem SelectedBrowserMode
    {
        get => _selectedBrowserMode;
        set
        {
            if (!Set(ref _selectedBrowserMode, value) || _loading) return;
            if (value.Key == "custom" && !IsBrowserPathValid(BrowserExecutable))
            {
                if (!PickBrowserExecutable())
                {
                    RestoreBrowserMode();
                    return;
                }
            }
            SaveGeneral(g => g.BrowserMode = value.Key, "设置：游戏浏览器承载方式 = " + value.Label);
            BrowserStatus = "已保存，下次启动游戏时生效。";
            OnPropertyChanged(nameof(IsCustomBrowser));
        }
    }

    public OptionItem SelectedClosePage
    {
        get => _selectedClosePage;
        set
        {
            if (!Set(ref _selectedClosePage, value) || _loading) return;
            SaveGeneral(g => g.ClosePageBehavior = value.Key, "设置：关闭游戏页面时 = " + value.Label);
        }
    }

    public bool WriteModMetadata
    {
        get => _writeModMetadata;
        set
        {
            if (!Set(ref _writeModMetadata, value) || _loading) return;
            SaveGeneral(g => g.WriteModMetadata = value, "设置：补全信息写回模组包 = " + (value ? "开" : "关"));
        }
    }
    private bool _writeModMetadata;

    public bool AutoCheckUpdate
    {
        get => _autoCheckUpdate;
        set
        {
            if (!Set(ref _autoCheckUpdate, value) || _loading) return;
            SaveGeneral(g => g.AutoCheckUpdate = value, "设置：启动时自动检测更新 = " + (value ? "开" : "关"));
        }
    }
    private bool _autoCheckUpdate;

    public bool ShowAnimations
    {
        get => _showAnimations;
        set
        {
            if (!Set(ref _showAnimations, value) || _loading) return;
            SaveGeneral(g => g.ShowAnimations = value, "设置：界面过渡动画 = " + (value ? "开" : "关"));
        }
    }
    private bool _showAnimations = true;

    public IImage? AvatarImage
    {
        get => _avatarImage;
        private set => Set(ref _avatarImage, value);
    }

    public string AvatarStatusText
    {
        get => _avatarStatusText;
        private set => Set(ref _avatarStatusText, value);
    }

    public string BrowserExecutable { get; set; } = string.Empty;

    public string BrowserArguments { get; set; } = string.Empty;

    public bool BrowserUseProfile
    {
        get => _browserUseProfile;
        set
        {
            if (!Set(ref _browserUseProfile, value) || _loading) return;
            SaveGeneral(g => g.BrowserUseProfile = value, "设置：自定义浏览器独立 Profile = " + (value ? "开" : "关"));
        }
    }
    private bool _browserUseProfile;

    public bool IsCustomBrowser => _selectedBrowserMode.Key == "custom";

    public string BrowserStatus
    {
        get => _browserStatus;
        private set => Set(ref _browserStatus, value);
    }

    public string VersionInfoText
    {
        get => _versionInfoText;
        private set => Set(ref _versionInfoText, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => Set(ref _updateStatus, value);
    }

    // ---------- 关页检测时间参数 ----------

    public double HeartbeatSeconds
    {
        get => _heartbeatSeconds;
        set
        {
            if (!Set(ref _heartbeatSeconds, value)) return;
            OnPropertyChanged(nameof(HeartbeatText));
        }
    }

    public string HeartbeatText => (int)Math.Round(_heartbeatSeconds) + " 秒";

    public double HeartbeatTimeoutSeconds
    {
        get => _heartbeatTimeoutSeconds;
        set
        {
            if (!Set(ref _heartbeatTimeoutSeconds, value)) return;
            OnPropertyChanged(nameof(HeartbeatTimeoutText));
        }
    }

    public string HeartbeatTimeoutText => (int)Math.Round(_heartbeatTimeoutSeconds) + " 秒";

    public double FirstGraceSeconds
    {
        get => _firstGraceSeconds;
        set
        {
            if (!Set(ref _firstGraceSeconds, value)) return;
            OnPropertyChanged(nameof(FirstGraceText));
        }
    }

    public string FirstGraceText => (int)Math.Round(_firstGraceSeconds) + " 秒";

    public double CloseDebounceSeconds
    {
        get => _closeDebounceSeconds;
        set
        {
            if (!Set(ref _closeDebounceSeconds, value)) return;
            OnPropertyChanged(nameof(CloseDebounceText));
        }
    }

    public string CloseDebounceText => (int)Math.Round(_closeDebounceSeconds) + " 秒";

    // ---------- 主题色 ----------

    public double AccentHue
    {
        get => _accentHue;
        set
        {
            if (!Set(ref _accentHue, value)) return;
            OnPropertyChanged(nameof(AccentHueText));
            UpdateAccentPreview();
        }
    }

    public string AccentHueText => (int)Math.Round(_accentHue) + "°";

    public double AccentSaturation
    {
        get => _accentSaturation;
        set
        {
            if (!Set(ref _accentSaturation, value)) return;
            OnPropertyChanged(nameof(AccentSaturationText));
            UpdateAccentPreview();
        }
    }

    public string AccentSaturationText => (int)Math.Round(_accentSaturation) + "%";

    public double AccentLightness
    {
        get => _accentLightness;
        set
        {
            if (!Set(ref _accentLightness, value)) return;
            OnPropertyChanged(nameof(AccentLightnessText));
            UpdateAccentPreview();
        }
    }

    public string AccentLightnessText => (int)Math.Round(_accentLightness) + "%";

    public string AccentPreviewHex
    {
        get => _accentPreviewHex;
        private set
        {
            if (!Set(ref _accentPreviewHex, value)) return;
            OnPropertyChanged(nameof(AccentPreviewBrush));
        }
    }

    public IBrush AccentPreviewBrush
    {
        get
        {
            try
            {
                return new SolidColorBrush(Color.Parse(_accentPreviewHex));
            }
            catch
            {
                return Brushes.Transparent;
            }
        }
    }

    // ---------- 保存动作（由视图在失焦 / 松手时调用） ----------

    public void SaveBrowserExecutable()
    {
        var path = (BrowserExecutable ?? string.Empty).Trim();
        BrowserExecutable = path;
        if (path.Length > 0 && !File.Exists(path))
        {
            BrowserStatus = "浏览器路径不存在，已保留原设置。";
            return;
        }
        SaveGeneral(g => g.BrowserExecutable = path, "设置：自定义浏览器路径 = " + (path.Length == 0 ? "（空）" : path));
        BrowserStatus = "已保存，下次启动游戏时生效。";
    }

    public void SaveBrowserArguments()
    {
        var args = (BrowserArguments ?? string.Empty).Trim();
        BrowserArguments = args;
        SaveGeneral(g => g.BrowserArguments = args, "设置：自定义浏览器参数已更新");
        BrowserStatus = "已保存，下次启动游戏时生效。";
    }

    public void SaveSubtitle()
    {
        var value = (Subtitle ?? string.Empty).Trim();
        Subtitle = value;
        SaveGeneral(g => g.Subtitle = value, "设置：启动器副标题 = " + (value.Length == 0 ? "（默认）" : value));
    }

    public void SaveTiming()
    {
        if (_loading) return;
        var heartbeat = (int)Math.Round(HeartbeatSeconds);
        var timeout = (int)Math.Round(HeartbeatTimeoutSeconds);
        var grace = (int)Math.Round(FirstGraceSeconds);
        var debounce = (int)Math.Round(CloseDebounceSeconds);
        SaveGeneral(g =>
        {
            g.HeartbeatSeconds = heartbeat;
            g.HeartbeatTimeoutSeconds = timeout;
            g.FirstHeartbeatGraceSeconds = grace;
            g.CloseDebounceSeconds = debounce;
        }, $"设置：关页检测 心跳 {heartbeat}s / 超时 {timeout}s / 宽限 {grace}s / 防抖 {debounce}s");
    }

    public void CommitAccentColor()
    {
        if (_loading) return;
        var hex = BuildAccentHex();
        try
        {
            var cfg = AppConfig.Load();
            if (string.Equals(cfg.General.ThemeAccentColor, hex, StringComparison.OrdinalIgnoreCase)) return;
            cfg.General.ThemeAccentColor = hex;
            AppConfig.Save(cfg);
            ThemeManager.Apply();
            LogService.Append("设置：主题主色 = " + hex);
        }
        catch (Exception ex)
        {
            LogService.Error("保存主题主色失败", ex);
        }
    }

    public void CommitAccentSaturation()
    {
        if (_loading) return;
        var percent = (int)Math.Round(AccentSaturation);
        try
        {
            var cfg = AppConfig.Load();
            if (cfg.General.ThemeAccentSaturation == percent) return;
            cfg.General.ThemeAccentSaturation = percent;
            AppConfig.Save(cfg);
            ThemeManager.Apply();
            LogService.Append("设置：主题主色饱和度 = " + percent + "%");
        }
        catch (Exception ex)
        {
            LogService.Error("保存主题主色饱和度失败", ex);
        }
    }

    // ---------- 内部实现 ----------

    private void SetPath(string path)
    {
        _currentPath = path;
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsNetwork));
        OnPropertyChanged(nameof(IsData));
        OnPropertyChanged(nameof(IsModInfo));
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(IsBrowser));
        OnPropertyChanged(nameof(IsAppearance));
        OnPropertyChanged(nameof(IsVersion));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(Description));
    }

    private void LoadAccent(GeneralSettings general)
    {
        var rgb = ParseAccent(general.ThemeAccentColor);
        var (h, s, l) = rgb.ToHsl();
        _storedSaturation = s;
        _accentHue = Math.Round(h);
        _accentLightness = Math.Round(l * 100);
        _accentSaturation = Math.Clamp(general.ThemeAccentSaturation, 0, 100);
        UpdateAccentPreview();
    }

    private void LoadAvatar()
    {
        var configured = (AppConfig.Load().General.AvatarPath ?? string.Empty).Trim();
        var (image, reason) = ResolveAvatar(configured);
        AvatarImage = image;
        AvatarStatusText = configured.Length == 0
            ? "当前使用内置占位图；点击「上传头像」可选择本地图片。"
            : reason.Length > 0
                ? "头像文件不可用，已回退内置占位图：" + reason
                : "当前头像：" + Path.GetFileName(configured);
    }

    private async Task UploadAvatarAsync()
    {
        var path = PlatformServices.FilePicker.PickFile(
            "选择玩家头像", "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*");
        if (path == null) return;

        var (ok, reason) = AvatarRules.Validate(path);
        if (!ok)
        {
            await PlatformServices.Dialogs.WarnAsync(reason, "玩家头像");
            return;
        }

        try
        {
            var userDir = Path.Combine(Paths.Root, "user");
            Directory.CreateDirectory(userDir);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var dest = Path.Combine(userDir, "avatar" + ext);
            var sourceFull = Path.GetFullPath(path);
            var destFull = Path.GetFullPath(dest);

            foreach (var old in Directory.GetFiles(userDir, "avatar.*"))
            {
                if (string.Equals(Path.GetFullPath(old), sourceFull, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch { }
            }

            if (!string.Equals(sourceFull, destFull, StringComparison.OrdinalIgnoreCase))
                File.Copy(sourceFull, destFull, overwrite: true);

            var relative = Path.Combine("user", "avatar" + ext).Replace('\\', '/');
            SaveGeneral(g => g.AvatarPath = relative, "设置：已更新玩家头像 = " + relative);
            LoadAvatar();
        }
        catch (Exception ex)
        {
            LogService.Error("保存玩家头像失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("保存头像失败：" + ex.Message, "玩家头像");
        }
    }

    private void ResetAvatar()
    {
        try
        {
            SaveGeneral(g => g.AvatarPath = string.Empty, "设置：已恢复默认头像");
            LoadAvatar();
        }
        catch (Exception ex)
        {
            LogService.Error("恢复默认头像失败", ex);
        }
    }

    private static (IImage? Image, string Reason) ResolveAvatar(string configured)
    {
        if (configured.Length > 0)
        {
            var full = Path.IsPathRooted(configured) ? configured : Path.Combine(Paths.Root, configured);
            var (ok, reason) = AvatarRules.Validate(full);
            if (ok)
            {
                try
                {
                    return (new Bitmap(full), string.Empty);
                }
                catch
                {
                    return (null, "图片无法解码。");
                }
            }
            return (null, reason);
        }

        try
        {
            return (new Bitmap(AssetLoader.Open(new Uri("avares://DoLLauncher.App/Assets/dol-favicon.png"))), string.Empty);
        }
        catch
        {
            return (null, string.Empty);
        }
    }

    private void ResetAccent()
    {
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.ThemeAccentColor = string.Empty;
            cfg.General.ThemeAccentSaturation = 100;
            AppConfig.Save(cfg);
            LoadAccent(cfg.General);
            OnPropertyChanged(nameof(AccentHue));
            OnPropertyChanged(nameof(AccentHueText));
            OnPropertyChanged(nameof(AccentSaturation));
            OnPropertyChanged(nameof(AccentSaturationText));
            OnPropertyChanged(nameof(AccentLightness));
            OnPropertyChanged(nameof(AccentLightnessText));
            ThemeManager.Apply();
            LogService.Append("设置：主题主色已恢复默认");
        }
        catch (Exception ex)
        {
            LogService.Error("恢复默认主题色失败", ex);
        }
    }

    private void UpdateAccentPreview()
    {
        try
        {
            var palette = ThemePaletteBuilder.Build(
                ThemeManager.IsDark ? ThemePalette.Dark : ThemePalette.Light,
                BuildAccentHex(),
                (int)Math.Round(AccentSaturation));
            AccentPreviewHex = palette.TryGetValue("AccentBrush", out var value) ? value : BuildAccentHex();
        }
        catch
        {
            AccentPreviewHex = BuildAccentHex();
        }
    }

    private string BuildAccentHex() =>
        RgbColor.FromHsl(AccentHue, _storedSaturation, AccentLightness / 100.0).ToArgbHex();

    private static RgbColor ParseAccent(string? hex)
    {
        if (RgbColor.TryParse(hex, out var rgb)) return rgb;
        RgbColor.TryParse(ThemePaletteBuilder.DefaultAccentColor, out rgb);
        return rgb;
    }

    public bool PickBrowserExecutable()
    {
        var path = PlatformServices.FilePicker.PickFile(
            "选择浏览器可执行文件", "可执行文件|*.exe;*|所有文件|*.*");
        if (path == null) return false;
        BrowserExecutable = path;
        SaveBrowserExecutable();
        return true;
    }

    private void RestoreBrowserMode()
    {
        _loading = true;
        try
        {
            _selectedBrowserMode = Pick(BrowserModeOptions, NormalizeBrowserMode(AppConfig.Load().General.BrowserMode));
            OnPropertyChanged(nameof(SelectedBrowserMode));
            OnPropertyChanged(nameof(IsCustomBrowser));
        }
        finally
        {
            _loading = false;
        }
    }

    private static string NormalizeBrowserMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "default" => "default",
        "custom" => "custom",
        _ => "profile",
    };

    private static bool IsBrowserPathValid(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim());

    private void SaveGeneral(Action<GeneralSettings> mutate, string message)
    {
        if (_loading) return;
        try
        {
            var cfg = AppConfig.Load();
            mutate(cfg.General);
            AppConfig.Save(cfg);
            LogService.Append(message);
        }
        catch (Exception ex)
        {
            LogService.Error("保存设置失败", ex);
        }
    }

    private async Task CheckUpdateAsync()
    {
        _isCheckingUpdate = true;
        (CheckUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        UpdateStatus = "正在检查更新…";
        try
        {
            var release = await UpdateService.CheckAsync();
            if (release.Tag.Length == 0)
            {
                UpdateStatus = "没有可用的 Release 信息。";
                return;
            }

            var hasUpdate = BuildInfo.Tag == "dev" || UpdateService.IsNewer(release.Tag, BuildInfo.Tag);
            UpdateStatus = hasUpdate
                ? BuildInfo.Tag == "dev"
                    ? $"当前为开发版构建，最新 Release 为 {release.Tag}。"
                    : $"发现新版本 {release.Tag}（当前 {BuildInfo.Tag}）。"
                : $"已是最新版本（{BuildInfo.Tag}）。";
        }
        catch (Exception ex)
        {
            UpdateStatus = ex.Message.Contains("404")
                ? "仓库还没有发布任何 Release。"
                : "检查更新失败：" + ex.Message;
            LogService.Error("检查更新失败", ex);
        }
        finally
        {
            _isCheckingUpdate = false;
            (CheckUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void OpenFeedback()
    {
        var home = (Credits?.ProjectUrl ?? string.Empty).Trim().TrimEnd('/');
        if (home.Length == 0)
        {
            _ = PlatformServices.Dialogs.InfoAsync("credits.json 里还没有填写项目主页地址。", "反馈与建议");
            return;
        }
        OpenUrlSafe(home + "/issues/new/choose");
    }

    private static void OpenUrlSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            PlatformServices.Links.OpenUrl(url);
        }
        catch (Exception ex)
        {
            LogService.Error("打开链接失败", ex);
        }
    }

    private static List<OptionItem> BuildOptions(params (string Key, string Label)[] values) =>
        values.Select(v => new OptionItem(v.Key, v.Label)).ToList();

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

/// <summary>下拉选项：Key 对应配置文件里的原始值，Label 用于显示。</summary>
public sealed class OptionItem
{
    public OptionItem(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }

    public string Label { get; }
}

/// <summary>设置首页的分类卡片。</summary>
public sealed class SettingsCategoryItem
{
    public SettingsCategoryItem(string title, string description, string icon, string path, ICommand openCommand)
    {
        Title = title;
        Description = description;
        Icon = icon;
        Path = path;
        OpenCommand = openCommand;
    }

    public string Title { get; }

    public string Description { get; }

    public string Icon { get; }

    public string Path { get; }

    public ICommand OpenCommand { get; }
}

/// <summary>致谢名单条目：显示职位 / 名字，并可直接打开主页。</summary>
public sealed class CreditItemVm
{
    public CreditItemVm(CreditEntry entry)
    {
        Role = entry.Role;
        Name = entry.Name;
        var link = new[] { entry.Github, entry.Bilibili, entry.Website }
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
        Link = link;
        HasLink = link.Length > 0;
        OpenCommand = new RelayCommand(_ => Open());
    }

    public string Role { get; }

    public string Name { get; }

    public string Link { get; }

    public bool HasLink { get; }

    public ICommand OpenCommand { get; }

    public string Title => Role.Length == 0 ? Name : $"{Role} · {Name}";

    private void Open()
    {
        if (!HasLink) return;
        try
        {
            PlatformServices.Links.OpenUrl(Link);
        }
        catch (Exception ex)
        {
            LogService.Error("打开致谢链接失败", ex);
        }
    }
}
