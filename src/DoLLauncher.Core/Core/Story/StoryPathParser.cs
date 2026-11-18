using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DoLLauncher.Core.Story;

/// <summary>解析出的单个原始步骤（未合并）。</summary>
internal sealed class RawMoment
{
    public string Title = "";
    public long Timestamp;
    public long StartDate;
    public long StartOffset;
    public long EndOffset;
}

/// <summary>一个槽位（saves 记录）的解析结果。</summary>
internal sealed class SlotParseResult
{
    public bool Found;
    public bool HadHistory;
    public bool IsDelta;
    public long DeltaStart = -1;
    public long DeltaEnd = -1;
    public List<RawMoment> Moments = new();
}

/// <summary>存档库扫描结果。</summary>
internal sealed class ParsedArchive
{
    public string? Format;
    public bool SawDbs;
    public bool FoundDb;
    public string? Error;
    public List<StorySlotInfo> Slots = new();
}

/// <summary>按槽位合并的临时数据。</summary>
internal sealed class SlotBuilder
{
    public string SlotId = "";
    public string SaveName = "";
    public string Title = "";
    public long SavedAt;
    public bool InSaves;
    public bool InDetails;
}

/// <summary>一个库（dbs[] 元素）的扫描结果。</summary>
internal sealed class DbParse
{
    public string Name = "";
    public Dictionary<string, SlotBuilder> Slots = new(StringComparer.Ordinal);
    public bool HasData => Slots.Count > 0;
}

/// <summary>一个库内 saves 表针对目标槽位的扫描结果。</summary>
internal sealed class DbSlotScan
{
    public string Name = "";
    public bool HasSavesStore;
    public SlotParseResult? Result;
}

/// <summary>
/// 基于分块缓冲的只进 JSON 令牌读取器：底层使用 <see cref="Utf8JsonReader"/> 的增量状态，
/// 不把整份文件载入内存，也不对大对象做反序列化。仅支持顺序读取。
/// </summary>
internal ref struct JsonFileTokenReader
{
    private const int InitialBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer;
    private int _dataLength;
    private long _baseOffset;
    private JsonReaderState _state;
    private Utf8JsonReader _reader;
    private bool _hasReader;
    private bool _eof;

    public JsonFileTokenReader(Stream stream)
    {
        _stream = stream;
        _buffer = new byte[InitialBufferSize];
        _dataLength = 0;
        _baseOffset = 0;
        _state = new JsonReaderState(new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = false,
            MaxDepth = 512,
        });
        _reader = default;
        _hasReader = false;
        _eof = false;
    }

    /// <summary>当前令牌类型。</summary>
    public JsonTokenType TokenType => _reader.TokenType;

    /// <summary>当前令牌深度（根对象为 0）。</summary>
    public int CurrentDepth => _reader.CurrentDepth;

    /// <summary>当前令牌在文件中的起始字节偏移。</summary>
    public long TokenStartOffset => _baseOffset + (long)_reader.TokenStartIndex;

    /// <summary>当前令牌结束后的文件字节偏移。</summary>
    public long ConsumedOffset => _baseOffset + (long)_reader.BytesConsumed;

    /// <summary>读取下一个令牌；没有更多内容时返回 false。</summary>
    public bool Read()
    {
        while (true)
        {
            if (_hasReader && _reader.Read()) return true;
            if (_eof) return false;
            Refill();
        }
    }

    /// <summary>读取字符串令牌（含属性名）。</summary>
    public string GetString() =>
        _reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName
            ? _reader.GetString() ?? ""
            : "";

    /// <summary>尝试读取整数令牌。</summary>
    public bool TryGetInt64(out long value) => _reader.TryGetInt64(out value);

    /// <summary>尝试读取浮点令牌。</summary>
    public bool TryGetDouble(out double value) => _reader.TryGetDouble(out value);

    /// <summary>跳过当前令牌所属的整个值（对象 / 数组 / 标量）。</summary>
    public void SkipValue()
    {
        if (TokenType != JsonTokenType.StartObject && TokenType != JsonTokenType.StartArray) return;
        var depth = 0;
        while (true)
        {
            if (TokenType == JsonTokenType.StartObject || TokenType == JsonTokenType.StartArray) depth++;
            else if (TokenType == JsonTokenType.EndObject || TokenType == JsonTokenType.EndArray) depth--;
            if (depth <= 0) return;
            if (!Read()) return;
        }
    }

    private void Refill()
    {
        var consumed = _hasReader ? (int)_reader.BytesConsumed : 0;
        if (_hasReader) _state = _reader.CurrentState;

        var leftover = _dataLength - consumed;
        if (leftover > 0) Buffer.BlockCopy(_buffer, consumed, _buffer, 0, leftover);
        _baseOffset += consumed;
        _dataLength = leftover;

        if (_dataLength >= _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);

        var read = _stream.Read(_buffer, _dataLength, _buffer.Length - _dataLength);
        if (read == 0) _eof = true;
        else _dataLength += read;

        _reader = new Utf8JsonReader(new ReadOnlySpan<byte>(_buffer, 0, _dataLength), _eof, _state);
        _hasReader = true;
    }
}

/// <summary>存档文件的分块解析实现，供 <see cref="SaveMirrorReader"/> 使用。</summary>
internal static class StoryFileParser
{
    /// <summary>期望的导出格式标识。</summary>
    public const string ExpectedFormat = "dol-saves/1";

    internal const string TargetDb = "degrees-of-lewdity";
    internal const string SavesStore = "saves";
    internal const string DetailsStore = "details";

    /// <summary>以只读共享方式打开存档文件。</summary>
    public static FileStream OpenRead(string filePath) => new(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        64 * 1024,
        FileOptions.SequentialScan);

    /// <summary>流式扫描整个文件，汇总槽位列表。</summary>
    public static ParsedArchive ParseArchive(string filePath, CancellationToken ct)
    {
        var result = new ParsedArchive();
        using var fs = OpenRead(filePath);
        var tr = new JsonFileTokenReader(fs);

        if (!tr.Read())
        {
            result.Error = "存档文件为空或无法解析";
            return result;
        }

        if (tr.TokenType != JsonTokenType.StartObject)
        {
            result.Error = "这不是存档助手导出的存档库";
            return result;
        }

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject) break;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var name = tr.GetString();
            if (!tr.Read()) break;

            if (name == "format")
            {
                result.Format = tr.TokenType == JsonTokenType.String ? tr.GetString() : null;
                if (result.Format != ExpectedFormat)
                {
                    result.Error = "这不是存档助手导出的存档库";
                    return result;
                }
            }
            else if (name == "dbs")
            {
                result.SawDbs = true;
                ParseDbs(ref tr, result, ct);
            }
            else
            {
                tr.SkipValue();
            }
        }

        if (result.Format != ExpectedFormat)
        {
            result.Error = "这不是存档助手导出的存档库";
            return result;
        }

        if (!result.SawDbs)
        {
            result.Error = "存档库缺少 dbs 数据";
            return result;
        }

        if (result.Error == null && result.Slots.Count == 0)
        {
            result.Error = "存档库里没有可用的存档槽";
        }

        return result;
    }

    private static void ParseDbs(ref JsonFileTokenReader tr, ParsedArchive result, CancellationToken ct)
    {
        if (tr.TokenType != JsonTokenType.StartArray)
        {
            tr.SkipValue();
            return;
        }

        DbParse? target = null;
        DbParse? fallback = null;

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndArray) break;
            if (tr.TokenType != JsonTokenType.StartObject)
            {
                tr.SkipValue();
                continue;
            }

            var db = new DbParse();
            ParseDb(ref tr, db, ct);
            if (string.Equals(db.Name, TargetDb, StringComparison.OrdinalIgnoreCase)) target = db;
            else if (fallback == null && db.HasData) fallback = db;
        }

        var chosen = target ?? fallback;
        if (chosen == null)
        {
            result.Error = "存档库里没有找到 degrees-of-lewdity 库";
            return;
        }

        result.FoundDb = true;
        result.Slots = BuildSlots(chosen);
        if (result.Slots.Count == 0) result.Error = "存档库里没有可用的存档槽";
    }

    private static void ParseDb(ref JsonFileTokenReader tr, DbParse db, CancellationToken ct)
    {
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var name = tr.GetString();
            if (!tr.Read()) return;

            if (name == "name" && tr.TokenType == JsonTokenType.String) db.Name = tr.GetString();
            else if (name == "stores") ParseStores(ref tr, db, ct);
            else tr.SkipValue();
        }
    }

    private static void ParseStores(ref JsonFileTokenReader tr, DbParse db, CancellationToken ct)
    {
        if (tr.TokenType != JsonTokenType.StartArray)
        {
            tr.SkipValue();
            return;
        }

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndArray) return;
            if (tr.TokenType != JsonTokenType.StartObject)
            {
                tr.SkipValue();
                continue;
            }

            ParseStore(ref tr, db, ct);
        }
    }

    private static void ParseStore(ref JsonFileTokenReader tr, DbParse db, CancellationToken ct)
    {
        var storeName = "";
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) return;

            if (prop == "name") storeName = tr.TokenType == JsonTokenType.String ? tr.GetString() : "";
            else if (prop == "records")
            {
                if (storeName == SavesStore || storeName == DetailsStore)
                    ParseRecords(ref tr, db, storeName == DetailsStore, ct);
                else
                    tr.SkipValue();
            }
            else tr.SkipValue();
        }
    }

    private static void ParseRecords(ref JsonFileTokenReader tr, DbParse db, bool isDetails, CancellationToken ct)
    {
        if (tr.TokenType != JsonTokenType.StartArray)
        {
            tr.SkipValue();
            return;
        }

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndArray) return;
            if (tr.TokenType != JsonTokenType.StartObject)
            {
                tr.SkipValue();
                continue;
            }

            ParseRecord(ref tr, db, isDetails, ct);
        }
    }

    private static void ParseRecord(
        ref JsonFileTokenReader tr, DbParse db, bool isDetails, CancellationToken ct)
    {
        var depth = tr.CurrentDepth;
        string? slotText = null;
        var date = 0L;
        var title = "";
        var saveName = "";

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject && tr.CurrentDepth == depth) break;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) break;

            if (prop == "key" && (tr.TokenType == JsonTokenType.Number || tr.TokenType == JsonTokenType.String))
                slotText ??= ReadScalarAsString(ref tr);
            else if (prop == "value" && tr.TokenType == JsonTokenType.StartObject)
                ParseRecordValue(ref tr, isDetails, ref slotText, ref date, ref title, ref saveName, ct);
            else
                tr.SkipValue();
        }

        if (string.IsNullOrEmpty(slotText)) return;
        if (!db.Slots.TryGetValue(slotText, out var builder))
        {
            builder = new SlotBuilder { SlotId = slotText };
            db.Slots[slotText] = builder;
        }

        if (isDetails)
        {
            builder.InDetails = true;
            if (builder.Title.Length == 0) builder.Title = title;
            if (builder.SaveName.Length == 0) builder.SaveName = saveName;
            if (builder.SavedAt == 0) builder.SavedAt = date;
        }
        else
        {
            builder.InSaves = true;
        }
    }

    private static void ParseRecordValue(
        ref JsonFileTokenReader tr, bool isDetails, ref string? slotText,
        ref long date, ref string title, ref string saveName, CancellationToken ct)
    {
        var depth = tr.CurrentDepth;
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject && tr.CurrentDepth == depth) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) return;

            if (prop == "slot" && (tr.TokenType == JsonTokenType.Number || tr.TokenType == JsonTokenType.String))
                slotText ??= ReadScalarAsString(ref tr);
            else if (isDetails && prop == "data" && tr.TokenType == JsonTokenType.StartObject)
                ParseDetailsData(ref tr, ref date, ref title, ref saveName, ct);
            else
                tr.SkipValue();
        }
    }

    private static void ParseDetailsData(
        ref JsonFileTokenReader tr, ref long date, ref string title, ref string saveName, CancellationToken ct)
    {
        var depth = tr.CurrentDepth;
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject && tr.CurrentDepth == depth) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) return;

            if (prop == "date" && tr.TokenType == JsonTokenType.Number) date = ReadLong(ref tr);
            else if (prop == "title") title = tr.GetString();
            else if (prop == "metadata" && tr.TokenType == JsonTokenType.StartObject)
                saveName = ReadMetadataSaveName(ref tr);
            else tr.SkipValue();
        }
    }

    private static string ReadMetadataSaveName(ref JsonFileTokenReader tr)
    {
        var depth = tr.CurrentDepth;
        var saveName = "";
        while (tr.Read())
        {
            if (tr.TokenType == JsonTokenType.EndObject && tr.CurrentDepth == depth) return saveName;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) return saveName;

            if (prop == "saveName" && tr.TokenType == JsonTokenType.String) saveName = tr.GetString();
            else tr.SkipValue();
        }

        return saveName;
    }

    private static List<StorySlotInfo> BuildSlots(DbParse db)
    {
        var list = new List<StorySlotInfo>(db.Slots.Count);
        foreach (var builder in db.Slots.Values)
        {
            if (!builder.InSaves && !builder.InDetails) continue;
            list.Add(new StorySlotInfo
            {
                SlotId = builder.SlotId,
                SaveName = string.IsNullOrWhiteSpace(builder.SaveName) ? "存档 " + builder.SlotId : builder.SaveName,
                Title = builder.Title,
                SavedAt = builder.SavedAt,
            });
        }

        list.Sort(CompareSlots);
        return list;
    }

    private static int CompareSlots(StorySlotInfo a, StorySlotInfo b)
    {
        var aNumeric = long.TryParse(a.SlotId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x);
        var bNumeric = long.TryParse(b.SlotId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y);
        if (aNumeric && bNumeric) return x.CompareTo(y);
        if (aNumeric != bNumeric) return aNumeric ? -1 : 1;
        return string.Compare(a.SlotId, b.SlotId, StringComparison.Ordinal);
    }

    private static string ReadScalarAsString(ref JsonFileTokenReader tr)
    {
        if (tr.TokenType == JsonTokenType.String) return tr.GetString();
        if (tr.TokenType == JsonTokenType.Number)
        {
            if (tr.TryGetInt64(out var n)) return n.ToString(CultureInfo.InvariantCulture);
            if (tr.TryGetDouble(out var d)) return ((long)d).ToString(CultureInfo.InvariantCulture);
        }

        return "";
    }

    private static long ReadLong(ref JsonFileTokenReader tr)
    {
        if (tr.TokenType == JsonTokenType.Number)
        {
            if (tr.TryGetInt64(out var n)) return n;
            if (tr.TryGetDouble(out var d)) return (long)d;
        }
        else if (tr.TokenType == JsonTokenType.String &&
                 long.TryParse(tr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            return s;
        }

        return 0;
    }

    /// <summary>定位指定槽位并解析其剧情步骤（history 直接读；delta 只记录区间）。</summary>
    public static SlotParseResult ParseSlot(
        string filePath, string slotId, CancellationToken ct, out string? error)
    {
        error = null;
        var result = new SlotParseResult();
        using var fs = OpenRead(filePath);
        var tr = new JsonFileTokenReader(fs);

        if (!tr.Read() || tr.TokenType != JsonTokenType.StartObject)
        {
            error = "这不是存档助手导出的存档库";
            return result;
        }

        string? format = null;
        var sawDbs = false;
        DbSlotScan? target = null;
        DbSlotScan? fallback = null;

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject) break;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var name = tr.GetString();
            if (!tr.Read()) break;

            if (name == "format")
            {
                format = tr.TokenType == JsonTokenType.String ? tr.GetString() : null;
                if (format != ExpectedFormat)
                {
                    error = "这不是存档助手导出的存档库";
                    return result;
                }
            }
            else if (name == "dbs")
            {
                sawDbs = true;
                ScanSlotDbs(ref tr, slotId, ct, ref target, ref fallback);
            }
            else
            {
                tr.SkipValue();
            }
        }

        if (format != ExpectedFormat)
        {
            error = "这不是存档助手导出的存档库";
            return result;
        }

        if (!sawDbs)
        {
            error = "存档库缺少 dbs 数据";
            return result;
        }

        var chosen = target ?? fallback;
        if (chosen?.Result == null)
        {
            error = "存档里没有剧情历史";
            return result;
        }

        return chosen.Result;
    }

    private static void ScanSlotDbs(
        ref JsonFileTokenReader tr, string slotId, CancellationToken ct,
        ref DbSlotScan? target, ref DbSlotScan? fallback)
    {
        if (tr.TokenType != JsonTokenType.StartArray)
        {
            tr.SkipValue();
            return;
        }

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndArray) return;
            if (tr.TokenType != JsonTokenType.StartObject)
            {
                tr.SkipValue();
                continue;
            }

            var scan = new DbSlotScan();
            ScanSlotDb(ref tr, slotId, scan, ct);
            if (string.Equals(scan.Name, TargetDb, StringComparison.OrdinalIgnoreCase)) target = scan;
            else if (fallback == null && scan.HasSavesStore) fallback = scan;
        }
    }

    private static void ScanSlotDb(
        ref JsonFileTokenReader tr, string slotId, DbSlotScan scan, CancellationToken ct)
    {
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var name = tr.GetString();
            if (!tr.Read()) return;

            if (name == "name" && tr.TokenType == JsonTokenType.String) scan.Name = tr.GetString();
            else if (name == "stores") ScanSlotStores(ref tr, slotId, scan, ct);
            else tr.SkipValue();
        }
    }

    private static void ScanSlotStores(
        ref JsonFileTokenReader tr, string slotId, DbSlotScan scan, CancellationToken ct)
    {
        if (tr.TokenType != JsonTokenType.StartArray)
        {
            tr.SkipValue();
            return;
        }

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndArray) return;
            if (tr.TokenType != JsonTokenType.StartObject)
            {
                tr.SkipValue();
                continue;
            }

            ScanSlotStore(ref tr, slotId, scan, ct);
        }
    }

    private static void ScanSlotStore(
        ref JsonFileTokenReader tr, string slotId, DbSlotScan scan, CancellationToken ct)
    {
        var storeName = "";
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) return;

            if (prop == "name") storeName = tr.TokenType == JsonTokenType.String ? tr.GetString() : "";
            else if (prop == "records")
            {
                if (storeName == SavesStore)
                {
                    scan.HasSavesStore = true;
                    ScanSlotRecords(ref tr, slotId, scan, ct);
                }
                else tr.SkipValue();
            }
            else tr.SkipValue();
        }
    }

    private static void ScanSlotRecords(
        ref JsonFileTokenReader tr, string slotId, DbSlotScan scan, CancellationToken ct)
    {
        if (tr.TokenType != JsonTokenType.StartArray)
        {
            tr.SkipValue();
            return;
        }

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndArray) return;
            if (tr.TokenType != JsonTokenType.StartObject)
            {
                tr.SkipValue();
                continue;
            }

            ScanSlotRecord(ref tr, slotId, scan, ct);
        }
    }

    private static void ScanSlotRecord(
        ref JsonFileTokenReader tr, string slotId, DbSlotScan scan, CancellationToken ct)
    {
        var depth = tr.CurrentDepth;
        string? slotText = null;
        SlotParseResult? found = null;

        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject && tr.CurrentDepth == depth) break;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) break;

            if (prop == "key" && (tr.TokenType == JsonTokenType.Number || tr.TokenType == JsonTokenType.String))
                slotText ??= ReadScalarAsString(ref tr);
            else if (prop == "value" && tr.TokenType == JsonTokenType.StartObject)
                ScanSlotRecordValue(ref tr, slotId, ref slotText, ref found, ct);
            else
                tr.SkipValue();
        }

        if (found != null) scan.Result ??= found;
    }

    private static void ScanSlotRecordValue(
        ref JsonFileTokenReader tr, string slotId, ref string? slotText,
        ref SlotParseResult? found, CancellationToken ct)
    {
        var depth = tr.CurrentDepth;
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject && tr.CurrentDepth == depth) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) return;

            if (prop == "slot" && (tr.TokenType == JsonTokenType.Number || tr.TokenType == JsonTokenType.String))
                slotText ??= ReadScalarAsString(ref tr);
            else if (prop == "data" && tr.TokenType == JsonTokenType.StartObject)
            {
                if (string.Equals(slotText, slotId, StringComparison.Ordinal))
                {
                    var data = new SlotParseResult { Found = true };
                    ParseSlotData(ref tr, data, ct);
                    found = data;
                }
                else
                {
                    tr.SkipValue();
                }
            }
            else
            {
                tr.SkipValue();
            }
        }
    }

    private static void ParseSlotData(ref JsonFileTokenReader tr, SlotParseResult result, CancellationToken ct)
    {
        var depth = tr.CurrentDepth;
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject && tr.CurrentDepth == depth) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) return;

            if (prop == "history" && tr.TokenType == JsonTokenType.StartArray)
            {
                result.HadHistory = true;
                ParseHistory(ref tr, result, ct);
            }
            else if (prop == "delta" && tr.TokenType == JsonTokenType.StartArray)
            {
                result.IsDelta = true;
                result.DeltaStart = tr.TokenStartOffset;
                tr.SkipValue();
                result.DeltaEnd = tr.ConsumedOffset;
            }
            else
            {
                tr.SkipValue();
            }
        }
    }

    private static void ParseHistory(ref JsonFileTokenReader tr, SlotParseResult result, CancellationToken ct)
    {
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndArray) return;
            if (tr.TokenType != JsonTokenType.StartObject)
            {
                tr.SkipValue();
                continue;
            }

            var moment = new RawMoment { StartOffset = tr.TokenStartOffset };
            ParseMoment(ref tr, moment, ct);
            moment.EndOffset = tr.ConsumedOffset;
            result.Moments.Add(moment);
        }
    }

    private static void ParseMoment(ref JsonFileTokenReader tr, RawMoment moment, CancellationToken ct)
    {
        var depth = tr.CurrentDepth;
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject && tr.CurrentDepth == depth) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) return;

            if (prop == "title") moment.Title = tr.GetString();
            else if (prop == "variables" && tr.TokenType == JsonTokenType.StartObject)
                ParseMomentVariables(ref tr, moment, ct);
            else tr.SkipValue();
        }
    }

    private static void ParseMomentVariables(ref JsonFileTokenReader tr, RawMoment moment, CancellationToken ct)
    {
        var depth = tr.CurrentDepth;
        while (tr.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (tr.TokenType == JsonTokenType.EndObject && tr.CurrentDepth == depth) return;
            if (tr.TokenType != JsonTokenType.PropertyName) continue;

            var prop = tr.GetString();
            if (!tr.Read()) return;

            if (prop == "startDate") moment.StartDate = ReadLong(ref tr);
            else if (prop == "timeStamp") moment.Timestamp = ReadLong(ref tr);
            else tr.SkipValue();
        }
    }

    /// <summary>
    /// 读取文件区间并链式还原 SugarCube 增量（delta）存档，返回每一步的完整 moment。
    /// 返回的节点可直接用于回读变量；调用方负责在提取后丢弃。
    /// </summary>
    public static List<JsonNode>? RestoreDeltaMoments(
        string filePath, long start, long end, CancellationToken ct, out string? error)
    {
        error = null;
        if (start < 0 || end <= start)
        {
            error = "增量存档缺少数据";
            return null;
        }

        JsonArray? array;
        try
        {
            var bytes = ReadRange(filePath, start, end);
            array = JsonNode.Parse(bytes, null, new JsonDocumentOptions { MaxDepth = 512 }) as JsonArray;
        }
        catch (JsonException)
        {
            error = "增量存档格式无法识别";
            return null;
        }
        catch (IOException ex)
        {
            error = "读取增量存档失败：" + ex.Message;
            return null;
        }

        if (array == null || array.Count == 0)
        {
            error = "增量存档格式无法识别";
            return null;
        }

        var first = array[0]?.DeepClone();
        if (first == null)
        {
            error = "增量存档格式无法识别";
            return null;
        }

        try
        {
            var moments = new List<JsonNode>(array.Count) { first };
            JsonNode current = first;
            for (var i = 1; i < array.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                current = current.DeepClone();
                if (array[i] is JsonObject diff) ApplyDelta(current, diff);
                moments.Add(current);
            }

            return moments;
        }
        catch (InvalidOperationException)
        {
            error = "增量存档格式无法识别";
            return null;
        }
        catch (FormatException)
        {
            error = "增量存档格式无法识别";
            return null;
        }
    }

    /// <summary>把单个差分对象应用到目标 moment 上（与游戏内 SugarCube Diff.diff 一致）。</summary>
    private static void ApplyDelta(JsonNode target, JsonObject diff)
    {
        foreach (var pair in diff)
        {
            var key = pair.Key;
            var value = pair.Value;

            if (value is JsonValue scalar)
            {
                if (scalar.TryGetValue<int>(out var op) && op == 0)
                {
                    if (target is JsonObject obj) obj.Remove(key);
                    continue;
                }

                SetChild(target, key, value.DeepClone());
                continue;
            }

            if (value is JsonArray opArray && opArray.Count > 0)
            {
                if (opArray[0] is not JsonValue opValue || !opValue.TryGetValue<int>(out var op))
                {
                    SetChild(target, key, value.DeepClone());
                    continue;
                }

                switch (op)
                {
                    case 1:
                        SpliceArray(target, opArray);
                        break;
                    case 2:
                    case 3:
                        if (opArray.Count >= 2) SetChild(target, key, opArray[1]?.DeepClone());
                        break;
                    default:
                        SetChild(target, key, value.DeepClone());
                        break;
                }

                continue;
            }

            if (value is JsonObject sub)
            {
                var child = GetChild(target, key);
                if (child == null)
                {
                    child = new JsonObject();
                    SetChild(target, key, child);
                }

                ApplyDelta(child, sub);
                continue;
            }

            SetChild(target, key, value?.DeepClone());
        }
    }

    private static void SpliceArray(JsonNode target, JsonArray opArray)
    {
        if (target is not JsonArray array || opArray.Count < 3) return;
        var from = opArray[1]?.GetValue<int>() ?? 0;
        var to = opArray[2]?.GetValue<int>() ?? from;
        if (from < 0) from = 0;
        if (to >= array.Count) to = array.Count - 1;
        for (var i = to; i >= from; i--) array.RemoveAt(i);
    }

    private static void SetChild(JsonNode target, string key, JsonNode? value)
    {
        if (target is JsonObject obj)
        {
            obj[key] = value;
            return;
        }

        if (target is JsonArray array &&
            int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            if (index >= 0 && index < array.Count) array[index] = value;
            else array.Add(value);
        }
    }

    private static JsonNode? GetChild(JsonNode target, string key)
    {
        if (target is JsonObject obj) return obj.TryGetPropertyValue(key, out var child) ? child : null;
        if (target is JsonArray array &&
            int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 && index < array.Count)
        {
            return array[index];
        }

        return null;
    }

    /// <summary>从还原后的 moment 中提取标题与两个时间字段。</summary>
    public static RawMoment ToRawMoment(JsonNode moment)
    {
        var raw = new RawMoment();
        if (moment is not JsonObject obj) return raw;

        if (obj.TryGetPropertyValue("title", out var titleNode) && titleNode is JsonValue titleValue &&
            titleValue.TryGetValue<string>(out var title))
        {
            raw.Title = title;
        }

        if (obj.TryGetPropertyValue("variables", out var vars) && vars is JsonObject variables)
        {
            if (variables.TryGetPropertyValue("startDate", out var startDate)) raw.StartDate = ReadNodeLong(startDate);
            if (variables.TryGetPropertyValue("timeStamp", out var timeStamp)) raw.Timestamp = ReadNodeLong(timeStamp);
        }

        return raw;
    }

    /// <summary>从还原后的 moment 中提取完整 variables（键 → 原始 JSON 文本）。</summary>
    public static Dictionary<string, string>? ExtractVariables(JsonNode moment)
    {
        if (moment is not JsonObject obj) return null;
        if (!obj.TryGetPropertyValue("variables", out var vars) || vars is not JsonObject variables)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var result = new Dictionary<string, string>(variables.Count, StringComparer.Ordinal);
        foreach (var pair in variables) result[pair.Key] = pair.Value?.ToJsonString() ?? "null";
        return result;
    }

    /// <summary>按文件区间读取单个 moment 并提取完整 variables。</summary>
    public static Dictionary<string, string>? LoadMomentVariables(
        string filePath, long start, long end, CancellationToken ct, out string? error)
    {
        error = null;
        try
        {
            var bytes = ReadRange(filePath, start, end);
            var node = JsonNode.Parse(bytes, null, new JsonDocumentOptions { MaxDepth = 512 });
            if (node is not JsonObject)
            {
                error = "该步骤的数据格式无法识别";
                return null;
            }

            return ExtractVariables(node);
        }
        catch (JsonException)
        {
            error = "该步骤的数据格式无法识别";
            return null;
        }
        catch (IOException ex)
        {
            error = "读取该步骤变量失败：" + ex.Message;
            return null;
        }
    }

    private static long ReadNodeLong(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return (long)d;
        if (value.TryGetValue<string>(out var s) &&
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static byte[] ReadRange(string filePath, long start, long end)
    {
        var length = (int)Math.Min(int.MaxValue, end - start);
        var buffer = new byte[length];
        using var fs = OpenRead(filePath);
        fs.Seek(start, SeekOrigin.Begin);
        var offset = 0;
        while (offset < length)
        {
            var read = fs.Read(buffer, offset, length - offset);
            if (read <= 0) break;
            offset += read;
        }

        return offset == length ? buffer : buffer[..offset];
    }
}
