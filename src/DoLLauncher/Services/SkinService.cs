using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoLLauncher.Controls;
using DoLLauncher.Core.Skin;
using DoLLauncher.Core.Theme;

namespace DoLLauncher.Services;

/// <summary>
/// 本地 UI 美化包：加载 / 卸载 / 预览。
/// 只处理 <c>skins\&lt;名称&gt;\skin.json</c> 与包内图片，不执行任何脚本。
/// 优先级：美化包 &gt; 自定义主题色 &gt; 默认主题。
/// </summary>
public static class SkinService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SkinsRoot => Path.Combine(Paths.Root, "skins");

    /// <summary>当前美化包目录（供声明式组件解析包内图片等相对路径）。</summary>
    public static string SkinDirectory => _skinDir;

    /// <summary>当前生效的美化包清单；未启用时为 null。</summary>
    public static SkinManifest? Current { get; private set; }

    /// <summary>当前生效的 CSS 样式表；未提供或为空时为 null。</summary>
    public static SkinStyleSheet? StyleSheet { get; private set; }

    /// <summary>当前生效的样式表相对路径，用于状态与日志。</summary>
    public static string StyleSheetPath { get; private set; } = string.Empty;

    /// <summary>当前生效的美化包目录名。</summary>
    public static string CurrentFolder { get; private set; } = string.Empty;

    /// <summary>主窗口背景画刷（图片优先，其次底色）；没有配置时为 null。</summary>
    public static Brush? WindowBackdropBrush { get; private set; }

    /// <summary>弹窗背景画刷（图片优先，其次底色）；没有配置时为 null。</summary>
    public static Brush? DialogBackgroundBrush { get; private set; }

    /// <summary>当前美化包声明的可切换主题（已过滤空 id 与重复项）。</summary>
    public static IReadOnlyList<SkinTheme> Themes => _themes;

    /// <summary>当前生效的主题 id；没有主题时为空串。</summary>
    public static string CurrentThemeId { get; private set; } = string.Empty;

    /// <summary>当前生效的主题；没有主题时为 null。</summary>
    public static SkinTheme? CurrentTheme
    {
        get
        {
            var id = CurrentThemeId;
            if (id.Length == 0) return null;
            foreach (var theme in _themes)
                if (theme.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return theme;
            return null;
        }
    }

    /// <summary>当前环境层 / 动效配置；没有美化包时为全关。</summary>
    public static SkinEffectOptions Effects { get; private set; } = CreateDisabledEffects();

    /// <summary>主题边饰开关（供美化包设置页覆盖）。</summary>
    public static bool OrnamentEnabled { get; set; } = true;

    /// <summary>动效总开关：统一切换 panelIn / breathe / hairline / ripple；没有美化包时恒为 false。</summary>
    public static bool MotionEnabled
    {
        get => Current != null && Effects.Motion is { } motion
               && (motion.PanelIn || motion.Breathe || motion.Hairline || motion.Ripple);
        set
        {
            if (Current == null) return;
            var motion = Effects.Motion;
            if (motion == null)
            {
                motion = new SkinMotionOptions();
                Effects.Motion = motion;
            }
            motion.PanelIn = motion.Breathe = motion.Hairline = motion.Ripple = value;
            RefreshEffects();
        }
    }

    private static readonly Dictionary<string, string> NoVariables = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _variableOverrides = new(StringComparer.OrdinalIgnoreCase);
    private static List<SkinTheme> _themes = new();
    private static Dictionary<string, string> _baseVariables = new(StringComparer.OrdinalIgnoreCase);
    private static string _skinDir = string.Empty;
    private const string AmbientBackTag = "skin-ambient-back";
    private const string AmbientFrontTag = "skin-ambient-front";
    private static string _ambientBackSignature = string.Empty;
    private static string _ambientFrontSignature = string.Empty;
    private static string _ornamentSignature = string.Empty;

    private sealed class ButtonSurfaceOriginal
    {
        public Brush? Background { get; init; }
        public bool HadLocalValue { get; init; }
    }

    private static ConditionalWeakTable<Button, ButtonSurfaceOriginal> _buttonOriginals = new();
    private static readonly List<WeakReference<Button>> _buttonApplied = new();
    private static ImageBrush? _windowSurfaceBrush;
    private static ImageBrush? _buttonSurfaceBrush;
    private static ImageBrush? _cardImageBrush;
    private static Brush? _cardOriginalBrush;
    private static bool _applyingSurfaces;

    /// <summary>窗口背景底图覆盖（相对皮肤目录）；为空表示不覆盖。</summary>
    public static string WindowImageOverride { get; private set; } = string.Empty;

    /// <summary>按钮底图覆盖（相对皮肤目录）；为空表示不覆盖。</summary>
    public static string ButtonImageOverride { get; private set; } = string.Empty;

    /// <summary>卡片底图覆盖（相对皮肤目录）；为空表示不覆盖。</summary>
    public static string CardImageOverride { get; private set; } = string.Empty;

    /// <summary>卡片底图是否正在占用 <c>CardBrush</c> 资源（供主题调色时跳过该键）。</summary>
    internal static bool HasCardSurfaceImage => _cardImageBrush != null;

    /// <summary>美化包主色；为空表示不覆盖主题色。</summary>
    public static string? AccentColor
    {
        get
        {
            var text = (Current?.AccentColor ?? string.Empty).Trim();
            return RgbColor.TryParse(text, out _) ? text : null;
        }
    }

    /// <summary>
    /// 美化包背景过深时要求使用深色配色，避免深底 + 深色文字看不清。
    /// 只看显式给出的背景底色；只提供主色或背景图时不影响主题深浅。
    /// </summary>
    public static bool RequiresDarkPalette =>
        IsDarkColor(Current?.WindowBackgroundColor)
        || IsDarkColor(Current?.DialogBackgroundColor)
        || IsPaletteBgDark();

    /// <summary>设置页展示的状态说明。</summary>
    public static string StatusText
    {
        get
        {
            var skin = Current;
            if (skin == null) return "当前未启用美化包。";
            var author = string.IsNullOrWhiteSpace(skin.Author) ? string.Empty : $" · {skin.Author}";
            var version = string.IsNullOrWhiteSpace(skin.Version) ? string.Empty : $" v{skin.Version}";
            var style = StyleSheet is { Rules.Count: > 0 } ? $" · CSS {StyleSheet.Rules.Count} 条规则" : string.Empty;
            return $"已启用：{skin.Name}{version}{author}{style}";
        }
    }

    /// <summary>启动时按配置恢复上次启用的美化包。</summary>
    public static void Initialize()
    {
        // 全局 Loaded 钩子先装好，之后动态创建的控件也能套用样式
        SkinStyleApplier.Initialize();
        try
        {
            var folder = (AppConfig.Load().General.SkinFolder ?? string.Empty).Trim();
            if (folder.Length == 0)
            {
                ClearCurrent();
                return;
            }
            if (!Activate(folder))
            {
                LogService.Append($"美化包 {folder} 不可用，已回退默认外观。");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("初始化美化包失败", ex);
            ClearCurrent();
        }
    }

    /// <summary>从 zip 安装并立即启用；成功返回提示文本。</summary>
    public static (bool Ok, string Message) LoadFromZip(string zipPath, Action<string>? log = null)
    {
        Directory.CreateDirectory(SkinsRoot);
        var temp = Path.Combine(SkinsRoot, ".tmp-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (!File.Exists(zipPath)) return (false, "美化包文件不存在。");
            Directory.CreateDirectory(temp);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifestEntry = zip.Entries.FirstOrDefault(e =>
                    NormalizeEntry(e.FullName).Equals("skin.json", StringComparison.OrdinalIgnoreCase));
                if (manifestEntry == null) return (false, "美化包根目录缺少 skin.json。");

                foreach (var entry in zip.Entries)
                {
                    var name = NormalizeEntry(entry.FullName);
                    if (name.Length == 0 || name.EndsWith('/')) continue;
                    if (!SkinRules.IsSafeRelativePath(name)) continue;

                    var target = Path.GetFullPath(Path.Combine(temp, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(Path.GetFullPath(temp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parent = Path.GetDirectoryName(target);
                    if (parent != null) Directory.CreateDirectory(parent);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            var manifestPath = Path.Combine(temp, "skin.json");
            var manifest = JsonSerializer.Deserialize<SkinManifest>(File.ReadAllText(manifestPath), JsonOpts);
            var (ok, reason) = SkinRules.Validate(manifest);
            if (!ok)
            {
                log?.Invoke("美化包校验失败：" + reason);
                return (false, reason);
            }

            foreach (var (relative, label) in new[]
                     {
                         (manifest!.WindowBackgroundImage, "主窗口背景图"),
                         (manifest.DialogBackgroundImage, "弹窗背景图"),
                     })
            {
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var file = Path.Combine(temp, relative!.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) return (false, $"{label}不存在：{relative}");
            }

            var folder = Sanitize(manifest.Id, manifest.Name, Path.GetFileNameWithoutExtension(zipPath));
            var dest = Path.Combine(SkinsRoot, folder);
            string? backup = null;
            if (Directory.Exists(dest))
            {
                // 重新安装同一皮肤时保留用户的 settings.json 与 custom 底图
                backup = Path.Combine(SkinsRoot, ".bak-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);
                PreserveUserData(dest, backup);
                Directory.Delete(dest, recursive: true);
            }
            Directory.Move(temp, dest);
            if (backup != null)
            {
                RestoreUserData(backup, dest);
                try { Directory.Delete(backup, recursive: true); } catch { }
            }

            if (!Activate(folder)) return (false, "美化包内容读取失败，请检查 skin.json。");

            var cfg = AppConfig.Load();
            cfg.General.SkinFolder = folder;
            AppConfig.Save(cfg);

            log?.Invoke($"已启用美化包：{manifest.Name}（{folder}）");
            return (true, StatusText);
        }
        catch (Exception ex)
        {
            log?.Invoke("加载美化包失败：" + ex.Message);
            return (false, "加载美化包失败：" + ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响结果
            }
        }
    }

    private static void PreserveUserData(string source, string backup)
    {
        var settings = Path.Combine(source, "settings.json");
        if (File.Exists(settings)) File.Copy(settings, Path.Combine(backup, "settings.json"), true);

        var custom = Path.Combine(source, "custom");
        if (Directory.Exists(custom)) CopyDirectory(custom, Path.Combine(backup, "custom"));
    }

    private static void RestoreUserData(string backup, string dest)
    {
        var settings = Path.Combine(backup, "settings.json");
        if (File.Exists(settings)) File.Copy(settings, Path.Combine(dest, "settings.json"), true);

        var custom = Path.Combine(backup, "custom");
        if (Directory.Exists(custom)) CopyDirectory(custom, Path.Combine(dest, "custom"));
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>启用已安装的美化包目录。</summary>
    public static bool Activate(string folder)
    {
        try
        {
            SkinComponentService.Unload();
            SkinScriptHost.Clear();
            ClearSurfaceImages();

            var dir = Path.Combine(SkinsRoot, folder);
            var manifestPath = Path.Combine(dir, "skin.json");
            if (!File.Exists(manifestPath))
            {
                ClearCurrent();
                return false;
            }

            var manifest = JsonSerializer.Deserialize<SkinManifest>(File.ReadAllText(manifestPath), JsonOpts);
            var (ok, reason) = SkinRules.Validate(manifest);
            if (!ok)
            {
                LogService.Append("美化包不可用：" + reason);
                ClearCurrent();
                return false;
            }

            Current = manifest;
            CurrentFolder = folder;
            _skinDir = dir;
            _ambientBackSignature = string.Empty;
            _ambientFrontSignature = string.Empty;
            _ornamentSignature = string.Empty;
            Effects = manifest!.Effects ?? CreateDisabledEffects();
            OrnamentEnabled = true;
            PrepareThemes(manifest);
            DialogBackgroundBrush = BuildBrush(dir, manifest.DialogBackgroundImage, manifest.DialogBackgroundColor, 0.55);
            LoadStyleSheet(dir, manifest);
            _baseVariables = new Dictionary<string, string>(
                StyleSheet?.Variables ?? NoVariables, StringComparer.OrdinalIgnoreCase);
            _variableOverrides.Clear();
            ApplyThemeVariables(CurrentTheme);
            RebuildBackdropBrush();
            SkinStyleApplier.Load(StyleSheet, dir);
            ApplyStoredSettings();
            var main = FindMainWindow();
            if (main != null) ApplyToWindow(main);
            LogService.Append($"美化包「{manifest.Name}」已启用，主色：{AccentColor ?? "(未覆盖，沿用自定义主题色)"}"
                              + (StyleSheetPath.Length > 0 ? $"，样式表：{StyleSheetPath}" : string.Empty));
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("读取美化包失败", ex);
            ClearCurrent();
            return false;
        }
    }

    /// <summary>卸载美化包并清空配置。</summary>
    public static void Deactivate()
    {
        SkinComponentService.Unload();
        SkinStyleApplier.Unload();
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.SkinFolder = string.Empty;
            AppConfig.Save(cfg);
        }
        catch (Exception ex)
        {
            LogService.Error("保存美化包设置失败", ex);
        }

        ClearCurrent();
        RefreshEffects();
    }

    /// <summary>切换当前主题：重算变量、调色板与样式，并刷新背景纹理 / 环境层 / 边饰。</summary>
    public static bool ApplyTheme(string id)
    {
        if (Current == null) return false;

        var text = (id ?? string.Empty).Trim();
        SkinTheme? theme = null;
        foreach (var item in _themes)
        {
            if (!item.Id.Equals(text, StringComparison.OrdinalIgnoreCase)) continue;
            theme = item;
            break;
        }
        if (theme == null) return false;

        CurrentThemeId = theme.Id;
        ApplyThemeVariables(theme);
        RebuildBackdropBrush();
        ThemeService.Reapply();
        SkinStyleApplier.Load(StyleSheet, _skinDir);

        var window = FindMainWindow();
        if (window != null) ApplyToWindow(window);
        return true;
    }

    /// <summary>按当前 <see cref="Effects"/> 重建主窗口的环境层与边饰。</summary>
    public static void RefreshEffects()
    {
        var window = FindMainWindow();
        if (window == null) return;
        UpdateSkinLayers(window);
    }

    /// <summary>把 <c>--xxx</c> 变量写入当前样式表变量并重新应用；key 不以 -- 开头时自动补。</summary>
    public static void SetVariableOverride(string key, string value)
    {
        if (Current == null) return;

        var name = (key ?? string.Empty).Trim();
        if (name.Length == 0) return;
        if (!name.StartsWith("--", StringComparison.Ordinal)) name = "--" + name.TrimStart('-');
        if (name.Length <= 2) return;

        var created = false;
        if (StyleSheet == null)
        {
            StyleSheet = new SkinStyleSheet();
            created = true;
        }

        _variableOverrides[name] = value ?? string.Empty;
        ApplyThemeVariables(CurrentTheme);
        if (created) SkinStyleApplier.Load(StyleSheet, _skinDir);
        else SkinStyleApplier.Reapply();
    }

    /// <summary>
    /// 供轻量脚本 / 交互组件写入设置项：应用后写入该皮肤的 settings.json，并刷新数据绑定。
    /// 未知键按 <c>theme</c> / <c>motion</c> / <c>particle</c> / <c>ornament</c> 关键字兜底。
    /// </summary>
    public static bool SetSettingValue(string key, string value)
    {
        if (Current == null) return false;

        var name = (key ?? string.Empty).Trim();
        if (name.Length == 0) return false;

        // theme 可能同时被 settings[] 声明为 select，先按主题处理，避免被当作普通设置项吞掉
        if (name.Equals("theme", StringComparison.OrdinalIgnoreCase))
        {
            if (!ApplyTheme(value)) return false;
            var themeState = SkinSettingsStore.Load(_skinDir);
            themeState.SetValue("theme", value);
            SkinSettingsStore.Save(_skinDir, themeState);
            NotifyBindingsChanged();
            return true;
        }

        var definition = SettingsDefinitions().FirstOrDefault(item => IsSettingKey(item.Key, name));
        if (definition != null) ApplySettingValue(definition, value);
        else if (name.Contains("motion", StringComparison.OrdinalIgnoreCase)) MotionEnabled = ParseToggle(value, true);
        else if (name.Contains("particle", StringComparison.OrdinalIgnoreCase))
        {
            var number = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
            var ambient = Effects.Ambient;
            if (ambient == null)
            {
                ambient = new SkinAmbientOptions();
                Effects.Ambient = ambient;
            }
            ambient.Particles = Math.Clamp(number, 0, SkinRules.MaxParticles);
            RefreshEffects();
        }
        else if (name.Contains("ornament", StringComparison.OrdinalIgnoreCase))
        {
            OrnamentEnabled = ParseToggle(value, true);
            RefreshEffects();
        }
        else if (name.StartsWith("--", StringComparison.Ordinal)) SetVariableOverride(name, value);
        else return false;

        var state = SkinSettingsStore.Load(_skinDir);
        state.SetValue(name, value);
        SkinSettingsStore.Save(_skinDir, state);
        NotifyBindingsChanged();
        return true;
    }

    /// <summary>通知所有数据绑定重新求值（变量、设置项或主题变化后调用）。</summary>
    public static void NotifyBindingsChanged() => SkinComponentService.RefreshBindings();

    /// <summary>恢复主题 / 皮肤的原始变量，清除设置页写入的变量覆盖。</summary>
    public static void ClearVariableOverrides()
    {
        if (Current == null) return;
        _variableOverrides.Clear();
        ApplyThemeVariables(CurrentTheme);
        SkinStyleApplier.Reapply();
    }

    /// <summary>设置 / 清除某类底图（<c>window</c> / <c>button</c> / <c>card</c>），并立即应用。</summary>
    public static void SetSurfaceImage(string kind, string? relativePath)
    {
        if (Current == null) return;

        var name = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (name is not ("window" or "button" or "card")) return;

        var text = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
        if (text.Length > 0 && !IsSafeSurfacePath(text))
        {
            LogService.Append($"底图路径不安全，已忽略：{text}");
            return;
        }

        switch (name)
        {
            case "window": WindowImageOverride = text; break;
            case "button": ButtonImageOverride = text; break;
            default: CardImageOverride = text; break;
        }
        ApplySurfaceImages();
    }

    /// <summary>按当前覆盖重建窗口背景、全部按钮底图与卡片底图资源。</summary>
    public static void ApplySurfaceImages()
    {
        if (Current == null)
        {
            ClearSurfaceImages();
            return;
        }
        if (_applyingSurfaces) return;

        _applyingSurfaces = true;
        try
        {
            _windowSurfaceBrush = BuildSurfaceBrush(WindowImageOverride, "窗口底图");
            var main = FindMainWindow();
            if (main != null) ApplyWindowBackdrop(main);

            if (ButtonImageOverride.Length > 0)
            {
                _buttonSurfaceBrush = BuildSurfaceBrush(ButtonImageOverride, "按钮底图");
                if (_buttonSurfaceBrush != null) ApplyButtonSurfaces();
                else RestoreButtonSurfaces();
            }
            else
            {
                _buttonSurfaceBrush = null;
                RestoreButtonSurfaces();
            }

            if (CardImageOverride.Length > 0) ApplyCardSurface();
            else RestoreCardBrush();
        }
        finally
        {
            _applyingSurfaces = false;
        }
    }

    /// <summary>撤掉三类底图，恢复窗口背景、按钮 Background 与 CardBrush 资源。</summary>
    public static void ClearSurfaceImages()
    {
        WindowImageOverride = string.Empty;
        ButtonImageOverride = string.Empty;
        CardImageOverride = string.Empty;
        _windowSurfaceBrush = null;
        _buttonSurfaceBrush = null;

        var main = FindMainWindow();
        if (main != null) ApplyWindowBackdrop(main);

        RestoreButtonSurfaces();
        RestoreCardBrush();
    }

    /// <summary>供 Loaded 钩子调用：仅当元素是 Button 且存在按钮底图时套用，幂等。</summary>
    public static void ApplyButtonSurface(FrameworkElement element)
    {
        if (_buttonSurfaceBrush == null || element is not Button button) return;

        try
        {
            if (_buttonOriginals.TryGetValue(button, out var original))
            {
                if (!ReferenceEquals(button.Background, _buttonSurfaceBrush)) button.Background = _buttonSurfaceBrush;
                return;
            }

            original = new ButtonSurfaceOriginal
            {
                Background = button.Background,
                HadLocalValue = !ReferenceEquals(button.ReadLocalValue(Control.BackgroundProperty),
                    DependencyProperty.UnsetValue),
            };
            _buttonOriginals.Add(button, original);
            _buttonApplied.Add(new WeakReference<Button>(button));
            button.Background = _buttonSurfaceBrush;
        }
        catch (Exception ex)
        {
            LogService.Error("应用按钮底图失败", ex);
        }
    }

    private static void ApplyButtonSurfaces()
    {
        var app = Application.Current;
        if (app == null || _buttonSurfaceBrush == null) return;

        foreach (Window window in app.Windows)
        {
            if (window.Content is not DependencyObject root) continue;
            WalkVisual(root, ApplyButtonSurface);
        }
    }

    private static void WalkVisual(DependencyObject node, Action<FrameworkElement> apply)
    {
        if (node is FrameworkElement element) apply(element);

        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
            return;
        }

        for (var i = 0; i < count; i++)
            WalkVisual(VisualTreeHelper.GetChild(node, i), apply);
    }

    private static void RestoreButtonSurfaces()
    {
        foreach (var reference in _buttonApplied)
        {
            if (!reference.TryGetTarget(out var button)) continue;
            if (!_buttonOriginals.TryGetValue(button, out var original)) continue;

            try
            {
                if (original.HadLocalValue) button.Background = original.Background;
                else button.ClearValue(Control.BackgroundProperty);
            }
            catch
            {
                // 元素已释放时忽略
            }
        }

        _buttonApplied.Clear();
        _buttonOriginals = new ConditionalWeakTable<Button, ButtonSurfaceOriginal>();
    }

    private static void ApplyCardSurface()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        var image = LoadImage(_skinDir, CardImageOverride);
        if (image == null)
        {
            LogService.Append("卡片底图加载失败，已跳过：" + CardImageOverride);
            RestoreCardBrush();
            return;
        }

        // 0.5 × 0.5 的相对视口形成平铺纹理；TileMode 让图案铺满卡片。
        var brush = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 0.5, 0.5),
        };
        brush.Freeze();

        if (!ReferenceEquals(resources["CardBrush"], _cardImageBrush))
            _cardOriginalBrush = resources["CardBrush"] as Brush ?? _cardOriginalBrush;

        _cardImageBrush = brush;
        resources["CardBrush"] = brush;
    }

    private static void RestoreCardBrush()
    {
        var resources = Application.Current?.Resources;
        if (resources != null && _cardImageBrush != null
            && ReferenceEquals(resources["CardBrush"], _cardImageBrush) && _cardOriginalBrush != null)
        {
            resources["CardBrush"] = _cardOriginalBrush;
        }

        _cardImageBrush = null;
        _cardOriginalBrush = null;
    }

    private static ImageBrush? BuildSurfaceBrush(string relative, string label)
    {
        var text = (relative ?? string.Empty).Trim();
        if (text.Length == 0 || !IsSafeSurfacePath(text)) return null;

        var image = LoadImage(_skinDir, text);
        if (image == null)
        {
            LogService.Append($"{label}加载失败，已跳过：{text}");
            return null;
        }

        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }

    private static bool IsSafeSurfacePath(string path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        if (Path.IsPathRooted(text)) return false;
        return SkinRules.IsSafeRelativePath(text);
    }

    /// <summary>读取该皮肤 settings.json 并应用主题、动效、粒子、边饰与变量覆盖；无皮肤时安全跳过。</summary>
    public static void ApplyStoredSettings()
    {
        if (Current == null) return;

        var state = SkinSettingsStore.Load(_skinDir);

        var themeId = (state.Theme ?? string.Empty).Trim();
        if (themeId.Length == 0)
        {
            foreach (var definition in SettingsDefinitions())
            {
                if (!IsSettingKey(definition.Key, "theme")) continue;
                themeId = state.ValueOf(definition).Trim();
                break;
            }
        }
        if (themeId.Length > 0) ApplyTheme(themeId);

        foreach (var definition in SettingsDefinitions())
        {
            if (IsSettingKey(definition.Key, "theme")) continue;
            ApplySettingValue(definition, state.ValueOf(definition));
        }

        SetSurfaceImage("window", state.WindowImage);
        SetSurfaceImage("button", state.ButtonImage);
        SetSurfaceImage("card", state.CardImage);
        ApplySurfaceImages();

        RefreshEffects();
    }

    private static IEnumerable<SkinSettingDefinition> SettingsDefinitions()
    {
        var settings = Current?.Settings;
        if (settings == null) yield break;

        var count = 0;
        foreach (var definition in settings)
        {
            if (definition == null) continue;
            if (count++ >= SkinRules.MaxSettings) break;
            yield return definition;
        }
    }

    private static bool IsSettingKey(string? key, string expected) =>
        (key ?? string.Empty).Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static void ApplySettingValue(SkinSettingDefinition definition, string value)
    {
        var key = (definition.Key ?? string.Empty).Trim();
        if (key.Length == 0) return;

        if (key.Contains("motion", StringComparison.OrdinalIgnoreCase))
        {
            MotionEnabled = ParseToggle(value, true);
            return;
        }

        if (key.Contains("particle", StringComparison.OrdinalIgnoreCase))
        {
            var text = (value ?? string.Empty).Trim();
            var number = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int)Math.Round(definition.GetDefaultDouble(0));
            var ambient = Effects.Ambient;
            if (ambient == null)
            {
                ambient = new SkinAmbientOptions();
                Effects.Ambient = ambient;
            }
            ambient.Particles = Math.Clamp(number, 0, SkinRules.MaxParticles);
            RefreshEffects();
            return;
        }

        if (key.Contains("ornament", StringComparison.OrdinalIgnoreCase))
        {
            OrnamentEnabled = ParseToggle(value, true);
            RefreshEffects();
            return;
        }

        var target = (definition.Target ?? string.Empty).Trim();
        if (target.StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value))
            SetVariableOverride(target, value);
    }

    private static bool ParseToggle(string? value, bool fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return fallback;
        if (bool.TryParse(text, out var parsed)) return parsed;
        return text.ToLowerInvariant() switch
        {
            "1" or "on" or "yes" => true,
            "0" or "off" or "no" => false,
            _ => fallback,
        };
    }

    /// <summary>把当前美化包的背景与 CSS 样式应用到主窗口；没有配置时隐藏背景层。</summary>
    public static void ApplyToWindow(Window window)
    {
        ApplyWindowBackdrop(window);
        SkinStyleApplier.ApplyToWindow(window);
        UpdateSkinLayers(window);
        SkinComponentService.Apply(window);
        ApplySurfaceImages();
    }

    /// <summary>窗口背景：底图覆盖优先，其次主题 / 美化包背景画刷。</summary>
    private static void ApplyWindowBackdrop(Window window)
    {
        if (window.FindName("SkinBackdrop") is not Border backdrop) return;

        var brush = _windowSurfaceBrush ?? WindowBackdropBrush;
        backdrop.Background = brush;
        backdrop.Visibility = brush == null ? Visibility.Collapsed : Visibility.Visible;
        // 背景图在环境层之下、内容之上：环境层 ZIndex = -1
        Panel.SetZIndex(backdrop, -2);
    }

    /// <summary>弹窗背景：有美化包弹窗背景时覆盖，否则沿用主题的 BgBrush。</summary>
    public static void ApplyDialogBackground(Window window)
    {
        if (DialogBackgroundBrush == null) return;
        window.Background = DialogBackgroundBrush;
    }

    private static void ClearCurrent()
    {
        SkinComponentService.Unload();
        SkinScriptHost.Clear();

        Current = null;
        CurrentFolder = string.Empty;
        StyleSheet = null;
        StyleSheetPath = string.Empty;
        WindowBackdropBrush = null;
        DialogBackgroundBrush = null;
        ClearSurfaceImages();
        _themes = new List<SkinTheme>();
        CurrentThemeId = string.Empty;
        Effects = CreateDisabledEffects();
        OrnamentEnabled = true;
        _baseVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _variableOverrides.Clear();
        _skinDir = string.Empty;
        _ambientBackSignature = string.Empty;
        _ambientFrontSignature = string.Empty;
        _ornamentSignature = string.Empty;
    }

    /// <summary>过滤主题列表并解析默认主题（defaultTheme → 第一个）。</summary>
    private static void PrepareThemes(SkinManifest manifest)
    {
        _themes = new List<SkinTheme>();
        foreach (var theme in manifest.Themes ?? new List<SkinTheme>())
        {
            if (theme == null) continue;
            var id = (theme.Id ?? string.Empty).Trim();
            if (id.Length == 0) continue;
            if (_themes.Any(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            if (_themes.Count >= SkinRules.MaxThemes) break;
            theme.Id = id;
            _themes.Add(theme);
        }

        CurrentThemeId = string.Empty;
        if (_themes.Count == 0) return;

        var wanted = (manifest.DefaultTheme ?? string.Empty).Trim();
        SkinTheme? selected = null;
        if (wanted.Length > 0 && !wanted.Equals("auto", StringComparison.OrdinalIgnoreCase))
            selected = _themes.FirstOrDefault(item => item.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        selected ??= _themes[0];
        CurrentThemeId = selected.Id;
    }

    /// <summary>把主题变量叠加到样式表变量（在包原始变量之上），最后写入设置页的变量覆盖。</summary>
    private static void ApplyThemeVariables(SkinTheme? theme)
    {
        if (StyleSheet == null)
        {
            if (theme?.Variables is not { Count: > 0 } && _variableOverrides.Count == 0) return;
            StyleSheet = new SkinStyleSheet();
        }

        var variables = StyleSheet.Variables;
        variables.Clear();
        foreach (var pair in _baseVariables) variables[pair.Key] = pair.Value;
        if (theme?.Variables != null)
        {
            foreach (var pair in theme.Variables)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                variables[pair.Key.Trim()] = pair.Value;
            }
        }
        foreach (var pair in _variableOverrides) variables[pair.Key] = pair.Value;
    }

    /// <summary>主窗口背景：显式背景图优先，其次主题 texture，否则只剩底色。</summary>
    private static void RebuildBackdropBrush()
    {
        if (Current == null)
        {
            WindowBackdropBrush = null;
            return;
        }

        var image = (Current.WindowBackgroundImage ?? string.Empty).Trim();
        if (image.Length == 0)
        {
            var texture = (CurrentTheme?.Texture ?? string.Empty).Trim();
            if (texture.Length > 0)
            {
                if (SkinRules.IsSafeRelativePath(texture)) image = texture;
                else LogService.Append("主题背景纹理路径不安全，已忽略：" + texture);
            }
        }

        WindowBackdropBrush = BuildBrush(_skinDir, image, Current.WindowBackgroundColor, 0.85);
    }

    /// <summary>在主窗口根 Grid 中插入 / 更新 / 移除环境层与主题边饰。</summary>
    private static void UpdateSkinLayers(Window window)
    {
        if (!IsMainWindow(window)) return;
        if (window.Content is not Grid root)
        {
            LogService.Append("主窗口根容器不是 Grid，已跳过美化包环境层与边饰。");
            return;
        }

        UpdateAmbientLayer(root);
        UpdateOrnamentLayer(root);
    }

    private static void UpdateAmbientLayer(Grid root)
    {
        var back = root.Children.OfType<SkinAmbientLayer>()
            .FirstOrDefault(layer => string.Equals(layer.Tag as string, AmbientBackTag, StringComparison.Ordinal));
        var front = root.Children.OfType<SkinAmbientLayer>()
            .FirstOrDefault(layer => string.Equals(layer.Tag as string, AmbientFrontTag, StringComparison.Ordinal));
        var options = Effects?.Ambient;
        var wanted = Current != null && options != null
            && (options.Sky || options.Veil || options.Particles > 0);

        if (!wanted)
        {
            if (back != null)
            {
                back.Stop();
                root.Children.Remove(back);
            }
            if (front != null)
            {
                front.Stop();
                root.Children.Remove(front);
            }
            _ambientBackSignature = string.Empty;
            _ambientFrontSignature = string.Empty;
            return;
        }

        var motion = UiAnimations.Active;
        var signature = $"{CurrentFolder}|{CurrentThemeId}|{options!.Particles}|{options.Veil}|{options.Sky}|"
                        + $"{options.Opacity:0.###}|{motion}";

        if (back == null)
        {
            back = new SkinAmbientLayer { Tag = AmbientBackTag, BackgroundOnly = true };
            root.Children.Insert(0, back);
            AttachLayerToRoot(root, back);
            Panel.SetZIndex(back, -1);
            _ambientBackSignature = string.Empty;
        }

        if (front == null)
        {
            // 粒子单独放在内容上层，避免被不透明的面板完全遮住
            front = new SkinAmbientLayer { Tag = AmbientFrontTag, ParticlesOnly = true };
            root.Children.Add(front);
            AttachLayerToRoot(root, front);
            Panel.SetZIndex(front, 900);
            _ambientFrontSignature = string.Empty;
        }

        var backSignature = signature + "|bg";
        if (!backSignature.Equals(_ambientBackSignature, StringComparison.Ordinal))
        {
            back.Rebuild(options, CurrentTheme, motion);
            _ambientBackSignature = backSignature;
        }

        var frontSignature = signature + "|front";
        if (!frontSignature.Equals(_ambientFrontSignature, StringComparison.Ordinal))
        {
            front.Rebuild(options, CurrentTheme, motion);
            _ambientFrontSignature = frontSignature;
        }

        LogService.Append($"环境层：粒子={options.Particles}，天空={options.Sky}，光带={options.Veil}，"
                          + $"动效={motion}，前层子元素={front.Children.Count}，前层可见={front.Visibility}，"
                          + $"前层Z={Panel.GetZIndex(front)}");
    }

    /// <summary>诊断用：采样前层第一个粒子的位置与透明度。</summary>
    public static (double X, double Opacity) SampleAmbientParticle(Window window)
    {
        if (window.Content is Grid root)
        {
            foreach (var child in root.Children)
            {
                if (child is SkinAmbientLayer layer && layer.ParticlesOnly)
                    return (layer.SampleParticleX, layer.SampleParticleOpacity);
            }
        }
        return (0, 0);
    }

    private static void AttachLayerToRoot(Grid root, FrameworkElement layer)
    {
        Grid.SetColumnSpan(layer, Math.Max(1, root.ColumnDefinitions.Count));
        Grid.SetRowSpan(layer, Math.Max(1, root.RowDefinitions.Count));
    }

    /// <summary>诊断用：输出当前皮肤环境层状态到 launcher.log。</summary>
    public static void DumpAmbientDiagnostics(Window window)
    {
        var ambient = Effects?.Ambient;
        LogService.Append($"DIAG 皮肤={Current?.Name ?? "(none)"}，主题={CurrentThemeId}，"
                          + $"粒子={ambient?.Particles}，天空={ambient?.Sky}，光带={ambient?.Veil}，"
                          + $"动效={MotionEnabled}，UiAnimations={UiAnimations.Active}");
        if (window.Content is not Grid root)
        {
            LogService.Append("DIAG 主窗口根容器不是 Grid");
            return;
        }

        var count = 0;
        foreach (var child in root.Children)
        {
            if (child is not SkinAmbientLayer layer) continue;
            count++;
            LogService.Append($"DIAG 环境层 tag={layer.Tag}，可见={layer.Visibility}，Z={Panel.GetZIndex(layer)}，"
                              + $"子元素={layer.Children.Count}，粒子={layer.ParticleCount}，"
                              + $"尺寸={layer.ActualWidth:0}x{layer.ActualHeight:0}，透明度={layer.Opacity:0.##}");
        }
        LogService.Append($"DIAG 环境层数量={count}，根子元素={root.Children.Count}");
    }

    private static void UpdateOrnamentLayer(Grid root)
    {
        var ornament = root.Children.OfType<SkinOrnamentBorder>().FirstOrDefault();
        var theme = CurrentTheme;
        var relative = (theme?.Ornament ?? string.Empty).Trim();
        var wanted = Current != null && OrnamentEnabled && theme != null && relative.Length > 0;

        if (!wanted)
        {
            if (ornament != null)
            {
                ornament.Clear();
                root.Children.Remove(ornament);
            }
            _ornamentSignature = string.Empty;
            return;
        }

        if (!SkinRules.IsSafeRelativePath(relative))
        {
            LogService.Append("主题边饰路径不安全，已忽略：" + relative);
            if (ornament != null)
            {
                ornament.Clear();
                root.Children.Remove(ornament);
            }
            _ornamentSignature = string.Empty;
            return;
        }

        ornament ??= InsertLayer(root, new SkinOrnamentBorder());
        var slice = theme!.Slice > 0 ? theme.Slice : 24;
        var signature = $"{theme.Id}|{relative}|{slice}";
        if (signature.Equals(_ornamentSignature, StringComparison.Ordinal)) return;

        var file = Path.Combine(_skinDir, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!ornament.Apply(file, slice))
        {
            LogService.Append("主题边饰加载失败，已跳过：" + relative);
            ornament.Clear();
            root.Children.Remove(ornament);
            _ornamentSignature = string.Empty;
            return;
        }
        _ornamentSignature = signature;
    }

    private static T InsertLayer<T>(Grid root, T layer) where T : FrameworkElement
    {
        if (layer is SkinAmbientLayer) root.Children.Insert(0, layer);
        else root.Children.Add(layer);

        Grid.SetColumnSpan(layer, Math.Max(1, root.ColumnDefinitions.Count));
        Grid.SetRowSpan(layer, Math.Max(1, root.RowDefinitions.Count));
        return layer;
    }

    private static bool IsMainWindow(Window window)
        => window is MainWindow || ReferenceEquals(window, Application.Current?.MainWindow);

    private static Window? FindMainWindow()
    {
        var app = Application.Current;
        if (app == null) return null;
        if (app.MainWindow is MainWindow main) return main;
        foreach (Window window in app.Windows)
            if (window is MainWindow) return window;
        return app.MainWindow;
    }

    private static SkinEffectOptions CreateDisabledEffects() => new()
    {
        Ambient = new SkinAmbientOptions { Particles = 0, Veil = false, Sky = false },
    };

    /// <summary>读取包内样式表与清单变量；没有任何样式内容时返回 null。</summary>
    private static void LoadStyleSheet(string dir, SkinManifest manifest)
    {
        StyleSheet = null;
        StyleSheetPath = string.Empty;

        var relative = (manifest.StyleSheet ?? string.Empty).Trim();
        if (relative.Length == 0)
        {
            foreach (var name in new[] { "skin.css", "style.css", "styles.css" })
            {
                if (!File.Exists(Path.Combine(dir, name))) continue;
                relative = name;
                break;
            }
        }

        SkinStyleSheet? sheet = null;
        if (relative.Length > 0)
        {
            if (!SkinRules.IsSafeRelativePath(relative))
            {
                LogService.Append("美化包样式表路径不安全，已忽略：" + relative);
            }
            else
            {
                var file = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file))
                {
                    LogService.Append("美化包样式表不存在：" + relative);
                }
                else
                {
                    try
                    {
                        sheet = SkinStyleSheet.Parse(File.ReadAllText(file));
                        StyleSheetPath = relative;
                        if (sheet.Warnings.Count > 0)
                            LogService.Append("美化包样式表告警：" + string.Join("；", sheet.Warnings.Take(6)));
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("读取美化包样式表失败", ex);
                    }
                }
            }
        }

        // skin.json 的 variables 与 CSS :root 变量等价，作为额外补充
        if (manifest.Variables is { Count: > 0 })
        {
            sheet ??= new SkinStyleSheet();
            foreach (var pair in manifest.Variables)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                sheet.Variables[pair.Key.Trim()] = pair.Value;
            }
        }

        if (sheet == null) return;
        StyleSheet = sheet;
        if (StyleSheetPath.Length > 0)
            LogService.Append($"美化包样式表已加载：{StyleSheetPath}（{sheet.Rules.Count} 条规则）");
    }

    /// <summary>把样式表变量里的调色板键写进当前调色板（优先级高于主色派生结果）。</summary>
    public static void ApplyPaletteOverrides(IDictionary<string, string> palette, bool dark)
    {
        var variables = StyleSheet?.VariablesFor(dark);
        if (variables == null || variables.Count == 0) return;

        foreach (var pair in variables)
        {
            if (!SkinStyleApplier.TryMapPaletteKey(pair.Key, out var key)) continue;
            if (!palette.ContainsKey(key)) continue;
            if (SkinColors.TryParse(pair.Value, out var color)) palette[key] = SkinColors.ToArgbHex(color);
        }
    }

    /// <summary>CSS 变量里覆盖了页面底色且偏暗时，同样要求切到深色配色。</summary>
    private static bool IsPaletteBgDark()
    {
        var variables = StyleSheet?.Variables;
        if (variables == null) return false;

        foreach (var pair in variables)
            if (SkinStyleApplier.TryMapPaletteKey(pair.Key, out var key) && key == "BgBrush")
                return IsDarkColor(pair.Value);
        return false;
    }

    /// <summary>按感知亮度判断底色是否偏暗（0.299R + 0.587G + 0.114B）。</summary>
    private static bool IsDarkColor(string? hex)
    {
        if (!SkinColors.TryParse(hex, out var color)) return false;
        if (color.A == 0) return false;
        var luma = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luma < 0.5;
    }

    private static Brush? BuildBrush(string dir, string? imageRelative, string? colorHex, double imageOpacity)
    {
        var color = ParseBrush(colorHex);
        var image = LoadImage(dir, imageRelative);
        if (image == null) return color;

        var brush = new ImageBrush(image)
        {
            Stretch = Stretch.UniformToFill,
            // 背景图压暗一点，保证前景文字在深浅主题下都可读
            Opacity = imageOpacity,
        };
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush? ParseBrush(string? hex)
    {
        var text = (hex ?? string.Empty).Trim();
        if (!RgbColor.TryParse(text, out var rgb)) return null;
        var brush = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
        brush.Freeze();
        return brush;
    }

    private static ImageSource? LoadImage(string dir, string? relative)
    {
        var text = (relative ?? string.Empty).Trim();
        if (text.Length == 0 || !SkinRules.IsSafeRelativePath(text)) return null;

        try
        {
            var file = Path.Combine(dir, text.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) return null;
            if (SkinSvgRenderer.IsSvg(file))
                return SkinSvgRenderer.TryLoadImageSource(file, 512, out var svg) ? svg : null;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            LogService.Error("读取美化包图片失败", ex);
            return null;
        }
    }

    private static string NormalizeEntry(string name) => (name ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static string Sanitize(params string?[] candidates)
    {
        var raw = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? "skin";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? "skin" : result;
    }
}
