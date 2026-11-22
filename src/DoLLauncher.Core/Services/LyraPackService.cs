using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>Lyra 整合包内置模组的解压结果。</summary>
public sealed class LyraExtractResult
{
    /// <summary>成功写入 mods/ 的普通模组数量。</summary>
    public int Extracted { get; set; }

    /// <summary>按 ModLoader 必需组件保留在 HTML 里的数量。</summary>
    public int Skipped { get; set; }

    /// <summary>HTML 里的内置列表是否已改写（只保留必需组件）。</summary>
    public bool HtmlRewritten { get; set; }

    /// <summary>写入 mods/ 后的文件名（按原数组顺序）。</summary>
    public List<string> Files { get; } = new();
}

/// <summary>整合包 HTML 里内置模组的只读信息。</summary>
public sealed class LyraEmbeddedMod
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public long Size { get; set; }
    public int FileCount { get; set; }

    /// <summary>是否为 ModLoader 自带必需组件；这类模组保留在 HTML 里，不解压。</summary>
    public bool IsBuiltin { get; set; }
}

/// <summary>
/// 读取 Lyra 整合包 HTML 里的 <c>window.modDataValueZipList</c>，并把内置 base64 模组解压到 mods/。
/// 全程按字节流式处理，不把 79–153 MB 的 HTML 整个读入内存。
/// </summary>
public static class LyraPackService
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("window.modDataValueZipList");
    private const string TempPrefix = ".lyra-extract-";
    private const string HiddenTempHtmlSuffix = ".lyra-new";
    private const string CacheFileName = "lyra-mod-cache.json";

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (string Signature, List<LyraEmbeddedMod> Mods)> MemoryCache = new();

    private static readonly JsonSerializerOptions CacheJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>按资产文件名快速判断是否为 Lyra 整合包，避免对普通包做无谓扫描。</summary>
    public static bool IsLyraPackName(string? packName) =>
        !string.IsNullOrEmpty(packName) &&
        packName.Contains("lyra", StringComparison.OrdinalIgnoreCase);

    /// <summary>统计内置模组数量；没有该数组时返回 0。</summary>
    public static int CountEmbeddedMods(string versionDir)
    {
        var html = VersionService.FindHtml(versionDir);
        if (html == null) return 0;

        try
        {
            using var fs = OpenRead(html);
            if (!TryLocate(fs, out var contentStart)) return 0;

            var reader = new ByteReader(fs, contentStart);
            var count = 0;
            var complete = WalkArray(reader, r =>
            {
                count++;
                return SkipString(r);
            });
            return complete ? count : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>只读解析内置模组的 boot.json、压缩包大小与文件数，不落盘。</summary>
    private static List<LyraEmbeddedMod> DecodeModInfos(string html, Action<string>? log)
    {
        using var fs = OpenRead(html);
        if (!TryLocate(fs, out var contentStart)) return new List<LyraEmbeddedMod>();

        var reader = new ByteReader(fs, contentStart);
        var result = new List<LyraEmbeddedMod>();
        var index = 0;
        var ok = WalkArray(reader, r =>
        {
            index++;
            using var memory = new MemoryStream();
            if (!DecodeString(r, memory))
            {
                log?.Invoke($"[Lyra] 第 {index} 个内置模组解码失败。");
                return false;
            }

            memory.Position = 0;
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("boot.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                log?.Invoke($"[Lyra] 第 {index} 个内置模组缺少 boot.json。");
                return false;
            }

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream, ModService.BootJsonOptions);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty
                : string.Empty;
            result.Add(new LyraEmbeddedMod
            {
                Name = name,
                Version = root.TryGetProperty("version", out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString()
                    : string.Empty,
                Size = memory.Length,
                FileCount = zip.Entries.Count,
                IsBuiltin = ModService.IsBuiltin(name),
            });
            return true;
        });

        return ok ? result : new List<LyraEmbeddedMod>();
    }

    /// <summary>读取内置模组清单；结果按 HTML 大小与修改时间缓存到 data/lyra-mod-cache.json。</summary>
    public static List<LyraEmbeddedMod> ReadEmbeddedMods(string versionDir, Action<string>? log = null)
    {
        var html = VersionService.FindHtml(versionDir);
        if (html == null) return new List<LyraEmbeddedMod>();

        string signature;
        try
        {
            var info = new FileInfo(html);
            // 版本号参与签名：模型新增字段时让旧缓存自动失效
            signature = $"2:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return new List<LyraEmbeddedMod>();
        }

        lock (CacheLock)
        {
            if (MemoryCache.TryGetValue(versionDir, out var cached) && cached.Signature == signature)
                return cached.Mods;
        }

        var cachePath = Path.Combine(versionDir, "data", CacheFileName);
        try
        {
            if (File.Exists(cachePath))
            {
                var cache = JsonSerializer.Deserialize<LyraModCache>(File.ReadAllText(cachePath), CacheJsonOpts);
                if (cache != null && cache.Signature == signature)
                {
                    Remember(versionDir, signature, cache.Mods);
                    return cache.Mods;
                }
            }
        }
        catch
        {
            // 缓存损坏时重新解析
        }

        List<LyraEmbeddedMod> mods;
        try
        {
            mods = DecodeModInfos(html, log);
        }
        catch (Exception ex)
        {
            log?.Invoke("[Lyra] 读取内置模组失败：" + ex.Message);
            return new List<LyraEmbeddedMod>();
        }

        Remember(versionDir, signature, mods);
        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(cachePath,
                JsonSerializer.Serialize(new LyraModCache { Signature = signature, Mods = mods }, CacheJsonOpts),
                new UTF8Encoding(false));
        }
        catch
        {
            // 缓存写不进去不影响本次结果
        }
        return mods;
    }

    /// <summary>内置模组名列表，用于满足前置与重复加载检测。</summary>
    public static List<string> EmbeddedModNames(string versionDir) =>
        ReadEmbeddedMods(versionDir).Select(m => m.Name).Where(n => n.Length > 0).ToList();

    private static void Remember(string versionDir, string signature, List<LyraEmbeddedMod> mods)
    {
        lock (CacheLock) MemoryCache[versionDir] = (signature, mods);
    }

    /// <summary>
    /// 把 HTML 内置的 mod 解压到版本 mods/，并把数组清空，避免磁盘模组与内置模组重复加载。
    /// 任一步失败都会整体回滚：不改 HTML、不留下半成品 zip。
    /// </summary>
    public static LyraExtractResult ExtractToMods(string versionDir, Action<string> log)
    {
        var html = VersionService.FindHtml(versionDir)
                   ?? throw new InvalidOperationException("版本目录里没有找到游戏 HTML。");

        var modsDir = ModService.ModsDir(versionDir);
        Directory.CreateDirectory(modsDir);

        var tempDir = Path.Combine(modsDir, TempPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try { File.SetAttributes(tempDir, FileAttributes.Hidden); } catch { }

        var result = new LyraExtractResult();
        var staged = new List<(string TempPath, string FileName)>();
        var kept = new List<string>();
        var skipped = 0;
        var moved = new List<string>();
        string? tempHtml = null;

        try
        {
            long contentStart;
            long arrayEnd;

            using (var fs = OpenRead(html))
            {
                if (!TryLocate(fs, out contentStart))
                    throw new InvalidOperationException("这个 HTML 里没有找到内置模组列表。");

                var reader = new ByteReader(fs, contentStart);
                var index = 0;
                var ok = WalkArray(reader, r =>
                {
                    index++;
                    var tempZip = Path.Combine(tempDir, $"mod-{index:D2}.zip");
                    var raw = new StringBuilder();
                    if (!DecodeStringToFile(r, tempZip, raw))
                    {
                        log($"[Lyra] 第 {index} 个内置模组解码失败。");
                        return false;
                    }

                    var (name, version) = ReadBoot(tempZip);
                    if (ModService.IsBuiltin(name))
                    {
                        // ModLoader 必需组件继续留在 HTML 里，不搬到 mods/
                        kept.Add(raw.ToString());
                        skipped++;
                        log($"[Lyra] 保留内置必需组件：{name} {version}".TrimEnd());
                        TryDeleteFile(tempZip);
                        return true;
                    }

                    var fileName = ModFileName(name, version, index);
                    staged.Add((tempZip, fileName));
                    log($"[Lyra] 已解出普通模组：{fileName}");
                    return true;
                });

                if (!ok)
                    throw new InvalidOperationException("内置模组列表解析失败，已取消解压。");

                // WalkArray 读完 ']' 后 Position 指向下一个字节
                arrayEnd = reader.Position - 1;
            }

            result.Skipped = skipped;
            if (staged.Count == 0 && kept.Count == 0)
                return result;

            if (staged.Count == 0)
            {
                log($"[Lyra] 没有可解压的普通模组；{skipped} 个 ModLoader 必需组件保留内置。");
                return result;
            }

            // 先准备好改写后的 HTML 临时文件（只保留必需组件），避免动到原文件后才发现失败
            tempHtml = BuildRewrittenHtml(html, contentStart, arrayEnd, kept);

            foreach (var (tempPath, fileName) in staged)
            {
                var target = UniquePath(modsDir, fileName);
                File.Move(tempPath, target);
                moved.Add(target);
                result.Files.Add(Path.GetFileName(target));
                log($"[Lyra] 写入 mods/{Path.GetFileName(target)}");
            }

            // 按数组原顺序全部启用，并生成 modList.json
            var mods = ModService.Load(versionDir);
            var byName = mods.ToDictionary(m => m.FileName, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<ModInfo>();
            foreach (var fileName in result.Files)
            {
                if (!byName.TryGetValue(fileName, out var mod)) continue;
                mod.Enabled = true;
                ordered.Add(mod);
            }
            foreach (var mod in mods)
            {
                if (!ordered.Contains(mod)) ordered.Add(mod);
            }
            ModService.Save(versionDir, ordered);

            // 最后原子替换 HTML：只保留 ModLoader 必需组件
            File.Move(tempHtml!, html, overwrite: true);
            tempHtml = null;

            result.HtmlRewritten = true;
            result.Extracted = result.Files.Count;
            log($"[Lyra] 已解压 {result.Extracted} 个普通模组；{result.Skipped} 个必需组件保留内置。");
            return result;
        }
        catch
        {
            foreach (var path in moved)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }

            if (moved.Count > 0)
            {
                // 恢复 modList.json / mods-state.json，避免指向已删除的 zip
                try { ModService.Save(versionDir, ModService.Load(versionDir)); } catch { }
            }

            throw;
        }
        finally
        {
            if (tempHtml != null)
            {
                try { if (File.Exists(tempHtml)) File.Delete(tempHtml); } catch { }
            }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ---------- 扫描与解析 ----------

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    /// <summary>定位数组内容起点（'[' 后一个字节）。找不到时返回 false。</summary>
    private static bool TryLocate(FileStream fs, out long contentStart)
    {
        contentStart = -1;
        fs.Seek(0, SeekOrigin.Begin);
        var reader = new ByteReader(fs, 0);

        while (true)
        {
            if (!FindMarker(reader)) return false;

            // marker 后允许空白与 = / :，直到 '[' 才算赋值
            for (var i = 0; i < 64; i++)
            {
                var b = reader.Read();
                if (b < 0) return false;
                if (b == '[')
                {
                    contentStart = reader.Position;
                    // ByteReader 预读过的字节要丢弃，后续解析从数组内容处重新开始
                    fs.Seek(contentStart, SeekOrigin.Begin);
                    return true;
                }
                if (IsWhite(b) || b == '=' || b == ':') continue;
                break;
            }
        }
    }

    private static bool FindMarker(ByteReader reader)
    {
        var matched = 0;
        while (true)
        {
            var b = reader.Read();
            if (b < 0) return false;
            if (b == Marker[matched])
            {
                matched++;
                if (matched == Marker.Length) return true;
            }
            else
            {
                matched = b == Marker[0] ? 1 : 0;
            }
        }
    }

    /// <summary>遍历 JSON 数组元素，回调负责消费引号内的内容（含结尾引号）。</summary>
    private static bool WalkArray(ByteReader reader, Func<ByteReader, bool> element)
    {
        while (true)
        {
            var b = reader.Read();
            while (b >= 0 && (IsWhite(b) || b == ',')) b = reader.Read();
            if (b < 0) return false;
            if (b == ']') return true;
            if (b != '"') return false;
            if (!element(reader)) return false;
        }
    }

    private static bool SkipString(ByteReader reader)
    {
        while (true)
        {
            var b = reader.Read();
            if (b < 0) return false;
            if (b == '"') return true;
            if (b == '\\') reader.Read();
        }
    }

    /// <summary>把当前 JSON 字符串按 base64 流式解码到文件；raw 非空时同步收集原始 base64。</summary>
    private static bool DecodeStringToFile(ByteReader reader, string path, StringBuilder? raw = null)
    {
        try
        {
            using var output = File.Create(path);
            return DecodeString(reader, output, raw) && output.Length > 0;
        }
        catch
        {
            TryDeleteFile(path);
            return false;
        }
    }

    /// <summary>把当前 JSON 字符串按 base64 流式解码到输出流；返回是否成功读完。</summary>
    private static bool DecodeString(ByteReader reader, Stream output, StringBuilder? raw = null)
    {
        const int ChunkSize = 8192;
        var buffer = new byte[ChunkSize];
        var length = 0;

        try
        {
            while (true)
            {
                var b = reader.Read();
                if (b < 0) return false;
                if (b == '"') break;
                if (b == '\\')
                {
                    b = reader.Read();
                    if (b < 0) return false;
                }

                if (IsWhite(b)) continue;
                if (b == '-') b = (byte)'+';
                else if (b == '_') b = (byte)'/';

                buffer[length++] = (byte)b;
                if (length == ChunkSize)
                {
                    raw?.Append(Encoding.ASCII.GetString(buffer, 0, length));
                    output.Write(Convert.FromBase64String(Encoding.ASCII.GetString(buffer, 0, length)));
                    length = 0;
                }
            }

            if (length > 0)
            {
                var rem = length % 4;
                if (rem == 1) return false;
                raw?.Append(Encoding.ASCII.GetString(buffer, 0, length));
                var text = Encoding.ASCII.GetString(buffer, 0, length);
                if (rem == 2) text += "==";
                else if (rem == 3) text += "=";
                output.Write(Convert.FromBase64String(text));
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (string Name, string Version) ReadBoot(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("boot.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{Path.GetFileName(zipPath)} 里没有 boot.json。");

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream, ModService.BootJsonOptions);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? string.Empty
            : string.Empty;
        var version = root.TryGetProperty("version", out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString()
            : string.Empty;
        return (name, version);
    }

    // ---------- 文件与命名 ----------

    private static string BuildRewrittenHtml(string html, long contentStart, long arrayEnd,
        IReadOnlyList<string> keep)
    {
        // contentStart 在 '[' 之后、arrayEnd 在 ']' 上，这里只写数组内部内容
        var replacement = keep.Count == 0
            ? string.Empty
            : "\"" + string.Join("\",\"", keep) + "\"";
        var replacementBytes = Encoding.UTF8.GetBytes(replacement);

        var temp = html + HiddenTempHtmlSuffix;
        using (var src = OpenRead(html))
        using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            CopyRange(src, dst, 0, contentStart);
            dst.Write(replacementBytes, 0, replacementBytes.Length);
            src.Seek(arrayEnd, SeekOrigin.Begin);
            src.CopyTo(dst);
        }
        return temp;
    }

    private static void CopyRange(Stream src, Stream dst, long offset, long count)
    {
        src.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            var read = src.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException();
            dst.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string ModFileName(string name, string version, int index)
    {
        var stem = string.IsNullOrWhiteSpace(name) ? $"LyraMod-{index:D2}" : name.Trim();
        if (!string.IsNullOrWhiteSpace(version)) stem += "-" + version.Trim();

        var invalid = Path.GetInvalidFileNameChars();
        var chars = stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        stem = new string(chars).Trim().TrimEnd('.');
        if (stem.Length == 0) stem = $"LyraMod-{index:D2}";
        if (stem.Length > 80) stem = stem[..80].TrimEnd(' ', '.', '_');
        return stem + ".zip";
    }

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            path = Path.Combine(dir, $"{stem}-{i}{ext}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static bool IsWhite(int b) => b is ' ' or '\r' or '\n' or '\t';

    /// <summary>data/lyra-mod-cache.json 的结构。</summary>
    private sealed class LyraModCache
    {
        public string Signature { get; set; } = string.Empty;
        public List<LyraEmbeddedMod> Mods { get; set; } = new();
    }

    /// <summary>按字节读取的游标，避免 StreamReader 的字符解码与字节偏移混用。</summary>
    private sealed class ByteReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[1 << 16];
        private int _length;
        private int _index;

        public ByteReader(Stream stream, long startPosition)
        {
            _stream = stream;
            Position = startPosition;
        }

        /// <summary>下一个待读字节的绝对偏移。</summary>
        public long Position { get; private set; }

        public int Read()
        {
            if (_index >= _length)
            {
                _length = _stream.Read(_buffer, 0, _buffer.Length);
                _index = 0;
                if (_length <= 0)
                {
                    _length = 0;
                    return -1;
                }
            }

            var b = _buffer[_index++];
            Position++;
            return b;
        }
    }
}
