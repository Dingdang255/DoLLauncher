using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoLLauncher.Core.Skin;
using DoLLauncher.Core.Theme;
using DoLLauncher.Services;
using DoLLauncher.Maui.Settings;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// Android 端本地 UI 美化包：SAF 选 zip → 解压到 skins/&lt;id&gt; → 校验 skin.json → 启用。
/// 支持主色、页面背景色 / 背景图、variables，以及 CSS 子集（skin.css / android.styleSheet）。
/// 启用记录写在 skins/active.json（刻意不复用 General.SkinFolder，避免与 WPF 端配置互相干扰），
/// 启动时自动恢复。
/// </summary>
public static class AndroidSkinStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SkinsRoot => Path.Combine(Paths.Root, "skins");
    private static string ActiveFile => Path.Combine(SkinsRoot, "active.json");

    /// <summary>当前生效的美化包清单；未启用时为 null。</summary>
    public static SkinManifest? Current { get; private set; }

    /// <summary>当前生效的美化包解压目录名。</summary>
    public static string CurrentFolder { get; private set; } = string.Empty;

    /// <summary>美化包 variables（已合并 android.variables / 顶层 variables / CSS :root）。</summary>
    public static IReadOnlyDictionary<string, string> Variables { get; private set; } =
        new Dictionary<string, string>();

    /// <summary>当前解析出的 CSS 样式表；没有样式表时为 null。</summary>
    private static SkinStyleSheet? _sheet;

    /// <summary>CSS 规则条数（Android 实际生效条数）。</summary>
    public static int CssRuleCount => _sheet?.Rules.Count ?? 0;

    /// <summary>CSS 解析与应用阶段的告警（已去重，最多 100 条）。</summary>
    public static IReadOnlyList<string> CssWarnings
    {
        get
        {
            var list = new List<string>();
            if (_sheet != null) list.AddRange(_sheet.Warnings);
            foreach (var item in MauiSkinStyler.LoadWarnings)
                if (!list.Contains(item)) list.Add(item);
            return list;
        }
    }

    /// <summary>美化包主色；为空表示沿用用户主题色。</summary>
    public static string? AccentColor
    {
        get
        {
            var explicitColor = (Current?.AccentColor ?? string.Empty).Trim();
            if (RgbColor.TryParse(explicitColor, out _)) return explicitColor;

            foreach (var pair in Variables)
            {
                if (!pair.Key.TrimStart('-').Equals("AccentBrush", StringComparison.OrdinalIgnoreCase) &&
                    !pair.Key.TrimStart('-').Equals("AccentColor", StringComparison.OrdinalIgnoreCase)) continue;
                if (RgbColor.TryParse(pair.Value, out _)) return pair.Value.Trim();
            }
            return null;
        }
    }

    /// <summary>美化包背景过深时要求使用深色配色，避免深底 + 深色文字看不清。</summary>
    public static bool RequiresDarkPalette =>
        IsDarkColor(Current?.WindowBackgroundColor) || IsDarkColor(Current?.DialogBackgroundColor);

    /// <summary>设置页展示的状态说明。</summary>
    public static string StatusText
    {
        get
        {
            var skin = Current;
            if (skin == null) return "当前未启用美化包。";
            var author = string.IsNullOrWhiteSpace(skin.Author) ? string.Empty : $" · {skin.Author}";
            var version = string.IsNullOrWhiteSpace(skin.Version) ? string.Empty : $" v{skin.Version}";
            var css = CssRuleCount > 0 ? $" · CSS {CssRuleCount} 条规则" : string.Empty;
            var warnings = CssWarnings.Count;
            var warn = warnings > 0 ? $" · {warnings} 条警告" : string.Empty;
            return $"已启用：{skin.Name}{version}{author}{css}{warn}";
        }
    }

    /// <summary>启动时按 active.json 恢复上次启用的美化包。</summary>
    public static void Initialize(Application app, ResourceDictionary resources)
    {
        try
        {
            ClearCurrent();
            var active = ReadActive();
            if (active == null)
            {
                ApplyCore(app, resources);
                return;
            }

            if (!Activate(active.Folder))
            {
                LogService.Append($"美化包 {active.Folder} 不可用，已回退默认外观。");
                return;
            }

            ApplyCore(app, resources);
        }
        catch (Exception ex)
        {
            LogService.Error("初始化美化包失败", ex);
            ClearCurrent();
        }
    }

    /// <summary>按当前状态重新应用主色、深浅色与页面背景（设置页导入 / 卸载后调用）。</summary>
    public static void Refresh() => ApplyCore(Application.Current, Application.Current?.Resources);

    /// <summary>从 zip 安装并立即写入启用记录（不负责应用界面，调用方随后 Refresh）。</summary>
    public static (bool Ok, string Message) ImportFromZip(string zipPath, Action<string>? log = null)
    {
        var temp = Path.Combine(SkinsRoot, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!File.Exists(zipPath)) return (false, "美化包文件不存在。");
            Directory.CreateDirectory(SkinsRoot);
            Directory.CreateDirectory(temp);

            using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                var manifestEntry = zip.Entries.FirstOrDefault(e =>
                    NormalizeEntry(e.FullName).Equals("skin.json", StringComparison.OrdinalIgnoreCase));
                if (manifestEntry == null)
                {
                    LogService.Append("美化包不可用：根目录缺少 skin.json。");
                    return (false, "美化包根目录缺少 skin.json。");
                }

                foreach (var entry in zip.Entries)
                {
                    var name = NormalizeEntry(entry.FullName);
                    if (name.Length == 0 || name.EndsWith('/')) continue;
                    if (!SkinRules.IsSafeRelativePath(name)) continue;

                    var target = Path.GetFullPath(Path.Combine(temp, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(Path.GetFullPath(temp) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    var parent = Path.GetDirectoryName(target);
                    if (parent != null) Directory.CreateDirectory(parent);
                    using (var src = entry.Open())
                    using (var dst = File.Create(target))
                        src.CopyTo(dst);
                }
            }

            var manifestPath = Path.Combine(temp, "skin.json");
            if (!File.Exists(manifestPath)) return (false, "美化包根目录缺少 skin.json。");

            var manifest = JsonSerializer.Deserialize<SkinManifest>(File.ReadAllText(manifestPath), JsonOpts);
            var (ok, reason) = SkinRules.Validate(manifest);
            if (!ok)
            {
                LogService.Append("美化包校验失败：" + reason);
                log?.Invoke("美化包校验失败：" + reason);
                return (false, reason);
            }

            var image = (manifest!.WindowBackgroundImage ?? string.Empty).Trim();
            if (image.Length > 0)
            {
                var file = Path.Combine(temp, image.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) return (false, "页面背景图不存在：" + image);
            }

            var folder = Sanitize(manifest.Id, manifest.Name, Path.GetFileNameWithoutExtension(zipPath));
            var dest = Path.Combine(SkinsRoot, folder);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.Move(temp, dest);

            if (!Activate(folder)) return (false, "美化包内容读取失败，请检查 skin.json。");

            WriteActive(new ActiveSkin { Id = (manifest.Id ?? string.Empty).Trim(), Folder = folder });
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

    /// <summary>卸载美化包：清除启用记录、页面背景与主色覆盖，并恢复用户原主题设置。</summary>
    public static void Deactivate()
    {
        try
        {
            if (File.Exists(ActiveFile)) File.Delete(ActiveFile);
        }
        catch (Exception ex)
        {
            LogService.Error("删除美化包启用记录失败", ex);
        }

        ClearCurrent();
        RestorePageBackground(Application.Current?.Resources);
        MauiSkinComponentService.Unload();
        AndroidSkinScriptHost.Clear();
        MauiSkinAnimationRunner.StopAll();
        MauiSkinStyler.Unload();

        // 恢复用户在设置里选择的主题与主题色
        App.ApplyTheme(AppConfig.Load().General.ThemeMode);
        LogService.Append("设置：已卸载 UI 美化包");
    }

    // ---------- 内部实现 ----------

    private static void ApplyCore(Application? app, ResourceDictionary? resources)
    {
        resources ??= app?.Resources;
        if (resources == null) return;

        if (Current == null)
        {
            MauiSkinComponentService.Unload();
            AndroidSkinScriptHost.Clear();
            MauiSkinAnimationRunner.StopAll();
            MauiSkinStyler.Unload();
            RestorePageBackground(resources);
            return;
        }

        // 美化包指定深色背景时强制深色，避免深底 + 深色文字看不清
        if (RequiresDarkPalette && app != null && app.UserAppTheme != AppTheme.Dark)
            app.UserAppTheme = AppTheme.Dark;

        // 变量优先级：android.variables → 顶层 variables → CSS :root（按当前深浅色合并）
        var dark = ThemeAccent.IsDark;
        Variables = SkinPalette.Merge(Current, _sheet, dark);

        var general = AppConfig.Load().General;
        var accent = AccentColor;
        ThemeAccent.Apply(resources, accent ?? general.ThemeAccentColor,
            accent == null ? general.ThemeAccentSaturation : 100);
        ApplyPaletteOverrides(resources);
        ApplyPageBackground(resources);

        var folder = Path.Combine(SkinsRoot, CurrentFolder);
        MauiSkinStyler.Load(_sheet, folder, Variables, ThemeAccent.IsDark);

        // 声明式组件：给当前打开的页面补插一次，并重新求值数据绑定
        foreach (var page in OpenPages()) MauiSkinComponentService.Apply(page);
        MauiSkinComponentService.RefreshBindings();

        // 主题 / 资源重建后 UI 密度可能退回系统值，这里静默补回缩放
        DoLLauncher.Maui.MainApplication.ReapplyUiScale();
    }

    /// <summary>把变量里声明的调色板颜色覆盖到 MAUI 资源（AccentBrush → AccentColor 等）。</summary>
    private static void ApplyPaletteOverrides(ResourceDictionary resources)
    {
        var overrides = SkinPalette.ResolveColorOverrides(Variables);
        if (overrides.Count == 0) return;

        foreach (var pair in overrides)
        {
            var key = MapPaletteKey(pair.Key);
            if (key == null) continue;

            Color color;
            try
            {
                color = Color.FromArgb(pair.Value);
            }
            catch
            {
                continue;
            }

            resources[key] = color;
            // Colors.xaml 里的键在合并字典内，只有那里也改掉才会触发 DynamicResource 重新取值
            foreach (var merged in resources.MergedDictionaries)
                if (merged.TryGetValue(key, out _)) merged[key] = color;
        }
    }

    private static string? MapPaletteKey(string coreKey) => coreKey switch
    {
        "AccentBrush" => "AccentColor",
        "AccentHoverBrush" => "AccentHoverColor",
        "AccentSoftBrush" => "AccentSoftColor",
        "AccentPaleBrush" => "AccentPaleColor",
        "AccentStrongBrush" => "AccentStrongColor",
        "AccentOnBrush" => "AccentOnColor",
        "TabChipActiveBgBrush" => "AccentColor",
        "TabChipActiveFgBrush" => "AccentOnColor",
        _ => null,
    };

    private static bool Activate(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) ||
                folder.IndexOfAny(new[] { '/', '\\' }) >= 0 || folder.Contains(".."))
            {
                ClearCurrent();
                return false;
            }

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

            // 切换美化包：先清掉上一包的脚本变量、插入组件与动画
            MauiSkinComponentService.Unload();
            AndroidSkinScriptHost.Clear();
            MauiSkinAnimationRunner.StopAll();

            Current = manifest;
            CurrentFolder = folder;
            LoadSheet(dir, manifest!);
            Variables = SkinPalette.Merge(manifest, _sheet, ThemeAccent.IsDark);
            LogService.Append($"美化包「{manifest!.Name}」已启用，主色：{AccentColor ?? "(未覆盖，沿用自定义主题色)"}" +
                              (CssRuleCount > 0 ? $"，CSS {CssRuleCount} 条规则" : "，无 CSS"));
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("读取美化包失败", ex);
            ClearCurrent();
            return false;
        }
    }

    private static void ClearCurrent()
    {
        Current = null;
        CurrentFolder = string.Empty;
        Variables = new Dictionary<string, string>();
        _sheet = null;
        _sheetCacheKey = string.Empty;
    }

    // ---------- CSS 样式表 ----------

    private const int MaxSheetBytes = 256 * 1024;
    private const int MaxRules = 500;

    private static string _sheetCacheKey = string.Empty;

    /// <summary>解析美化包样式表：android.styleSheet 优先，其次顶层 styleSheet，最后自动查找。</summary>
    private static void LoadSheet(string skinDir, SkinManifest manifest)
    {
        _sheet = null;
        _sheetCacheKey = string.Empty;

        var relative = manifest.PreferredStyleSheet;
        if (relative.Length == 0)
        {
            foreach (var name in new[] { "skin.css", "style.css", "styles.css" })
            {
                if (!File.Exists(Path.Combine(skinDir, name))) continue;
                relative = name;
                break;
            }
        }

        if (relative.Length == 0) return;
        if (!SkinRules.IsSafeRelativePath(relative))
        {
            LogService.Append("美化包样式表路径不安全，已忽略：" + relative);
            return;
        }

        var file = Path.Combine(skinDir, relative.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file))
        {
            LogService.Append("美化包样式表不存在，已忽略：" + relative);
            return;
        }

        try
        {
            var info = new FileInfo(file);
            var cacheKey = file + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
            if (cacheKey == _sheetCacheKey && _sheet != null) return;

            var text = File.ReadAllText(file);
            var warnings = new List<string>();
            if (text.Length > MaxSheetBytes)
            {
                text = text[..MaxSheetBytes];
                warnings.Add($"样式表超过 {MaxSheetBytes / 1024}KB，已只加载前面部分。");
            }

            var sheet = SkinStyleSheet.Parse(text);
            if (sheet.Rules.Count > MaxRules)
            {
                sheet.Rules.RemoveRange(MaxRules, sheet.Rules.Count - MaxRules);
                sheet.Warnings.Add($"规则数超过上限 {MaxRules}，已只加载前面的规则。");
            }
            sheet.Warnings.InsertRange(0, warnings);
            foreach (var warning in sheet.Warnings) LogService.Append("[美化包] " + warning);

            _sheet = sheet;
            _sheetCacheKey = cacheKey;
        }
        catch (Exception ex)
        {
            LogService.Error("解析美化包样式表失败", ex);
        }
    }

    // ---------- 页面背景 ----------

    private static Style? _originalPageStyle;
    private static bool _pageStyleCaptured;

    /// <summary>隐式样式在资源字典里的键就是 TargetType 的 FullName。</summary>
    private static string PageStyleKey => typeof(Page).FullName!;

    private static void ApplyPageBackground(ResourceDictionary resources)
    {
        var color = (Current?.WindowBackgroundColor ?? string.Empty).Trim();
        var imageRelative = (Current?.WindowBackgroundImage ?? string.Empty).Trim();
        var hasColor = RgbColor.TryParse(color, out var rgb);
        var imagePath = imageRelative.Length > 0
            ? Path.Combine(SkinsRoot, CurrentFolder,
                imageRelative.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar))
            : null;
        var hasImage = imagePath != null && File.Exists(imagePath);

        if (!hasColor && !hasImage)
        {
            RestorePageBackground(resources);
            return;
        }

        if (!_pageStyleCaptured)
        {
            if (resources.TryGetValue(PageStyleKey, out var existing)) _originalPageStyle = existing as Style;
            _pageStyleCaptured = true;
        }

        var style = new Style(typeof(Page)) { ApplyToDerivedTypes = true };
        if (hasColor)
            style.Setters.Add(new Setter
            {
                Property = VisualElement.BackgroundColorProperty,
                Value = Color.FromArgb($"#FF{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}"),
            });
        if (hasImage)
            style.Setters.Add(new Setter
            {
                Property = Page.BackgroundImageSourceProperty,
                Value = ImageSource.FromFile(imagePath),
            });

        resources[PageStyleKey] = style;

        foreach (var page in OpenPages())
        {
            if (hasColor) page.BackgroundColor = Color.FromArgb($"#FF{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}");
            if (hasImage) page.BackgroundImageSource = ImageSource.FromFile(imagePath);
        }
    }

    private static void RestorePageBackground(ResourceDictionary? resources)
    {
        if (resources != null && _pageStyleCaptured)
        {
            if (_originalPageStyle != null) resources[PageStyleKey] = _originalPageStyle;
            else resources.Remove(PageStyleKey);
        }
        _originalPageStyle = null;
        _pageStyleCaptured = false;

        foreach (var page in OpenPages())
        {
            page.ClearValue(VisualElement.BackgroundColorProperty);
            page.ClearValue(Page.BackgroundImageSourceProperty);
        }
    }

    private static List<Page> OpenPages()
    {
        var result = new List<Page>();
        void Visit(Page? page)
        {
            if (page == null || result.Contains(page)) return;
            result.Add(page);

            switch (page)
            {
                case NavigationPage nav:
                    foreach (var child in nav.Navigation.NavigationStack) Visit(child);
                    break;
                case TabbedPage tabs:
                    foreach (var child in tabs.Children) Visit(child);
                    break;
                case FlyoutPage flyout:
                    Visit(flyout.Detail);
                    break;
            }

            foreach (var modal in page.Navigation.ModalStack) Visit(modal);
        }

        var root = Application.Current?.Windows.FirstOrDefault()?.Page;
        Visit(root);
        return result;
    }

    // ---------- active.json ----------

    private sealed class ActiveSkin
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("folder")] public string Folder { get; set; } = string.Empty;
    }

    private static ActiveSkin? ReadActive()
    {
        try
        {
            if (!File.Exists(ActiveFile)) return null;
            var active = JsonSerializer.Deserialize<ActiveSkin>(File.ReadAllText(ActiveFile), JsonOpts);
            return string.IsNullOrWhiteSpace(active?.Folder) ? null : active;
        }
        catch (Exception ex)
        {
            LogService.Error("读取美化包启用记录失败", ex);
            return null;
        }
    }

    private static void WriteActive(ActiveSkin active)
    {
        Directory.CreateDirectory(SkinsRoot);
        File.WriteAllText(ActiveFile, JsonSerializer.Serialize(active, WriteOpts));
    }

    // ---------- 工具 ----------

    private static bool IsDarkColor(string? hex)
    {
        if (!RgbColor.TryParse(hex, out var color)) return false;
        var luma = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luma < 0.5;
    }

    private static string NormalizeEntry(string name) =>
        (name ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static string Sanitize(params string?[] candidates)
    {
        var raw = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? "skin";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? "skin" : result;
    }
}
