using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DoLLauncher.Core.Versions;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>模组包导入结果：成功数量与被剔除的无效模组。</summary>
public sealed class ModPackImportResult
{
    public int Imported { get; set; }
    public List<string> Failed { get; } = new();
}

/// <summary>模组包形态。</summary>
public enum ModPackKind
{
    Unknown,
    /// <summary>整个压缩包就是一个模组包。</summary>
    SingleMod,
    /// <summary>启动器导出的模组包：manifest.json + mods/。</summary>
    LauncherPack,
    /// <summary>仅含 mods/ 文件夹。</summary>
    ModsFolder,
    /// <summary>只含一堆压缩包。</summary>
    ArchivesOnly,
}

/// <summary>模组包校验结果。Candidates 是包内待导入的模组条目（不含 manifest.json）。</summary>
public sealed class ModPackInfo
{
    public ModPackKind Kind { get; init; } = ModPackKind.Unknown;
    public List<string> Candidates { get; init; } = new();
    public bool IsValid => Kind != ModPackKind.Unknown;
}

/// <summary>
/// 版本目录下 mods/*.zip 与 mods/*.modpack 的读取、启停、排序，并生成 ModLoader 原生读取的 modList.json。
/// </summary>
public static class ModService
{
    private const string StateFileName = "mods-state.json";
    private const string ModListFileName = "modList.json";

    /// <summary>内置补丁锚点兼容层的文件名，随 modList 注入，但不出现在模组列表里。</summary>
    private const string CompatModFileName = "_DoLL-PatchAnchor.mod.zip";
    private const string CompatModVersion = "1.0.0";

    /// <summary>ModLoader 自带部件，不是磁盘模组，依赖里出现时直接视作满足。</summary>
    private static readonly string[] BuiltinPrefixes = { "ModLoader", "DoL ModLoader" };

    /// <summary>
    /// ModLoader 内置并自动加载的模组 / addon 名称（来自 ModLoader 运行日志）。
    /// 这些组件不会出现在 mods 目录，但依赖检查必须认为它们存在。
    /// </summary>
    private static readonly HashSet<string> BuiltinMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModLoader", "ModLoaderGui", "ModLoader DoL ImageLoaderHook", "ModSubUiAngularJs",
        "ConflictChecker", "BeautySelectorAddon", "ImageLoaderHook2BeautySelectorAddon",
        "CheckGameVersion", "CheckDoLCompressorDictionaries", "Diff3WayMerge",
        "DoLLinkButtonFilter", "DoLTimeWrapperAddon", "I18nScriptList", "I18nTweeList",
        "I18nTweeReplacer", "ModuleCssReplacer", "TweePrefixPostfixAddon",
        "TweeReplacer", "TweeReplacerLinker", "ImageLoaderHook", "ImageLoaderHookCore",
        "ReplacePatcher", "SweetAlert2Mod", "ModdedClothesAddon", "ModdedFeatsAddon",
        "ModdedHairAddon",
    };

    /// <summary>内置名单的归一化形式：前置写成「Mod Loader」「ModLoader GUI」等变体也能识别。</summary>
    private static readonly HashSet<string> BuiltinKeys =
        new(BuiltinMods.Select(NormalizeModName), StringComparer.Ordinal);

    private static readonly string[] BuiltinPrefixKeys =
        BuiltinPrefixes.Select(NormalizeModName).ToArray();

    /// <summary>包文件名里的版本尾巴，例如 "-0.5.12.13-v5.6.2"、".ver2.0.5.build_2"。</summary>
    private static readonly Regex VersionTail = new(
        @"[-_. ](?:ver(?:sion)?|v)?\d[\d.]*.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 部分模组（如 Ray 美化）的 boot.json 带尾随逗号、注释，ModLoader 能正常读，
    /// 但严格 JSON 解析会失败，这里统一放宽，避免被误判为“无法解析”。
    /// </summary>
    public static readonly JsonDocumentOptions BootJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static string ModsDir(string versionDir) => Path.Combine(versionDir, "mods");

    /// <summary>ModLoader 能加载的模组包扩展名（zip 与原生 .modpack）。</summary>
    public static bool IsModPackage(string path)
    {
        var name = path.ToLowerInvariant();
        return name.EndsWith(".zip") || name.EndsWith(".modpack") || name.EndsWith(".modpack.crypt");
    }

    public static bool IsNativePack(string path) =>
        !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>只做格式校验，不解压、不写盘。</summary>
    public static ModPackInfo ValidateModPack(ZipArchive zip)
    {
        var files = zip.Entries
            .Where(e => !e.FullName.EndsWith('/'))
            .Select(e => e.FullName.Replace('\\', '/'))
            .ToList();
        if (files.Count == 0) return new ModPackInfo();

        var hasManifest = files.Any(n => n.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        var content = files
            .Where(n => !n.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var underMods = content.Count > 0 &&
                        content.All(n => n.StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
        var allArchives = content.Count > 0 && content.All(IsModPackage);
        var singleMod = files.Any(n => n.Equals("boot.json", StringComparison.OrdinalIgnoreCase)) ||
                        UniqueNestedBootPrefix(zip) != null;

        if (hasManifest)
        {
            if (!underMods) return new ModPackInfo();
            var candidates = content.Where(IsModPackage).ToList();
            return candidates.Count == 0
                ? new ModPackInfo()
                : new ModPackInfo { Kind = ModPackKind.LauncherPack, Candidates = candidates };
        }

        if (singleMod) return new ModPackInfo { Kind = ModPackKind.SingleMod };

        if (underMods)
        {
            var candidates = content.Where(IsModPackage).ToList();
            return candidates.Count == 0
                ? new ModPackInfo()
                : new ModPackInfo { Kind = ModPackKind.ModsFolder, Candidates = candidates };
        }

        return allArchives
            ? new ModPackInfo { Kind = ModPackKind.ArchivesOnly, Candidates = content }
            : new ModPackInfo();
    }

    /// <summary>
    /// 把已校验的模组包导入版本的 mods 目录：先写入 mods 下的隐藏临时目录，逐个校验后只把有效模组移入 mods。
    /// </summary>
    public static ModPackImportResult ImportPack(string versionDir, string packPath, ZipArchive zip,
        ModPackInfo info, Action<string> log)
    {
        if (!info.IsValid)
            throw new InvalidOperationException("这个压缩包不是可识别的模组包。");

        var copySelf = info.Kind == ModPackKind.SingleMod;
        var wanted = new HashSet<string>(info.Candidates, StringComparer.OrdinalIgnoreCase);
        var files = zip.Entries
            .Where(e => !e.FullName.EndsWith('/'))
            .Select(e => (Entry: e, Name: e.FullName.Replace('\\', '/')))
            .ToList();
        var candidates = files.Where(f => wanted.Contains(f.Name)).ToList();

        if (!copySelf)
        {
            foreach (var skipped in files.Where(f => !wanted.Contains(f.Name) &&
                         !f.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                log($"跳过非模组文件：{skipped.Name}");
        }

        var modsDir = ModsDir(versionDir);
        Directory.CreateDirectory(modsDir);

        var tempDir = Path.Combine(modsDir, ".import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try { File.SetAttributes(tempDir, FileAttributes.Hidden); } catch { }

        var result = new ModPackImportResult();
        var staged = new List<string>();

        try
        {
            if (copySelf)
            {
                var name = SanitizeFileName(Path.GetFileName(packPath));
                var path = Path.Combine(tempDir, name);
                File.Copy(packPath, path, overwrite: true);
                if (NormalizeStaged(path, log)) staged.Add(path);
                else
                {
                    log($"跳过 {name}：不是有效的模组包。");
                    TryDeleteFile(path);
                    result.Failed.Add(name);
                }
            }
            else
            {
                foreach (var (entry, entryName) in candidates)
                {
                    var name = SanitizeFileName(Path.GetFileName(entryName));
                    if (name.Length == 0) continue;

                    var path = UniquePath(tempDir, name);
                    using (var input = entry.Open())
                    using (var output = File.Create(path))
                    {
                        input.CopyTo(output);
                    }

                    if (NormalizeStaged(path, log)) staged.Add(path);
                    else
                    {
                        log($"跳过 {name}：不是有效的模组包。");
                        TryDeleteFile(path);
                        result.Failed.Add(name);
                    }
                }
            }

            foreach (var path in staged)
            {
                File.Move(path, Path.Combine(modsDir, Path.GetFileName(path)), overwrite: true);
                result.Imported++;
            }

            return result;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>暂存模组校验：原生包按扩展名信任；zip 需根目录 boot.json，或去掉唯一一层子目录后可用。</summary>
    private static bool NormalizeStaged(string path, Action<string> log)
    {
        if (IsNativePack(path)) return true;

        try
        {
            string? tmp = null;
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.Entries.Any(e => e.FullName.Equals("boot.json", StringComparison.OrdinalIgnoreCase)))
                    return true;

                var prefix = UniqueNestedBootPrefix(zip);
                if (prefix == null) return false;

                tmp = path + ".tmp";
                RepackNestedMod(zip, prefix, tmp);
            }

            File.Move(tmp, path, overwrite: true);
            log($"已去掉多余的一层目录：{Path.GetFileName(path)}");
            return true;
        }
        catch
        {
            try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
            return false;
        }
    }

    /// <summary>zip 内唯一一层子目录带 boot.json 时返回该目录名，否则返回 null。</summary>
    private static string? UniqueNestedBootPrefix(ZipArchive zip)
    {
        var prefixes = zip.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(n => n.EndsWith("/boot.json", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Split('/'))
            .Where(parts => parts.Length == 2)
            .Select(parts => parts[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return prefixes.Count == 1 ? prefixes[0] : null;
    }

    /// <summary>把 zip 里指定子目录的内容重打包到 target，去掉该层目录。</summary>
    private static void RepackNestedMod(ZipArchive zip, string prefix, string target)
    {
        using var dest = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = name[(prefix.Length + 1)..];
            if (rel.Length == 0 || rel.EndsWith('/')) continue;

            var created = dest.CreateEntry(rel, CompressionLevel.Optimal);
            using var input = entry.Open();
            using var output = created.Open();
            input.CopyTo(output);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>读取磁盘模组列表（按用户在启动器里调整的顺序），并套用启停状态。</summary>
    public static List<ModInfo> Load(string versionDir)
    {
        var modsDir = ModsDir(versionDir);
        if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);

        var files = Directory.GetFiles(modsDir)
            .Where(IsModPackage)
            .Where(f => !IsCompatMod(f))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var state = LoadState(versionDir);
        var result = new List<ModInfo>();

        var ordered = new List<string>();
        foreach (var name in state.Order)
        {
            var hit = files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
            if (hit != null && !ordered.Contains(hit)) ordered.Add(hit);
        }

        if (ordered.Count == 0)
        {
            ordered = DefaultOrder(files);
        }
        else
        {
            foreach (var f in files)
            {
                if (!ordered.Contains(f)) ordered.Add(f);
            }
        }

        foreach (var file in ordered)
        {
            var info = ReadMod(file);
            var fileName = Path.GetFileName(file);
            info.Enabled = !state.Disabled.Contains(fileName);
            result.Add(info);
        }

        return result;
    }

    /// <summary>保存启停/顺序，并重新生成 modList.json；写文件前通知界面忽略自身的监听事件。</summary>
    public static event Action? InternalWriteStarted;

    public static void Save(string versionDir, IEnumerable<ModInfo> mods)
    {
        InternalWriteStarted?.Invoke();
        var list = mods.ToList();
        var state = new ModState
        {
            Order = list.Select(m => m.FileName).ToList(),
            Disabled = list.Where(m => !m.Enabled).Select(m => m.FileName).ToList(),
        };
        var statePath = Path.Combine(versionDir, StateFileName);
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, JsonOpts), new UTF8Encoding(false));

        var enabled = list.Where(m => m.Enabled).Select(m => "mods/" + m.FileName).ToList();

        // 装了汉化包时，把内置的补丁锚点兼容层排在它后面加载，用来修正偏移漂移的补丁。
        var i18nIndex = enabled.FindLastIndex(p =>
            Path.GetFileName(p).StartsWith("ModI18N", StringComparison.OrdinalIgnoreCase));
        if (i18nIndex >= 0 && EnsureCompatMod(versionDir))
            enabled.Insert(i18nIndex + 1, "mods/" + CompatModFileName);

        var json = enabled.Count == 0 ? "[]" : JsonSerializer.Serialize(enabled, JsonOpts);
        File.WriteAllText(Path.Combine(versionDir, ModListFileName), json, new UTF8Encoding(false));
    }

    private static bool IsCompatMod(string path) =>
        string.Equals(Path.GetFileName(path), CompatModFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>把内置兼容层写成模组包；文件缺失或版本落后时重建。</summary>
    private static bool EnsureCompatMod(string versionDir)
    {
        try
        {
            var modsDir = ModsDir(versionDir);
            Directory.CreateDirectory(modsDir);
            var path = Path.Combine(modsDir, CompatModFileName);
            if (File.Exists(path) && CompatModIsCurrent(path)) return true;

            using var stream = typeof(ModService).Assembly
                .GetManifestResourceStream("DoLLauncher.compat.patch-anchor.js");
            if (stream == null) return false;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var script = reader.ReadToEnd();

            var temp = path + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                WriteZipEntry(zip, "boot.json", CompatBootJson());
                WriteZipEntry(zip, "patch-anchor.js", script);
            }
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            // 游戏运行时包可能被占用：这次先跳过，下次重建 modList 时再写。
            return false;
        }
    }

    private static bool CompatModIsCurrent(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("boot.json");
            if (entry == null) return false;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd()
                .Contains($"\"version\": \"{CompatModVersion}\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteZipEntry(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string CompatBootJson() =>
        "{\n" +
        "  \"name\": \"DoLL 补丁兼容层\",\n" +
        $"  \"version\": \"{CompatModVersion}\",\n" +
        "  \"scriptFileList_inject_early\": [\"patch-anchor.js\"],\n" +
        "  \"scriptFileList_earlyload\": [],\n" +
        "  \"scriptFileList_preload\": [],\n" +
        "  \"scriptFileList\": [],\n" +
        "  \"styleFileList\": [],\n" +
        "  \"tweeFileList\": [],\n" +
        "  \"imgFileList\": [],\n" +
        "  \"additionFile\": [],\n" +
        "  \"addonPlugin\": [],\n" +
        "  \"dependenceInfo\": []\n" +
        "}";

    public static void Apply(string versionDir) => Save(versionDir, Load(versionDir));

    private static ModState LoadState(string versionDir)
    {
        var path = Path.Combine(versionDir, StateFileName);
        try
        {
            if (!File.Exists(path)) return new ModState();
            var text = File.ReadAllText(path, Encoding.UTF8);
            var state = JsonSerializer.Deserialize<ModState>(text);
            return state ?? new ModState();
        }
        catch
        {
            return new ModState();
        }
    }

    /// <summary>没有历史顺序时的默认排序：ModHub → ModI18N → 图像包 → 其他。</summary>
    private static List<string> DefaultOrder(IEnumerable<string> files)
    {
        static int Priority(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("ModHub", StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.StartsWith("ModI18N", StringComparison.OrdinalIgnoreCase)) return 1;
            if (name.StartsWith("GameOriginalImagePack", StringComparison.OrdinalIgnoreCase)) return 2;
            return 99;
        }

        return files
            .OrderBy(Priority)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsBuiltin(string? name)
    {
        var key = NormalizeModName(name);
        if (key.Length == 0) return false;
        if (BuiltinKeys.Contains(key)) return true;
        return BuiltinPrefixKeys.Any(p => key.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>GameVersion 是 ModLoader 内置 CheckGameVersion 提供的虚拟前置，不是磁盘模组。</summary>
    public static bool IsGameVersion(string? name)
    {
        var key = NormalizeModName(name);
        return key == "gameversion" || key == "checkgameversion";
    }

    /// <summary>
    /// 前置名比较用的归一化：只保留字母与数字并转小写，从而忽略空格、下划线、连字符、点号等符号差异。
    /// 例：「Simple Framework」「Simple-Framework」「simple_frameworks」→ simpleframework(s)。
    /// </summary>
    public static string NormalizeModName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>去掉 .zip / .modpack(.crypt) 扩展名，以及包内常见的 .mod 后缀。</summary>
    public static string FileNameStem(string? fileName)
    {
        var name = (fileName ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;
        if (name.EndsWith(".modpack.crypt", StringComparison.OrdinalIgnoreCase))
            name = name[..^".modpack.crypt".Length];
        else if (name.EndsWith(".modpack", StringComparison.OrdinalIgnoreCase))
            name = name[..^".modpack".Length];
        else
            name = Path.GetFileNameWithoutExtension(name);
        if (name.EndsWith(".mod", StringComparison.OrdinalIgnoreCase))
            name = name[..^".mod".Length];
        return name;
    }

    /// <summary>
    /// 本地模组可用于匹配前置名的键：boot.json 的 name、包文件名主干、去掉版本尾巴的包名主干。
    /// 实际样本：Simple.Framework.ver2.0.5.build_2.zip 的 boot name 是 Simple Frameworks，
    /// 而前置常写成 Simple Framework / Simple.Frameworks，必须归一化后才能匹配。
    /// </summary>
    public static IReadOnlyList<string> NameKeys(ModInfo mod)
    {
        var keys = new List<string>();
        void Add(string? raw)
        {
            var key = NormalizeModName(raw);
            if (key.Length > 0 && !keys.Contains(key)) keys.Add(key);
        }

        Add(mod.Name);
        var stem = FileNameStem(mod.FileName);
        Add(stem);
        Add(VersionTail.Replace(stem, string.Empty));

        // 市场里「简易框架」的 bootName / alias 同时是 Simple Framework 与 Simple Frameworks，
        // 只差一个英文复数 s；给长度足够的键补一个单数键，避免这类别名差异被当成缺失。
        foreach (var key in keys.ToArray())
        {
            if (key.Length > 5 && key.EndsWith("s", StringComparison.Ordinal))
                Add(key[..^1]);
        }
        return keys;
    }

    /// <summary>读取 zip 内 boot.json 与 README，用于模组详情面板。</summary>
    public static ModDetail ReadDetail(ModInfo mod)
    {
        var detail = new ModDetail
        {
            FileName = mod.FileName,
            FilePath = mod.FilePath,
            Name = mod.Name,
            Version = mod.Version,
            Description = mod.Description,
            BootParsed = mod.BootParsed,
            Dependencies = mod.Dependencies.Select(d => new ModDependency { Name = d.Name, Version = d.Version }).ToList(),
        };

        try
        {
            using var zip = ZipFile.OpenRead(mod.FilePath);
            var boot = FindEntry(zip, "boot.json");
            if (boot != null)
            {
                using var stream = boot.Open();
                using var doc = JsonDocument.Parse(stream, BootJsonOptions);
                var root = doc.RootElement;

                detail.Name = PropString(root, "name").Length > 0 ? PropString(root, "name") : detail.Name;
                var version = root.TryGetProperty("version", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
                if (version.Length > 0) detail.Version = version;
                detail.Description = PropString(root, "description");
                detail.Author = FirstNonEmpty(
                    PropString(root, "author"),
                    PropString(root, "authors"),
                    PropStrings(root, "author").FirstOrDefault() ?? "",
                    PropStrings(root, "authors").FirstOrDefault() ?? "");
                detail.Homepage = FirstNonEmpty(
                    PropString(root, "homepage"),
                    PropString(root, "repository"),
                    PropString(root, "github"),
                    PropString(root, "url"));

                detail.Dependencies.Clear();
                if (root.TryGetProperty("dependenceInfo", out var deps) && deps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dep in deps.EnumerateArray())
                    {
                        var modName = PropString(dep, "modName").Trim();
                        var ver = dep.TryGetProperty("version", out var dv)
                            ? (dv.ValueKind == JsonValueKind.String ? dv.GetString() ?? "" : dv.ToString())
                            : "";
                        if (modName.Length > 0) detail.Dependencies.Add(new ModDependency { Name = modName, Version = ver });
                    }
                }
            }

            var readme = FindEntry(zip, "readme.md") ?? FindEntry(zip, "readme.txt") ?? FindEntry(zip, "readme");
            if (readme != null)
            {
                using var stream = readme.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                detail.Readme = text.Length > 30000 ? text[..30000] + "\n…（README 过长，已截断）" : text;
            }
        }
        catch
        {
            // 损坏的 zip：保留基本信息即可
        }

        return detail;
    }

    /// <summary>
    /// 把市场索引里补全的作者 / 主页写回模组包内的 boot.json。
    /// 只补缺失的字段，已有值不覆盖；返回是否真的改动了模组包。
    /// </summary>
    public static bool WriteBackMetadata(ModInfo mod, string author, string homepage)
    {
        if (author.Length == 0 && homepage.Length == 0) return false;
        if (!File.Exists(mod.FilePath)) return false;

        try
        {
            var changed = false;
            using var zip = ZipFile.Open(mod.FilePath, ZipArchiveMode.Update);
            var boot = FindEntry(zip, "boot.json");
            if (boot == null) return false;

            string text;
            using (var stream = boot.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                text = reader.ReadToEnd();

            var node = JsonNode.Parse(text, documentOptions: BootJsonOptions);
            if (node is not JsonObject root) return false;

            if (author.Length > 0 &&
                !HasText(root, "author") && !HasText(root, "authors"))
            {
                root["author"] = author;
                changed = true;
            }

            if (homepage.Length > 0 &&
                !HasText(root, "homepage") && !HasText(root, "repository") &&
                !HasText(root, "github") && !HasText(root, "url"))
            {
                root["homepage"] = homepage;
                changed = true;
            }

            if (!changed) return false;

            var json = root.ToJsonString(JsonOpts);
            var entryName = boot.FullName;
            boot.Delete();
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var outStream = entry.Open();
            using var writer = new StreamWriter(outStream, new UTF8Encoding(false));
            writer.Write(json);
            return true;
        }
        catch
        {
            // 写回失败不影响打开详情窗，忽略即可
            return false;
        }
    }

    private static bool HasText(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var value) || value == null) return false;
        if (value is JsonArray array) return array.Count > 0;
        if (value is JsonValue jsonValue)
        {
            var text = jsonValue.ToString();
            return text.Length > 0 && text != "null";
        }
        return false;
    }

    /// <summary>导入一批模组 zip（支持包内多套一层目录），返回成功数量。</summary>
    public static int ImportFiles(string versionDir, IEnumerable<string> files, Action<string> log)
    {
        var modsDir = ModsDir(versionDir);
        Directory.CreateDirectory(modsDir);
        var count = 0;

        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            if (!IsModPackage(file))
            {
                log($"跳过 {Path.GetFileName(file)}：只支持 .zip / .modpack 模组包。");
                continue;
            }

            // .modpack 是 ModLoader 的私有二进制格式，原样放入 mods 即可
            if (IsNativePack(file))
            {
                var copyTarget = UniquePath(modsDir, Path.GetFileName(file));
                File.Copy(file, copyTarget);
                log($"已导入 {Path.GetFileName(copyTarget)}");
                count++;
                continue;
            }

            try
            {
                using var zip = ZipFile.OpenRead(file);
                var rootBoot = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Equals("boot.json", StringComparison.OrdinalIgnoreCase));

                if (rootBoot != null)
                {
                    var copyTarget = UniquePath(modsDir, Path.GetFileName(file));
                    File.Copy(file, copyTarget);
                    log($"已导入 {Path.GetFileName(copyTarget)}");
                    count++;
                    continue;
                }

                // 没有根 boot.json：尝试取唯一一层子目录里的 boot.json，重新打包
                var prefix = UniqueNestedBootPrefix(zip);
                if (prefix == null)
                {
                    log($"跳过 {Path.GetFileName(file)}：压缩包内找不到 boot.json（不是有效模组）。");
                    continue;
                }

                var repackTarget = UniquePath(modsDir, SanitizeFileName(prefix) + ".zip");
                RepackNestedMod(zip, prefix, repackTarget);
                log($"已导入 {Path.GetFileName(repackTarget)}（已去掉多余的一层目录 {prefix}/）");
                count++;
            }
            catch (Exception ex)
            {
                log($"导入 {Path.GetFileName(file)} 失败：{ex.Message}");
            }
        }

        if (count > 0) Apply(versionDir);
        return count;
    }

    public static void Delete(string versionDir, string fileName)
    {
        var path = Path.Combine(ModsDir(versionDir), fileName);
        if (File.Exists(path)) File.Delete(path);
        var mods = Load(versionDir);
        Save(versionDir, mods);
    }

    public static void Rename(string versionDir, string fileName, string newFileName)
    {
        var mods = Load(versionDir);
        var mod = mods.FirstOrDefault(m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException("找不到要重命名的模组文件。");

        var name = SanitizeFileName(newFileName);
        if (!IsModPackage(name)) name += PackageSuffix(fileName);
        var target = UniquePath(ModsDir(versionDir), name);
        File.Move(mod.FilePath, target);

        mod.FileName = Path.GetFileName(target);
        mod.FilePath = target;
        Save(versionDir, mods);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string fileName)
    {
        var exact = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return zip.Entries.FirstOrDefault(e =>
        {
            var name = e.FullName.Replace('\\', '/');
            return name.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) &&
                   name.Count(c => c == '/') == 1;
        });
    }

    private static string PropString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString() ?? "").FirstOrDefault(v => v.Length > 0) ?? "";
        return "";
    }

    private static List<string> PropStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToList();
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string SanitizeFileName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Trim().TrimEnd('.');
    }

    /// <summary>取文件名对应的模组包后缀（.zip / .modpack / .modpack.crypt），没有时按 .zip。</summary>
    private static string PackageSuffix(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        const string crypt = ".modpack.crypt";
        const string pack = ".modpack";
        if (lower.EndsWith(crypt)) return fileName[^crypt.Length..];
        if (lower.EndsWith(pack)) return fileName[^pack.Length..];
        return ".zip";
    }

    private static string UniquePath(string dir, string name)
    {
        var candidate = Path.Combine(dir, name);
        if (!File.Exists(candidate)) return candidate;
        var suffix = PackageSuffix(name);
        var baseName = name.Length > suffix.Length ? name[..^suffix.Length] : name;
        for (var i = 2; i < 999; i++)
        {
            candidate = Path.Combine(dir, $"{baseName}-{i}{suffix}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("同名模组太多，无法生成新文件名。");
    }

    private static ModInfo ReadMod(string path)
    {
        var info = new ModInfo
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            Name = Path.GetFileNameWithoutExtension(path),
            Version = string.Empty,
        };

        // .modpack 是 ModLoader 的私有格式，启动器无法读取 boot.json，只按文件名展示
        if (IsNativePack(path)) return info;

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("boot.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return info;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(text, BootJsonOptions);
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name)
            {
                info.Name = name;
            }
            if (root.TryGetProperty("version", out var v))
            {
                info.Version = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
            }
            if (root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
            {
                info.Description = d.GetString() ?? "";
            }
            if (root.TryGetProperty("dependenceInfo", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in deps.EnumerateArray())
                {
                    var modName = dep.TryGetProperty("modName", out var mn) ? (mn.GetString() ?? "").Trim() : "";
                    var ver = dep.TryGetProperty("version", out var dv) ? dv.GetString() ?? "" : "";
                    if (modName.Length > 0)
                    {
                        info.Dependencies.Add(new ModDependency { Name = modName, Version = ver });
                    }
                }
            }
            info.BootParsed = true;
        }
        catch
        {
            // zip 损坏或 boot.json 不合法：保留文件名，标记为未解析
            info.BootParsed = false;
        }

        return info;
    }

    /// <summary>完整性 / 依赖检查，返回可直接展示的中文报告行。</summary>
    public static List<string> Check(VersionInfo version)
    {
        var lines = new List<string>();
        var html = Path.Combine(version.FullPath, Paths.HtmlName);
        if (!File.Exists(html))
        {
            var alt = Directory.GetFiles(version.FullPath, "*.html").FirstOrDefault();
            if (alt == null) lines.Add("[错误] 找不到游戏本体 HTML 文件。");
            else lines.Add($"[提示] 未找到标准文件名，实际游戏文件为 {Path.GetFileName(alt)}。");
        }
        else
        {
            var mb = new FileInfo(html).Length / 1024.0 / 1024.0;
            lines.Add($"[正常] 游戏本体存在（{mb:F1} MB）");
        }

        lines.Add($"[信息] 游戏版本：{version.GameVersion ?? "未知"}");

        var mods = Load(version.FullPath);
        lines.Add($"[信息] 磁盘模组 {mods.Count} 个，启用 {mods.Count(m => m.Enabled)} 个");

        foreach (var mod in mods.Where(m => m.Enabled))
        {
            if (!mod.BootParsed)
            {
                if (IsNativePack(mod.FilePath))
                {
                    lines.Add($"[信息] {mod.FileName} 是原生 .modpack 包，启动器只做列出与启停，无法解析依赖。");
                }
                else
                {
                    lines.Add($"[警告] {mod.FileName} 不是有效的模组 zip（缺少或无法解析 boot.json），运行时会被跳过。");
                }
                continue;
            }

            foreach (var dep in mod.Dependencies)
            {
                if (IsGameVersion(dep.Name))
                {
                    if (version.GameVersion == null)
                    {
                        lines.Add($"[提示] {mod.Name} 要求游戏版本 {dep.Version}，但启动器没识别出当前游戏版本。");
                    }
                    else if (!VersionUtil.Satisfies(dep.Version, version.GameVersion))
                    {
                        lines.Add($"[错误] {mod.Name} 要求游戏版本 {dep.Version}，当前为 {version.GameVersion}，很可能不兼容。");
                    }
                    continue;
                }

                if (IsBuiltin(dep.Name))
                {
                    // ModLoader 自带的能力，无需磁盘模组
                    continue;
                }

                var target = mods.FirstOrDefault(m =>
                    m.Enabled && string.Equals(m.Name, dep.Name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    lines.Add($"[错误] {mod.Name} 依赖模组「{dep.Name}」，但该模组不存在或未启用。");
                }
                else if (!VersionUtil.Satisfies(dep.Version, target.Version))
                {
                    lines.Add($"[警告] {mod.Name} 依赖「{dep.Name}」{dep.Version}，当前为 {target.Version}。");
                }
                else if (mods.IndexOf(target) > mods.IndexOf(mod))
                {
                    lines.Add($"[警告] {mod.Name} 依赖「{dep.Name}」，但它在列表里排在后面，建议把前置模组排到更靠前。");
                }
            }
        }

        var dupes = mods.GroupBy(m => m.Name).Where(g => g.Count() > 1).ToList();
        foreach (var dupe in dupes)
        {
            lines.Add($"[警告] 有 {dupe.Count()} 个同名模组「{dupe.Key}」，会被重复加载，建议只保留一个。");
        }

        if (lines.All(l => !l.StartsWith("[错误]"))) lines.Add("[完成] 未发现阻断性问题，可以启动。");
        return lines;
    }

    /// <summary>用 ModHub 等工具新导入磁盘模组后，可让启动器重新同步一次。</summary>
    public static void EnsureModList(string versionDir)
    {
        var path = Path.Combine(versionDir, ModListFileName);
        if (!File.Exists(path)) Apply(versionDir);
    }

    private sealed class ModState
    {
        [JsonPropertyName("order")] public List<string> Order { get; set; } = new();
        [JsonPropertyName("disabled")] public List<string> Disabled { get; set; } = new();
    }
}
