using System.IO;
using System.Text;
using System.Text.Json;
using DoLLauncher.Platform;

namespace DoLLauncher.Core.Data;

/// <summary>页面启动时需要的镜像 / 待导入内容 / 待清理标记。</summary>
public sealed record IdbBootPayload(string? Mirror, string? Import, bool Clean);

/// <summary>端口归属判定结果。</summary>
public enum PortClaim
{
    FirstUse,
    SameVersion,
    ChangedVersion,
}

/// <summary>
/// IndexedDB 的磁盘镜像：把页面导出的 <c>dol-saves/1</c> 文本写到 <c>versions\&lt;版本&gt;\data\</c>，
/// 页面启动时下发导入、运行期与关闭时接收写回。写入前保留快照，异常中断可在下次启动回滚。
/// 同时维护「端口待清理」标记，供删除版本后的首次打开清空该 origin 的浏览器数据。
/// </summary>
public static class IdbMirrorStore
{
    public const string Format = "dol-saves/1";

    private const int KeepSnapshots = 2;
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string DataDir(string versionDir) => Path.Combine(versionDir, "data");

    public static string MirrorFile(string versionDir) => Path.Combine(DataDir(versionDir), "idb-mirror.json");

    public static string ImportFile(string versionDir) => Path.Combine(DataDir(versionDir), "idb-import.json");

    public static string SnapshotDir(string versionDir) => Path.Combine(DataDir(versionDir), "idb-snapshots");

    private static string PendingFile(string versionDir) => Path.Combine(DataDir(versionDir), "idb-mirror.pending");

    /// <summary>按端口记录「下次打开游戏页时先清空浏览器数据」的标记目录。</summary>
    public static string CleanDir => Path.Combine(PlatformServices.Paths.ProfilesDir, ".dol-clean");

    private static string CleanMarker(int port) => Path.Combine(CleanDir, $"port-{port}.json");

    public static string? ReadMirrorText(string versionDir) => ReadValid(versionDir, MirrorFile(versionDir));

    /// <summary>上次运行期留下、尚未被页面导入的内容。</summary>
    public static string? ReadImportText(string versionDir) => ReadValid(versionDir, ImportFile(versionDir));

    /// <summary>页面启动所需的一次性数据。镜像内容均已校验，非法时按不存在处理。</summary>
    public static IdbBootPayload LoadForBoot(string versionDir, int port)
    {
        RecoverPending(versionDir);
        return new IdbBootPayload(ReadMirrorText(versionDir), ReadImportText(versionDir), HasCleanMarker(port));
    }

    /// <summary>保存页面写回的镜像：先留快照，再原子替换，成功后清掉 pending 标记。</summary>
    public static long SaveMirror(string versionDir, string text)
    {
        if (!IsValidPayload(text)) throw new InvalidOperationException("镜像内容不是有效的存档库导出（缺少 dbs）。");

        lock (Sync)
        {
            var dir = DataDir(versionDir);
            Directory.CreateDirectory(dir);
            var mirror = MirrorFile(versionDir);
            var pending = PendingFile(versionDir);
            var temp = mirror + ".tmp";

            try
            {
                Snapshot(versionDir);
                File.WriteAllText(pending, DateTime.Now.ToString("O"), Utf8);
                File.WriteAllText(temp, text, Utf8);
                File.Move(temp, mirror, overwrite: true);
                try { File.Delete(pending); } catch { }
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
            return new FileInfo(mirror).Length;
        }
    }

    /// <summary>把备份文件排队为「下次打开游戏页时导入」，不打开任何浏览器窗口。</summary>
    public static void QueueImport(string versionDir, string text)
    {
        if (!IsValidPayload(text)) throw new InvalidOperationException("这个文件不是存档助手导出的存档库（缺少 dbs）。");
        var dir = DataDir(versionDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(ImportFile(versionDir), text, Utf8);
    }

    public static void MarkImportDone(string versionDir)
    {
        try { File.Delete(ImportFile(versionDir)); } catch { }
    }

    /// <summary>启动服务器时检查未完成写入：有 pending 就回滚到最近一次有效快照。</summary>
    public static void RecoverPending(string versionDir)
    {
        try
        {
            var pending = PendingFile(versionDir);
            var temp = MirrorFile(versionDir) + ".tmp";
            if (!File.Exists(pending))
            {
                if (File.Exists(temp)) File.Delete(temp);
                return;
            }

            var mirror = MirrorFile(versionDir);
            var newest = NewestSnapshot(versionDir);
            if (newest != null)
            {
                File.Copy(newest, mirror, overwrite: true);
                Log($"检测到未完成的镜像写入，已回滚到快照 {Path.GetFileName(newest)}");
            }
            else if (!IsValidPayload(File.Exists(mirror) ? File.ReadAllText(mirror, Utf8) : null))
            {
                Log("检测到未完成的镜像写入且没有可用快照，已忽略损坏的镜像。");
                try { File.Delete(mirror); } catch { }
            }

            try { File.Delete(pending); } catch { }
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
        catch (Exception ex)
        {
            Log("镜像回滚失败：" + ex.Message);
        }
    }

    public static bool HasCleanMarker(int port)
    {
        try { return port > 0 && File.Exists(CleanMarker(port)); } catch { return false; }
    }

    /// <summary>删除版本时写入：下次在同一端口打开页面时先清空 localStorage / IndexedDB / 缓存。</summary>
    public static void WriteCleanMarker(int port, string reason)
    {
        if (port <= 0) return;
        try
        {
            Directory.CreateDirectory(CleanDir);
            var json = JsonSerializer.Serialize(new
            {
                port,
                reason,
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            File.WriteAllText(CleanMarker(port), json, Utf8);
        }
        catch (Exception ex)
        {
            Log($"写入端口清理标记失败（{port}）：{ex.Message}");
        }
    }

    public static void ClearCleanMarker(int port)
    {
        try { if (port > 0) File.Delete(CleanMarker(port)); } catch { }
    }

    /// <summary>删除该版本的镜像、快照与待导入文件（版本目录被删除前调用）。</summary>
    public static void DeleteVersionData(string versionDir)
    {
        try
        {
            var dir = DataDir(versionDir);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log("删除镜像目录失败：" + ex.Message);
        }
    }

    /// <summary>镜像状态摘要，供备份页提示。</summary>
    public static string Describe(string versionDir)
    {
        try
        {
            var mirror = MirrorFile(versionDir);
            if (!File.Exists(mirror)) return "还没有磁盘镜像：启动过一次游戏后自动生成。";
            var info = new FileInfo(mirror);
            var size = info.Length >= 1024 * 1024
                ? $"{info.Length / 1024.0 / 1024.0:F1} MB"
                : $"{Math.Max(1, info.Length / 1024.0):F0} KB";
            return $"磁盘镜像：{info.LastWriteTime:yyyy-MM-dd HH:mm} · {size}";
        }
        catch (Exception ex)
        {
            return "镜像状态读取失败：" + ex.Message;
        }
    }

    private static void Snapshot(string versionDir)
    {
        var mirror = MirrorFile(versionDir);
        if (!File.Exists(mirror)) return;
        var dir = SnapshotDir(versionDir);
        Directory.CreateDirectory(dir);
        var name = "idb-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json";
        File.Copy(mirror, Path.Combine(dir, name), overwrite: true);

        foreach (var file in new DirectoryInfo(dir).GetFiles("idb-*.json")
                     .OrderByDescending(f => f.LastWriteTime)
                     .Skip(KeepSnapshots))
        {
            try { file.Delete(); } catch { }
        }
    }

    private static string? NewestSnapshot(string versionDir)
    {
        var dir = SnapshotDir(versionDir);
        if (!Directory.Exists(dir)) return null;
        var newest = new DirectoryInfo(dir).GetFiles("idb-*.json")
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
        if (newest == null) return null;
        return IsValidPayload(File.ReadAllText(newest.FullName, Utf8)) ? newest.FullName : null;
    }

    /// <summary>端口归属记录目录：同一端口被另一个版本接管时，首次打开会先清空旧版本留下的数据。</summary>
    public static string PortDir => Path.Combine(PlatformServices.Paths.ProfilesDir, ".dol-ports");

    public static PortClaim ClaimPort(string versionDir, int port)
    {
        if (port <= 0) return PortClaim.FirstUse;
        var file = Path.Combine(PortDir, $"port-{port}.json");
        var folder = Path.GetFileName(versionDir.TrimEnd(Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateDirectory(PortDir);
            var hadOwner = File.Exists(file);
            if (hadOwner)
            {
                var text = File.ReadAllText(file, Utf8);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var owner = doc.RootElement.TryGetProperty("folder", out var value) ? value.GetString() : null;
                    if (string.Equals(owner, folder, StringComparison.OrdinalIgnoreCase))
                        return PortClaim.SameVersion;
                }
                catch
                {
                    // 记录损坏时按「首次使用」处理，避免误清空用户数据
                    return PortClaim.FirstUse;
                }
            }
            File.WriteAllText(file, JsonSerializer.Serialize(new { folder, port }), Utf8);
            return hadOwner ? PortClaim.ChangedVersion : PortClaim.FirstUse;
        }
        catch (Exception ex)
        {
            Log($"记录端口归属失败（{port}）：{ex.Message}");
            return PortClaim.FirstUse;
        }
    }

    private static string? ReadValid(string versionDir, string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Utf8);
            return IsValidPayload(text) ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValidPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("format", out var format) ||
                format.ValueKind != JsonValueKind.String ||
                format.GetString() != Format) return false;
            return root.TryGetProperty("dbs", out var dbs) && dbs.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    private static void Log(string message)
    {
        try { PlatformServices.Log.Write("[存档库] " + message); } catch { }
    }
}
