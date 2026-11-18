using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoLLauncher.Platform;

namespace DoLLauncher.Core.Data;

/// <summary>槽位存档清单中的一项：记录槽位文件体积、写回时间与内容摘要。</summary>
public sealed class SlotStoreEntry
{
    public int Slot { get; set; }
    public long SaveBytes { get; set; }
    public long DetailsBytes { get; set; }
    public string UpdatedAt { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// <summary>槽位存档清单（<c>data\saves\manifest.json</c>）。</summary>
public sealed class SlotStoreManifest
{
    public string Format { get; set; } = SlotSaveStore.Format;
    public string SaveDb { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public List<SlotStoreEntry> Slots { get; set; } = new();
}

/// <summary>
/// 槽位存档存储层：只保存游戏存档库（saves / details 两个表）的内容，
/// 按槽位拆成 <c>data\saves\slot-&lt;n&gt;.json</c> 与 <c>slot-&lt;n&gt;.details.json</c>，
/// 避免把整库（含 Mod 缓存）写入单个大文件。所有写入均为临时文件 + 原子替换。
/// </summary>
public static class SlotSaveStore
{
    public const string Format = "dol-slotstore/1";

    private const string PayloadFormat = "dol-saves/1";
    private const string SaveDbName = "degrees-of-lewdity";
    private const string SaveDbAltName = "idb";
    private const string SavesStoreName = "saves";
    private const string DetailsStoreName = "details";
    private const long MaxSlotBytes = 32L * 1024 * 1024;

    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>按版本目录记住上次汇总缓存的签名，清单未变化时直接复用。</summary>
    private static readonly Dictionary<string, string> AggregateSignatures = new(StringComparer.OrdinalIgnoreCase);

    public static string Root(string versionDir) => Path.Combine(versionDir, "data", "saves");

    public static string ManifestFile(string versionDir) => Path.Combine(Root(versionDir), "manifest.json");

    public static string SlotFile(string versionDir, int slot)
        => Path.Combine(Root(versionDir), "slot-" + slot.ToString(CultureInfo.InvariantCulture) + ".json");

    public static string DetailsFile(string versionDir, int slot)
        => Path.Combine(Root(versionDir), "slot-" + slot.ToString(CultureInfo.InvariantCulture) + ".details.json");

    public static string AggregateCacheFile(string versionDir) => Path.Combine(Root(versionDir), ".read-cache.json");

    /// <summary>“下次启动清空 Mod 缓存”标记文件（<c>data\cache-clear.pending</c>，位于存档目录之外）。</summary>
    public static string CacheClearMarker(string versionDir)
        => Path.Combine(versionDir, "data", "cache-clear.pending");

    /// <summary>登记下次启动时清空除存档库外的所有 IndexedDB；重复调用覆盖标记。</summary>
    public static void RequestCacheClear(string versionDir)
    {
        lock (Sync)
        {
            try
            {
                var dir = Path.GetDirectoryName(CacheClearMarker(versionDir));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                AtomicWrite(CacheClearMarker(versionDir), NowText());
                Log("已登记清空 Mod 缓存（保留存档库）。");
            }
            catch (Exception ex)
            {
                LogError("登记清空 Mod 缓存失败", ex);
            }
        }
    }

    /// <summary>是否存在待处理的清空 Mod 缓存标记；仅判断文件是否存在，不抛异常。</summary>
    public static bool HasCacheClearRequest(string versionDir)
    {
        try { return File.Exists(CacheClearMarker(versionDir)); }
        catch { return false; }
    }

    /// <summary>消费清空 Mod 缓存标记（删除标记文件）；标记不存在时正常返回。</summary>
    public static void CompleteCacheClear(string versionDir)
    {
        lock (Sync)
        {
            try
            {
                var marker = CacheClearMarker(versionDir);
                if (!File.Exists(marker)) return;
                File.Delete(marker);
                Log("Mod 缓存清理标记已消费。");
            }
            catch (Exception ex)
            {
                LogError("删除 Mod 缓存清理标记失败", ex);
            }
        }
    }

    private static string LegacyMirrorFile(string versionDir) => Path.Combine(versionDir, "data", "idb-mirror.json");

    private static string LegacyMirrorBackup(string versionDir) => Path.Combine(versionDir, "data", "idb-mirror.legacy.json");

    private static string LegacyPendingFile(string versionDir) => Path.Combine(versionDir, "data", "idb-mirror.pending");

    private static string LegacySnapshotDir(string versionDir) => Path.Combine(versionDir, "data", "idb-snapshots");

    /// <summary>清单或任一槽位文件存在即视为已有槽位存档。</summary>
    public static bool HasStore(string versionDir)
    {
        try
        {
            var root = Root(versionDir);
            if (File.Exists(Path.Combine(root, "manifest.json"))) return true;
            return Directory.Exists(root) && Directory.EnumerateFiles(root, "slot-*.json").Any();
        }
        catch (Exception ex)
        {
            LogError("检查槽位存档失败", ex);
            return false;
        }
    }

    /// <summary>读取清单；缺失或损坏时返回空清单，不抛异常。</summary>
    public static SlotStoreManifest ReadManifest(string versionDir)
    {
        try
        {
            var text = ReadText(ManifestFile(versionDir));
            if (string.IsNullOrWhiteSpace(text)) return new SlotStoreManifest();
            var manifest = JsonSerializer.Deserialize<SlotStoreManifest>(text, ManifestJson);
            if (manifest == null) return new SlotStoreManifest();
            manifest.Slots ??= new List<SlotStoreEntry>();
            if (string.IsNullOrWhiteSpace(manifest.Format)) manifest.Format = Format;
            return manifest;
        }
        catch (Exception ex)
        {
            LogError("读取槽位清单失败", ex);
            return new SlotStoreManifest();
        }
    }

    /// <summary>读取单个槽位；存档文件缺失或损坏时返回 false，详情损坏时按无详情处理。</summary>
    public static bool TryReadSlot(string versionDir, int slot, out string? saveJson, out string? detailsJson)
    {
        saveJson = null;
        detailsJson = null;
        if (slot < 0) return false;
        try
        {
            var text = ReadText(SlotFile(versionDir, slot));
            if (!IsSlotPayload(text, slot)) return false;
            saveJson = text;

            var details = ReadText(DetailsFile(versionDir, slot));
            if (!string.IsNullOrWhiteSpace(details))
            {
                if (IsJsonObject(details)) detailsJson = details;
                else Log($"槽位 {slot} 的详情文件不是有效 JSON 对象，已忽略。");
            }
            return true;
        }
        catch (Exception ex)
        {
            LogError($"读取槽位 {slot} 失败", ex);
            return false;
        }
    }

    /// <summary>写入单个槽位（存档 + 可选详情），随后重算清单条目并原子写清单。</summary>
    public static void WriteSlot(string versionDir, int slot, string saveJson, string? detailsJson, string saveDb)
    {
        var saveBytes = ValidateSlotPayload(saveJson, slot);
        if (!string.IsNullOrWhiteSpace(detailsJson))
        {
            if (Utf8.GetByteCount(detailsJson) > MaxSlotBytes)
                throw new InvalidOperationException("单个槽位的详情文本超过 32 MB，已拒绝写入。");
            if (!IsJsonObject(detailsJson))
                throw new InvalidOperationException("存档详情不是有效的 JSON 对象。");
        }

        lock (Sync)
        {
            Directory.CreateDirectory(Root(versionDir));

            AtomicWrite(SlotFile(versionDir, slot), saveJson);
            if (string.IsNullOrWhiteSpace(detailsJson))
            {
                try { File.Delete(DetailsFile(versionDir, slot)); } catch { }
            }
            else
            {
                AtomicWrite(DetailsFile(versionDir, slot), detailsJson);
            }

            var now = NowText();
            var entry = new SlotStoreEntry
            {
                Slot = slot,
                SaveBytes = SafeLength(SlotFile(versionDir, slot)),
                DetailsBytes = SafeLength(DetailsFile(versionDir, slot)),
                UpdatedAt = now,
                Sha256 = Convert.ToHexString(SHA256.HashData(saveBytes)).ToLowerInvariant(),
            };

            var manifest = ReadManifest(versionDir);
            manifest.Format = Format;
            manifest.SaveDb = saveDb ?? "";
            manifest.UpdatedAt = now;
            manifest.Slots.RemoveAll(s => s.Slot == slot);
            manifest.Slots.Add(entry);
            manifest.Slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            AtomicWrite(ManifestFile(versionDir), JsonSerializer.Serialize(manifest, ManifestJson));
        }
    }

    /// <summary>删除单个槽位的存档与详情并更新清单；槽位不存在时返回 false。</summary>
    public static bool DeleteSlot(string versionDir, int slot)
    {
        if (slot < 0) return false;
        lock (Sync)
        {
            var save = SlotFile(versionDir, slot);
            var details = DetailsFile(versionDir, slot);
            if (!File.Exists(save) && !File.Exists(details)) return false;

            try
            {
                try { File.Delete(save); } catch (Exception ex) { LogError($"删除槽位 {slot} 存档失败", ex); }
                try { File.Delete(details); } catch (Exception ex) { LogError($"删除槽位 {slot} 详情失败", ex); }

                var manifest = ReadManifest(versionDir);
                manifest.Format = Format;
                manifest.UpdatedAt = NowText();
                manifest.Slots.RemoveAll(s => s.Slot == slot);
                AtomicWrite(ManifestFile(versionDir), JsonSerializer.Serialize(manifest, ManifestJson));
                return true;
            }
            catch (Exception ex)
            {
                LogError($"更新槽位清单失败（删除槽位 {slot}）", ex);
                return false;
            }
        }
    }

    /// <summary>清空所有槽位文件并写入空清单（保留存档库名）。</summary>
    public static void Clear(string versionDir, string saveDb)
    {
        lock (Sync)
        {
            try
            {
                var root = Root(versionDir);
                if (Directory.Exists(root))
                {
                    foreach (var file in Directory.EnumerateFiles(root, "slot-*"))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                else
                {
                    Directory.CreateDirectory(root);
                }

                var manifest = new SlotStoreManifest
                {
                    SaveDb = saveDb ?? "",
                    UpdatedAt = NowText(),
                };
                AtomicWrite(ManifestFile(versionDir), JsonSerializer.Serialize(manifest, ManifestJson));

                try { if (File.Exists(AggregateCacheFile(versionDir))) File.Delete(AggregateCacheFile(versionDir)); } catch { }
                AggregateSignatures.Remove(Path.GetFullPath(versionDir));
            }
            catch (Exception ex)
            {
                LogError("清空槽位存档失败", ex);
            }
        }
    }

    /// <summary>槽位存档摘要，供备份页提示。</summary>
    public static string Describe(string versionDir)
    {
        try
        {
            var manifest = ReadManifest(versionDir);
            if (manifest.Slots.Count == 0) return "还没有磁盘槽位：启动一次游戏后自动生成。";

            long total = 0;
            foreach (var entry in manifest.Slots)
            {
                var bytes = entry.SaveBytes + entry.DetailsBytes;
                if (bytes <= 0) bytes = SafeLength(SlotFile(versionDir, entry.Slot)) + SafeLength(DetailsFile(versionDir, entry.Slot));
                total += bytes;
            }

            var latest = manifest.Slots.OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal).First();
            return $"槽位 {latest.Slot} · {FormatSize(total)} · 最近写回 {TrimSeconds(latest.UpdatedAt)}";
        }
        catch (Exception ex)
        {
            return "槽位存档读取失败：" + ex.Message;
        }
    }

    /// <summary>是否存在旧版整库镜像 <c>data\idb-mirror.json</c>。</summary>
    public static bool HasLegacyMirror(string versionDir)
    {
        try { return File.Exists(LegacyMirrorFile(versionDir)); }
        catch { return false; }
    }

    /// <summary>
    /// 把旧版整库镜像里存档库的 saves / details 拆成槽位文件；
    /// 成功后直接删除旧整库镜像（含遗留的 idb-mirror.legacy.json）、pending 与快照目录；
    /// 镜像里没有存档库或没有槽位记录时也清理，避免每次启动重复解析数百 MB。
    /// 返回迁移槽数；格式损坏等无法处理的情况返回 -1（保留旧文件）。
    /// </summary>
    public static int MigrateLegacyMirror(string versionDir, out string message)
    {
        message = "";
        var legacy = LegacyMirrorFile(versionDir);
        try
        {
            if (!File.Exists(legacy)) return 0;
        }
        catch (Exception ex)
        {
            LogError("检查旧镜像失败", ex);
            message = "检查旧镜像失败：" + ex.Message;
            return -1;
        }

        lock (Sync)
        {
            try
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(legacy);
                }
                catch (Exception ex)
                {
                    LogError("读取旧镜像失败", ex);
                    message = "读取旧镜像失败：" + ex.Message;
                    return -1;
                }

                using var doc = JsonDocument.Parse(bytes);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("dbs", out var dbs) || dbs.ValueKind != JsonValueKind.Array)
                {
                    message = "旧镜像格式不正确，已保留原文件。";
                    return -1;
                }

                if (!TryFindSaveDb(root, out var db, out var dbName))
                {
                    var freedNoDb = DeleteLegacyArtifacts(versionDir);
                    message = $"旧镜像里没有存档库，已删除旧镜像与快照释放约 {freedNoDb / 1024.0 / 1024.0:F1} MB。";
                    Log(message);
                    return 0;
                }

                var details = ReadDetailsMap(db);
                var migrated = 0;
                if (TryFindStore(db, SavesStoreName, out var saves) &&
                    saves.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                {
                    foreach (var record in records.EnumerateArray())
                    {
                        if (!TryGetRecordValue(record, out var value)) continue;
                        if (!TryGetSlot(value, out var slot)) continue;

                        details.TryGetValue(slot, out var detailText);
                        WriteSlot(versionDir, slot, value.GetRawText(), detailText,
                            string.IsNullOrWhiteSpace(dbName) ? SaveDbName : dbName);
                        migrated++;
                    }
                }

                if (migrated == 0)
                {
                    // 存档库存在但没有任何槽位记录：旧镜像里只剩 Mod 缓存，删除以免每次启动都重新解析数百 MB。
                    var freedEmpty = DeleteLegacyArtifacts(versionDir);
                    message = $"旧镜像里没有存档记录，已删除旧镜像与快照释放约 {freedEmpty / 1024.0 / 1024.0:F1} MB。";
                    Log(message);
                    return 0;
                }

                var freed = DeleteLegacyArtifacts(versionDir);

                message = $"已把旧镜像里的 {migrated} 个槽位迁移到槽位存档，" +
                          $"删除旧整库镜像与快照释放约 {freed / 1024.0 / 1024.0:F1} MB" +
                          "（Mod 缓存会随后自行重建）。";
                Log(message);
                return migrated;
            }
            catch (Exception ex)
            {
                LogError("迁移旧镜像失败", ex);
                message = "迁移旧镜像失败：" + ex.Message;
                return -1;
            }
        }
    }

    /// <summary>删除旧版整库镜像、遗留备份、pending 与快照目录，返回释放的字节数。</summary>
    private static long DeleteLegacyArtifacts(string versionDir)
    {
        var legacy = LegacyMirrorFile(versionDir);
        var backup = LegacyMirrorBackup(versionDir);
        var snapshots = LegacySnapshotDir(versionDir);
        var freed = SafeLength(legacy) + SafeLength(backup) + DirectorySize(snapshots);

        try { if (File.Exists(legacy)) File.Delete(legacy); }
        catch (Exception ex) { LogError("删除旧整库镜像失败", ex); }
        try { if (File.Exists(backup)) File.Delete(backup); }
        catch (Exception ex) { LogError("删除遗留整库镜像备份失败", ex); }
        try { if (File.Exists(LegacyPendingFile(versionDir))) File.Delete(LegacyPendingFile(versionDir)); } catch { }
        try { if (Directory.Exists(snapshots)) Directory.Delete(snapshots, recursive: true); }
        catch (Exception ex) { LogError("删除旧镜像快照失败", ex); }

        return freed;
    }

    /// <summary>只保留存档库，返回新的 <c>dol-saves/1</c> 文本；无法识别存档库时返回 null。</summary>
    public static string? FilterPayloadToSaveDomain(string text, out string saveDb)
    {
        saveDb = "";
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("format", out var format) ||
                format.ValueKind != JsonValueKind.String ||
                format.GetString() != PayloadFormat) return null;
            if (!TryFindSaveDb(root, out var db, out var dbName)) return null;

            saveDb = dbName;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("format", PayloadFormat);
                var exportedAt = root.TryGetProperty("exportedAt", out var exported) && exported.ValueKind == JsonValueKind.String
                    ? exported.GetString()
                    : null;
                writer.WriteString("exportedAt", string.IsNullOrWhiteSpace(exportedAt) ? DateTime.Now.ToString("O") : exportedAt);
                writer.WriteStartArray("dbs");
                WriteSaveDb(writer, db, dbName);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Utf8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
        catch (Exception ex)
        {
            LogError("裁剪存档库负载失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 把当前所有槽位组装成一个 <c>dol-saves/1</c> 汇总缓存，供按文件偏移的解析器复用。
    /// 清单未变化且缓存文件存在时直接复用；没有槽位时返回 null。
    /// </summary>
    public static string? BuildAggregateCache(string versionDir, string saveDb)
    {
        lock (Sync)
        {
            try
            {
                var manifest = ReadManifest(versionDir);
                if (manifest.Slots.Count == 0) return null;

                var dbName = string.IsNullOrWhiteSpace(saveDb)
                    ? (string.IsNullOrWhiteSpace(manifest.SaveDb) ? SaveDbName : manifest.SaveDb)
                    : saveDb;
                var target = AggregateCacheFile(versionDir);
                var key = Path.GetFullPath(versionDir);
                var signature = BuildSignature(manifest);

                if (File.Exists(target) &&
                    AggregateSignatures.TryGetValue(key, out var cached) &&
                    string.Equals(cached, signature, StringComparison.Ordinal))
                {
                    return target;
                }

                Directory.CreateDirectory(Root(versionDir));
                var temp = target + ".tmp";
                var wroteAny = false;
                try
                {
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("format", PayloadFormat);
                        writer.WriteString("exportedAt", DateTime.Now.ToString("O"));
                        writer.WriteStartArray("dbs");
                        writer.WriteStartObject();
                        writer.WriteString("name", dbName);
                        writer.WriteNumber("version", 1);
                        writer.WriteStartArray("stores");

                        wroteAny |= WriteStore(writer, SavesStoreName, versionDir, manifest, details: false);
                        wroteAny |= WriteStore(writer, DetailsStoreName, versionDir, manifest, details: true);

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    if (!wroteAny)
                    {
                        TryDelete(temp);
                        return null;
                    }

                    File.Move(temp, target, overwrite: true);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }

                AggregateSignatures[key] = signature;
                return target;
            }
            catch (Exception ex)
            {
                LogError("生成槽位汇总缓存失败", ex);
                return null;
            }
        }
    }

    /// <summary>写出存档库，只保留 saves / details 两个表，表内容原样保留。</summary>
    private static void WriteSaveDb(Utf8JsonWriter writer, JsonElement db, string dbName)
    {
        writer.WriteStartObject();
        writer.WriteString("name", string.IsNullOrWhiteSpace(dbName) ? SaveDbName : dbName);
        if (db.TryGetProperty("version", out var version))
        {
            writer.WritePropertyName("version");
            version.WriteTo(writer);
        }

        writer.WriteStartArray("stores");
        if (db.TryGetProperty("stores", out var stores) && stores.ValueKind == JsonValueKind.Array)
        {
            foreach (var store in stores.EnumerateArray())
            {
                if (store.ValueKind != JsonValueKind.Object) continue;
                var name = store.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? ""
                    : "";
                if (string.Equals(name, SavesStoreName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, DetailsStoreName, StringComparison.OrdinalIgnoreCase))
                {
                    store.WriteTo(writer);
                }
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool WriteStore(
        Utf8JsonWriter writer, string storeName, string versionDir, SlotStoreManifest manifest, bool details)
    {
        var wrote = false;
        writer.WriteStartObject();
        writer.WriteString("name", storeName);
        writer.WriteNull("keyPath");
        writer.WriteBoolean("autoIncrement", false);
        writer.WriteStartArray("records");
        foreach (var entry in manifest.Slots.OrderBy(s => s.Slot))
        {
            var path = details ? DetailsFile(versionDir, entry.Slot) : SlotFile(versionDir, entry.Slot);
            var text = ReadText(path);
            if (string.IsNullOrWhiteSpace(text)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch { continue; }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                writer.WriteStartObject();
                writer.WriteNumber("key", entry.Slot);
                writer.WritePropertyName("value");
                doc.RootElement.WriteTo(writer);
                writer.WriteEndObject();
                wrote = true;
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        return wrote;
    }

    private static string BuildSignature(SlotStoreManifest manifest)
    {
        var sb = new StringBuilder(manifest.UpdatedAt);
        foreach (var slot in manifest.Slots.OrderBy(s => s.Slot))
        {
            sb.Append('|').Append(slot.Slot.ToString(CultureInfo.InvariantCulture))
              .Append(':').Append(slot.Sha256);
        }
        return sb.ToString();
    }

    /// <summary>在 dbs 中优先找 degrees-of-lewdity，其次 idb，最后找同时含 saves / details 表的库。</summary>
    private static bool TryFindSaveDb(JsonElement root, out JsonElement db, out string name)
    {
        db = default;
        name = "";
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("dbs", out var dbs) || dbs.ValueKind != JsonValueKind.Array) return false;

        JsonElement idbDb = default;
        string idbName = "";
        JsonElement fallbackDb = default;
        string fallbackName = "";

        foreach (var candidate in dbs.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            var dbName = candidate.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "";

            if (string.Equals(dbName, SaveDbName, StringComparison.OrdinalIgnoreCase))
            {
                db = candidate;
                name = dbName;
                return true;
            }

            if (idbDb.ValueKind == JsonValueKind.Undefined &&
                string.Equals(dbName, SaveDbAltName, StringComparison.OrdinalIgnoreCase))
            {
                idbDb = candidate;
                idbName = dbName;
            }
            else if (fallbackDb.ValueKind == JsonValueKind.Undefined && HasBothStores(candidate))
            {
                fallbackDb = candidate;
                fallbackName = dbName;
            }
        }

        if (idbDb.ValueKind != JsonValueKind.Undefined)
        {
            db = idbDb;
            name = idbName;
            return true;
        }

        if (fallbackDb.ValueKind != JsonValueKind.Undefined)
        {
            db = fallbackDb;
            name = fallbackName;
            return true;
        }

        return false;
    }

    private static bool HasBothStores(JsonElement db)
        => TryFindStore(db, SavesStoreName, out _) && TryFindStore(db, DetailsStoreName, out _);

    private static bool TryFindStore(JsonElement db, string storeName, out JsonElement store)
    {
        store = default;
        if (db.ValueKind != JsonValueKind.Object ||
            !db.TryGetProperty("stores", out var stores) || stores.ValueKind != JsonValueKind.Array) return false;

        foreach (var candidate in stores.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            var name = candidate.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "";
            if (string.Equals(name, storeName, StringComparison.OrdinalIgnoreCase))
            {
                store = candidate;
                return true;
            }
        }
        return false;
    }

    private static Dictionary<int, string> ReadDetailsMap(JsonElement db)
    {
        var map = new Dictionary<int, string>();
        if (!TryFindStore(db, DetailsStoreName, out var store)) return map;
        if (!store.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array) return map;

        foreach (var record in records.EnumerateArray())
        {
            if (!TryGetRecordValue(record, out var value)) continue;
            if (!TryGetSlot(value, out var slot)) continue;
            map[slot] = value.GetRawText();
        }
        return map;
    }

    private static bool TryGetRecordValue(JsonElement record, out JsonElement value)
    {
        value = default;
        if (record.ValueKind != JsonValueKind.Object) return false;
        if (record.TryGetProperty("value", out var inner))
        {
            value = inner;
            return value.ValueKind == JsonValueKind.Object;
        }
        value = record;
        return true;
    }

    private static bool TryGetSlot(JsonElement value, out int slot)
    {
        slot = -1;
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (!value.TryGetProperty("slot", out var slotElement)) return false;

        if (slotElement.ValueKind == JsonValueKind.Number)
        {
            if (!slotElement.TryGetInt32(out var number) || number < 0) return false;
            slot = number;
            return true;
        }

        if (slotElement.ValueKind == JsonValueKind.String &&
            int.TryParse(slotElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 0)
        {
            slot = parsed;
            return true;
        }

        return false;
    }

    private static byte[] ValidateSlotPayload(string saveJson, int slot)
    {
        if (string.IsNullOrWhiteSpace(saveJson))
            throw new InvalidOperationException("存档内容为空，无法写入槽位。");
        if (slot < 0)
            throw new InvalidOperationException("槽位号不能为负数。");

        var bytes = Utf8.GetBytes(saveJson);
        if (bytes.LongLength > MaxSlotBytes)
            throw new InvalidOperationException(
                $"单个槽位的存档文本超过 32 MB（{bytes.LongLength / 1024 / 1024} MB），已拒绝写入。");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(saveJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("存档内容不是有效的 JSON。");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("存档内容不是 JSON 对象。");
            if (!TryGetSlot(root, out var actual))
                throw new InvalidOperationException("存档内容缺少有效的 slot 字段。");
            if (actual != slot)
                throw new InvalidOperationException($"存档内容的槽位（{actual}）与写入槽位（{slot}）不一致。");
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("存档内容缺少 data 对象。");
        }

        return bytes;
    }

    private static bool IsSlotPayload(string? text, int slot)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!TryGetSlot(root, out var actual) || actual != slot) return false;
            return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static void AtomicWrite(string path, string text)
    {
        var temp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(temp, text, Utf8);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            LogError("读取 " + Path.GetFileName(path) + " 失败", ex);
            return null;
        }
    }

    private static long SafeLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";
        return Math.Max(1, bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB";
    }

    private static string TrimSeconds(string updatedAt)
    {
        if (string.IsNullOrWhiteSpace(updatedAt)) return "未知";
        return updatedAt.Length >= 16 ? updatedAt.Substring(0, 16) : updatedAt;
    }

    private static string NowText() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static void Log(string message)
    {
        try { PlatformServices.Log.Write("[槽位存档] " + message); } catch { }
    }

    private static void LogError(string context, Exception ex)
    {
        try { PlatformServices.Log.WriteError("[槽位存档] " + context, ex); } catch { }
    }
}
