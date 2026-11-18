using System.IO;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.Move(temp, dest);

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

    /// <summary>启用已安装的美化包目录。</summary>
    public static bool Activate(string folder)
    {
        try
        {
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
            WindowBackdropBrush = BuildBrush(dir, manifest!.WindowBackgroundImage, manifest.WindowBackgroundColor, 0.85);
            DialogBackgroundBrush = BuildBrush(dir, manifest.DialogBackgroundImage, manifest.DialogBackgroundColor, 0.55);
            LoadStyleSheet(dir, manifest);
            SkinStyleApplier.Load(StyleSheet, dir);
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
    }

    /// <summary>把当前美化包的背景与 CSS 样式应用到主窗口；没有配置时隐藏背景层。</summary>
    public static void ApplyToWindow(Window window)
    {
        if (window.FindName("SkinBackdrop") is System.Windows.Controls.Border backdrop)
        {
            backdrop.Background = WindowBackdropBrush;
            backdrop.Visibility = WindowBackdropBrush == null ? Visibility.Collapsed : Visibility.Visible;
        }
        SkinStyleApplier.ApplyToWindow(window);
    }

    /// <summary>弹窗背景：有美化包弹窗背景时覆盖，否则沿用主题的 BgBrush。</summary>
    public static void ApplyDialogBackground(Window window)
    {
        if (DialogBackgroundBrush == null) return;
        window.Background = DialogBackgroundBrush;
    }

    private static void ClearCurrent()
    {
        Current = null;
        CurrentFolder = string.Empty;
        StyleSheet = null;
        StyleSheetPath = string.Empty;
        WindowBackdropBrush = null;
        DialogBackgroundBrush = null;
    }

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
