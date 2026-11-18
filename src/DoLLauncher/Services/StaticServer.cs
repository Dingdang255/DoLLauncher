using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DoLLauncher.Core.Data;
using DoLLauncher.Core.Diagnostics;
using DoLLauncher.Core.Story;

namespace DoLLauncher.Services;

/// <summary>
/// 内置的极简 HTTP 静态服务器（TcpListener 实现，不需要 IIS / HttpListener 的 URL ACL，
/// 普通用户权限即可监听 127.0.0.1）。每个版本用固定端口，端口 = 独立浏览器 origin。
/// </summary>
public sealed class StaticServer : IDisposable
{
    private readonly string _root;
    private readonly string _rootFull;
    private readonly string _htmlName;
    private readonly string _versionName;
    private readonly string _versionFolder;
    private readonly object _sync = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private DateTime _lastProblemLoggedAt = DateTime.MinValue;
    private long _lastHeartbeatUtcTicks;

    /// <summary>当前还开着的页面（page id → 最后心跳时间 ticks）。游戏页与存档助手页各占一项。</summary>
    private readonly ConcurrentDictionary<string, long> _pageHeartbeats = new(StringComparer.Ordinal);
    private long _pageClosedUtcTicks;

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static byte[]? _pageScriptBytes;
    private static string? _pageScriptText;
    private static byte[]? _bridgeScriptBytes;
    private static int _gameLogScriptSeconds = -1;
    private static int _gameLogScriptQueue = -1;
    private static int _gameLogScriptFlush = -1;
    private static int _gameLogScriptDetail = -1;
    private static readonly object ScriptSync = new();

    /// <summary>
    /// 按当前设置生成注入脚本（心跳间隔、队列上限、flush 间隔变化时重建）。
    /// 存档库桥接脚本放在最前面，让导入尽早开始；日志采集与悬浮球随后。
    /// 生成后统一转成纯 ASCII，注入到任何编码的页面都不会被破坏。
    /// </summary>
    private static string PageScriptText()
    {
        var g = AppConfig.Load().General;
        var seconds = Math.Clamp(g.HeartbeatSeconds, 1, 60);
        var queue = Math.Clamp(g.LogQueueKb, 1, 128) * 1024;
        var flushMs = Math.Clamp(g.LogFlushMs, 200, 10000);
        var detail = LogService.IsDetailed(g.LogVerbosity) ? 1 : 0;
        lock (ScriptSync)
        {
            if (_pageScriptText != null && _gameLogScriptSeconds == seconds &&
                _gameLogScriptQueue == queue && _gameLogScriptFlush == flushMs &&
                _gameLogScriptDetail == detail)
                return _pageScriptText;

            _gameLogScriptSeconds = seconds;
            _gameLogScriptQueue = queue;
            _gameLogScriptFlush = flushMs;
            _gameLogScriptDetail = detail;
            var log = GameLogScript
                .Replace("__HEARTBEAT_MS__", (seconds * 1000).ToString())
                .Replace("__MAX_QUEUE__", queue.ToString())
                .Replace("__FLUSH_MS__", flushMs.ToString())
                .Replace("__DETAIL__", detail == 1 ? "true" : "false")
                .Replace("__RULES__", GameLogRules.RulesJson());
            _pageScriptText = AsciiEscape(IdbBridgeScript + log);
            _pageScriptBytes = Encoding.UTF8.GetBytes(_pageScriptText);
            _bridgeScriptBytes = Encoding.UTF8.GetBytes(AsciiEscape(IdbBridgeScript));
            return _pageScriptText;
        }
    }

    private static byte[] PageScriptBytes()
    {
        PageScriptText();
        lock (ScriptSync) return _pageScriptBytes!;
    }

    /// <summary>存档助手页只注入桥接脚本（它自己已有心跳与 UI，不需要悬浮球）。</summary>
    private static byte[] BridgeScriptBytes()
    {
        PageScriptText();
        lock (ScriptSync) return _bridgeScriptBytes!;
    }

    /// <summary>把非 ASCII 字符转成 \uXXXX：脚本只含 ASCII，注入任意编码的页面都不会乱码。</summary>
    private static string AsciiEscape(string text)
    {
        var sb = new StringBuilder(text.Length + 64);
        foreach (var c in text)
        {
            if (c <= 0x7F) sb.Append(c);
            else sb.Append("\\u").Append(((int)c).ToString("x4"));
        }
        return sb.ToString();
    }
    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int Port { get; }

    private readonly object _storySimLock = new();
    private string? _storySimCommand;
    private string _storySimStatus = "{\"state\":\"idle\",\"message\":\"尚未开始\"}";

    /// <summary>后台执行旧镜像回滚/迁移的任务；存档接口在读写前等待它完成。</summary>
    private Task _dataPrepareTask = Task.CompletedTask;

    public StaticServer(string root, int port, string htmlName, string versionName, string versionFolder)
    {
        _root = Path.GetFullPath(root);
        _rootFull = _root.TrimEnd(Path.DirectorySeparatorChar);
        _htmlName = htmlName;
        _versionName = versionName;
        _versionFolder = versionFolder;
        Port = port;
    }

    public bool IsListening
    {
        get
        {
            lock (_sync) return _listener != null;
        }
    }

    public string GameUrl => $"http://127.0.0.1:{Port}/";
    public string SaveHelperUrl => $"http://127.0.0.1:{Port}/__launcher/saves.html";

    /// <summary>最近一次收到页面心跳的时间（UTC）；页面还没发过心跳时为 null。</summary>
    public DateTime? LastHeartbeatUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastHeartbeatUtcTicks);
            return ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>是否还有页面开着（游戏页 / 存档助手页）。</summary>
    public bool HasPages => !_pageHeartbeats.IsEmpty;

    /// <summary>最后一个页面主动上报关闭的时间（UTC）；页面重新发心跳时清空。</summary>
    public DateTime? PageClosedUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _pageClosedUtcTicks);
            return ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public void Start()
    {
        // 同一端口被另一个版本接管：标记为待清理，避免看到上一个版本的存档与配置
        if (IdbMirrorStore.ClaimPort(_root, Port) == PortClaim.ChangedVersion)
        {
            IdbMirrorStore.WriteCleanMarker(Port, "端口被另一个版本接管");
            LogThrottled($"端口 {Port} 已切换到版本 {_versionName}，首次打开会清空上一个版本留下的浏览器数据。");
        }
        lock (_sync)
        {
            if (_listener != null) return;
            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start(128);
            _listener = listener;
            _cts = new CancellationTokenSource();
            // 心跳从 0 开始：页面还没真正跑起来之前不算“已连接”，避免误判为关闭
            Interlocked.Exchange(ref _lastHeartbeatUtcTicks, 0);
        }
        // 旧镜像回滚/迁移会读写数百 MB，放到后台做，避免启动器界面在拉起浏览器时卡住。
        // 存档接口会等这个任务完成后再读写槽位。
        _dataPrepareTask = Task.Run(PrepareDataStore);
        _ = Task.Run(() => AcceptLoop(_cts!.Token));
    }

    /// <summary>旧版整库镜像的回滚与迁移；失败只记录，不影响服务器继续工作。</summary>
    private void PrepareDataStore()
    {
        try
        {
            IdbMirrorStore.RecoverPending(_root);
            if (!SlotSaveStore.HasLegacyMirror(_root)) return;
            SlotSaveStore.MigrateLegacyMirror(_root, out var migrateMessage);
            if (!string.IsNullOrWhiteSpace(migrateMessage))
                LogThrottled($"旧镜像迁移（{_versionName}）：{migrateMessage}");
        }
        catch (Exception ex)
        {
            LogThrottled($"旧镜像迁移失败（{_versionName}）：{ex.Message}");
        }
    }

    private async Task WaitForDataStoreAsync(CancellationToken ct)
    {
        var task = _dataPrepareTask;
        if (task.IsCompleted) return;
        await task.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Stop()
    {
        lock (_sync)
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts = null;
        }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, ct);
                if (request == null) return;
                await RouteAsync(stream, request.Value.Header, request.Value.Leftover, ct);
            }
            catch
            {
                // 单个请求失败不应该影响服务器
            }
        }
    }

    private async Task RouteAsync(NetworkStream stream, string header, byte[] leftover, CancellationToken ct)
    {
        var lines = header.Split("\r\n");
        if (lines.Length == 0) return;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return;

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var query = string.Empty;
        var q = target.IndexOf('?');
        if (q >= 0)
        {
            query = target[(q + 1)..];
            target = target[..q];
        }

        string path;
        try { path = Uri.UnescapeDataString(target); }
        catch { path = target; }

        if (path.StartsWith("/__launcher/api/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleApiAsync(stream, method, path, query, lines, leftover, ct);
            return;
        }

        if (method != "GET" && method != "HEAD")
        {
            await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                "405 Method Not Allowed", method, ct);
            return;
        }

        if (path == "/" || path.Length == 0) path = "/" + _htmlName;

        if (path.Equals("/__launcher/saves.html", StringComparison.OrdinalIgnoreCase))
        {
            var autoOnly = query.Split('&').Any(x => x.Equals("auto=1", StringComparison.OrdinalIgnoreCase));
            var body = Encoding.UTF8.GetBytes(SaveHelperPage.Build(_versionName, autoOnly) +
                                              "<script>window.__dolIdbNoBoot=1;</script>" +
                                              Encoding.UTF8.GetString(BridgeScriptBytes()));
            await WriteBytesAsync(stream, 200, "OK", "text/html; charset=utf-8", body, method, ct);
            return;
        }

        var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_rootFull, relative));
        }
        catch
        {
            await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8", "400 Bad Request", method, ct);
            return;
        }

        if (!(full.Equals(_rootFull, StringComparison.OrdinalIgnoreCase) ||
              full.StartsWith(_rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            await WriteTextAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", "403 Forbidden", method, ct);
            return;
        }

        if (!File.Exists(full))
        {
            var html = "<!DOCTYPE html><meta charset=\"utf-8\"><body style=\"background:#191b20;color:#e8eaf0;" +
                       "font-family:Microsoft YaHei UI,Segoe UI,sans-serif;padding:40px\">" +
                       "<h2>404</h2><p>文件不存在：" + WebUtility.HtmlEncode(path) + "</p>" +
                       "<p><a style=\"color:#7fb3ff\" href=\"/\">返回游戏</a></p></body>";
            await WriteBytesAsync(stream, 404, "Not Found", "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(html), method, ct);
            return;
        }

        if (File.Exists(full) &&
            full.Equals(Path.Combine(_rootFull, _htmlName), StringComparison.OrdinalIgnoreCase))
        {
            await WriteInjectedHtmlAsync(stream, full, method, ct);
            return;
        }

        await WriteFileAsync(stream, full, method, lines, ct);
    }

    /// <summary>存档助手页用到的备份接口：列表、读取、写入、删除。</summary>
    private async Task HandleApiAsync(NetworkStream stream, string method, string path, string query,
        string[] lines, byte[] leftover, CancellationToken ct)
    {
        var q = ParseQuery(query);
        var file = q.GetValueOrDefault("file", "");

        if (path.Equals("/__launcher/api/heartbeat", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "POST")
            {
                await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    "405 Method Not Allowed", method, ct);
                return;
            }

            var page = q.GetValueOrDefault("page", "main");
            var nowTicks = DateTime.UtcNow.Ticks;
            _pageHeartbeats[page] = nowTicks;
            Interlocked.Exchange(ref _lastHeartbeatUtcTicks, nowTicks);
            Interlocked.Exchange(ref _pageClosedUtcTicks, 0);
            // 必须 200 + JSON：204 / 空体在部分浏览器里会被当作无效响应
            var json = Encoding.UTF8.GetBytes("{\"ok\":true}");
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", json, method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/page-closed", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            var page = q.GetValueOrDefault("page", "main");
            _pageHeartbeats.TryRemove(page, out _);
            if (_pageHeartbeats.IsEmpty)
            {
                Interlocked.Exchange(ref _pageClosedUtcTicks, DateTime.UtcNow.Ticks);
            }
            var json = Encoding.UTF8.GetBytes("{\"ok\":true}");
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", json, method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/backup-list", StringComparison.OrdinalIgnoreCase))
        {
            var json = BackupService.ListJson(_versionFolder);
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(json), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/backup", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "POST")
            {
                var body = await ReadBodyAsync(stream, leftover, lines, ct);
                var auto = q.GetValueOrDefault("auto", "0") == "1";
                try
                {
                    var name = BackupService.SaveBrowserBackup(_versionFolder, file, body, auto);
                    var json = JsonSerializer.Serialize(new { ok = true, name });
                    await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(json), method, ct);
                }
                catch (Exception ex)
                {
                    var json = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
                    await WriteBytesAsync(stream, 500, "Internal Server Error", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(json), method, ct);
                }
                return;
            }

            var text = BackupService.ReadText(_versionFolder, file, saves: true);
            if (text == null)
            {
                await WriteTextAsync(stream, 404, "Not Found", "text/plain; charset=utf-8",
                    "404 Not Found", method, ct);
                return;
            }
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(text), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/backup-delete", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            var body = await ReadBodyAsync(stream, leftover, lines, ct);
            var ok = BackupService.Delete(_versionFolder, file, saves: true);
            var json = JsonSerializer.Serialize(new { ok });
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(json), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/story-sim", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "POST")
            {
                await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    "405 Method Not Allowed", method, ct);
                return;
            }

            string body;
            try
            {
                body = await ReadBodyAsync(stream, leftover, lines, ct);
            }
            catch (Exception ex)
            {
                LogThrottled($"模拟结果上报读取失败（{_versionName}）：{ex.Message}");
                return;
            }

            try
            {
                var saved = StoryBranchReader.SaveLatest(_root, body);
                var json = JsonSerializer.Serialize(new { ok = true, file = saved });
                await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(json), method, ct);
            }
            catch (Exception ex)
            {
                LogThrottled($"保存模拟结果失败（{_versionName}）：{ex.Message}");
                var json = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
                await WriteBytesAsync(stream, 400, "Bad Request", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(json), method, ct);
            }
            return;
        }

        if (path.Equals("/__launcher/api/story-sim/run", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "POST")
            {
                string body;
                try
                {
                    body = await ReadBodyAsync(stream, leftover, lines, ct);
                }
                catch (Exception ex)
                {
                    LogThrottled($"模拟命令读取失败（{_versionName}）：{ex.Message}");
                    return;
                }

                if (body.Length > 4000)
                {
                    await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                        "命令过大", method, ct);
                    return;
                }

                lock (_storySimLock)
                {
                    _storySimCommand = body;
                    _storySimStatus = "{\"state\":\"queued\",\"message\":\"已加入队列，等待游戏页面接收\"}";
                }
                var json = JsonSerializer.Serialize(new { ok = true });
                await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(json), method, ct);
                return;
            }

            if (method == "GET")
            {
                string? command;
                lock (_storySimLock)
                {
                    command = _storySimCommand;
                    _storySimCommand = null;
                }
                var json = "{\"ok\":true,\"command\":" + (command ?? "null") + "}";
                await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(json), method, ct);
                return;
            }

            await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                "405 Method Not Allowed", method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/story-sim/status", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "POST")
            {
                string body;
                try
                {
                    body = await ReadBodyAsync(stream, leftover, lines, ct);
                }
                catch (Exception ex)
                {
                    LogThrottled($"模拟状态读取失败（{_versionName}）：{ex.Message}");
                    return;
                }

                if (body.Length > 4000) body = body[..4000];
                lock (_storySimLock) { _storySimStatus = body; }
                var json = JsonSerializer.Serialize(new { ok = true });
                await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(json), method, ct);
                return;
            }

            if (method == "GET")
            {
                string status;
                lock (_storySimLock) { status = _storySimStatus; }
                var json = "{\"ok\":true,\"status\":" + status + "}";
                await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(json), method, ct);
                return;
            }

            await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                "405 Method Not Allowed", method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/saves/manifest", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "GET" && method != "HEAD")
            {
                await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    "405 Method Not Allowed", method, ct);
                return;
            }

            await WaitForDataStoreAsync(ct);
            var manifest = SlotSaveStore.ReadManifest(_root);
            var saveDb = string.IsNullOrWhiteSpace(manifest.SaveDb) ? "degrees-of-lewdity" : manifest.SaveDb;
            var payload = new StringBuilder();
            payload.Append("{\"ok\":true,\"format\":\"").Append(SlotSaveStore.Format).Append("\",\"saveDb\":")
                .Append(JsonSerializer.Serialize(saveDb)).Append(",\"slots\":[");
            for (var i = 0; i < manifest.Slots.Count; i++)
            {
                if (i > 0) payload.Append(',');
                var entry = manifest.Slots[i];
                payload.Append("{\"slot\":").Append(entry.Slot)
                    .Append(",\"saveBytes\":").Append(entry.SaveBytes)
                    .Append(",\"detailsBytes\":").Append(entry.DetailsBytes)
                    .Append(",\"updatedAt\":").Append(JsonSerializer.Serialize(entry.UpdatedAt ?? ""))
                    .Append(",\"sha256\":").Append(JsonSerializer.Serialize(entry.Sha256 ?? ""))
                    .Append('}');
            }
            payload.Append("]}");
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(payload.ToString()), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/saves/slot", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "GET" || method == "HEAD")
            {
                if (!int.TryParse(q.GetValueOrDefault("slot", ""), out var querySlot) || querySlot < 0)
                {
                    await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                        "槽位号无效。", method, ct);
                    return;
                }

                await WaitForDataStoreAsync(ct);
                if (!SlotSaveStore.TryReadSlot(_root, querySlot, out var saveJson, out var detailsJson))
                {
                    var missing = "{\"ok\":false,\"slot\":" + querySlot + ",\"reason\":\"槽位不存在或读取失败\"}";
                    await WriteBytesAsync(stream, 404, "Not Found", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(missing), method, ct);
                    return;
                }

                var payload = new StringBuilder();
                payload.Append("{\"ok\":true,\"slot\":").Append(querySlot).Append(",\"save\":")
                    .Append(string.IsNullOrWhiteSpace(saveJson) ? "null" : saveJson)
                    .Append(",\"details\":")
                    .Append(string.IsNullOrWhiteSpace(detailsJson) ? "null" : detailsJson)
                    .Append('}');
                await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(payload.ToString()), method, ct);
                return;
            }

            if (method != "POST")
            {
                await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    "405 Method Not Allowed", method, ct);
                return;
            }

            if (IsBodyTooLarge(lines))
            {
                await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                    "请求体超过 32 MB，已拒绝。", method, ct);
                return;
            }

            string body;
            try
            {
                body = await ReadBodyAsync(stream, leftover, lines, ct);
            }
            catch (Exception ex)
            {
                LogThrottled($"槽位写入请求读取失败（{_versionName}）：{ex.Message}");
                return;
            }

            await WaitForDataStoreAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                        "请求体不是 JSON 对象。", method, ct);
                    return;
                }

                var op = root.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String
                    ? opElement.GetString()
                    : null;
                if (!root.TryGetProperty("slot", out var slotElement) || !slotElement.TryGetInt32(out var slot) || slot < 0)
                {
                    await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                        "槽位号无效。", method, ct);
                    return;
                }

                var saveDb = root.TryGetProperty("saveDb", out var dbElement) && dbElement.ValueKind == JsonValueKind.String
                    ? dbElement.GetString() ?? ""
                    : "";

                long bytes = 0;
                if (string.Equals(op, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    SlotSaveStore.DeleteSlot(_root, slot);
                }
                else if (string.Equals(op, "put", StringComparison.OrdinalIgnoreCase))
                {
                    if (!root.TryGetProperty("save", out var saveElement) || saveElement.ValueKind != JsonValueKind.Object)
                    {
                        await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                            "缺少存档内容。", method, ct);
                        return;
                    }

                    string? detailsJson = null;
                    if (root.TryGetProperty("details", out var detailsElement) &&
                        detailsElement.ValueKind != JsonValueKind.Null &&
                        detailsElement.ValueKind != JsonValueKind.Undefined)
                    {
                        if (detailsElement.ValueKind != JsonValueKind.Object)
                        {
                            await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                                "存档详情不是 JSON 对象。", method, ct);
                            return;
                        }
                        detailsJson = detailsElement.GetRawText();
                    }

                    var saveText = saveElement.GetRawText();
                    SlotSaveStore.WriteSlot(_root, slot, saveText, detailsJson, saveDb);
                    bytes = Encoding.UTF8.GetByteCount(saveText);
                }
                else
                {
                    await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                        "op 必须是 put 或 delete。", method, ct);
                    return;
                }

                var json = JsonSerializer.Serialize(new { ok = true, slot, bytes });
                await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(json), method, ct);
            }
            catch (JsonException)
            {
                await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                    "请求体不是有效的 JSON。", method, ct);
            }
            catch (InvalidOperationException ex)
            {
                await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                    ex.Message, method, ct);
            }
            catch (Exception ex)
            {
                LogThrottled($"写入槽位失败（{_versionName}）：{ex.Message}");
                var json = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
                await WriteBytesAsync(stream, 500, "Internal Server Error", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(json), method, ct);
            }
            return;
        }

        if (path.Equals("/__launcher/api/saves/cache-clear-done", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "POST")
            {
                await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    "405 Method Not Allowed", method, ct);
                return;
            }

            var count = int.TryParse(q.GetValueOrDefault("count", ""), out var parsedCount) && parsedCount >= 0
                ? parsedCount
                : 0;
            var ok = true;
            try
            {
                SlotSaveStore.CompleteCacheClear(_root);
                LogThrottled($"已清空 {count} 个 Mod 缓存库（保留存档库）（{_versionName}）。");
            }
            catch (Exception ex)
            {
                ok = false;
                LogThrottled($"完成 Mod 缓存清理失败（{_versionName}）：{ex.Message}");
            }
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(ok ? "{\"ok\":true}" : "{\"ok\":false}"), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/saves/clear", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "POST")
            {
                await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    "405 Method Not Allowed", method, ct);
                return;
            }

            if (IsBodyTooLarge(lines))
            {
                await WriteTextAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                    "请求体超过 32 MB，已拒绝。", method, ct);
                return;
            }

            string body;
            try
            {
                body = await ReadBodyAsync(stream, leftover, lines, ct);
            }
            catch (Exception ex)
            {
                LogThrottled($"清空槽位请求读取失败（{_versionName}）：{ex.Message}");
                return;
            }

            var saveDb = "";
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("saveDb", out var dbElement) &&
                        dbElement.ValueKind == JsonValueKind.String)
                        saveDb = dbElement.GetString() ?? "";
                }
                catch (JsonException)
                {
                    // 忽略非法请求体，按空存档库名清空
                }
            }

            await WaitForDataStoreAsync(ct);
            SlotSaveStore.Clear(_root, saveDb);
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes("{\"ok\":true}"), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/idb-mirror", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "POST")
            {
                // 保留路径以兼容旧页面，但不再接收整库镜像
                await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes("{\"ok\":false,\"reason\":\"legacy mirror disabled\"}"), method, ct);
                return;
            }

            // GET：只下发待导入的存档库内容与「删除版本后待清理」标记，不再回推旧整库镜像
            await WaitForDataStoreAsync(ct);
            var boot = IdbMirrorStore.LoadForBoot(_root, Port);
            string? importText = boot.Import;
            if (!string.IsNullOrWhiteSpace(importText))
            {
                importText = SlotSaveStore.FilterPayloadToSaveDomain(importText, out _);
                if (importText == null)
                    LogThrottled($"待导入镜像无法识别存档库，已忽略（{_versionName}）。");
            }

            var payload = new StringBuilder();
            payload.Append("{\"ok\":true,\"clean\":").Append(boot.Clean ? "true" : "false");
            payload.Append(",\"mirror\":null");
            payload.Append(",\"import\":").Append(importText ?? "null");
            payload.Append('}');
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(payload.ToString()), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/idb-clean-done", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            IdbMirrorStore.ClearCleanMarker(Port);
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes("{\"ok\":true}"), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/idb-import-done", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            IdbMirrorStore.MarkImportDone(_root);
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes("{\"ok\":true}"), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/game-fatal", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            string body;
            try
            {
                body = await ReadBodyAsync(stream, leftover, lines, ct);
            }
            catch (Exception ex)
            {
                LogThrottled($"致命日志上报读取失败（{_versionName}）：{ex.Message}");
                return;
            }
            GameLogService.NoteFatal(_root, _versionName, body);
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes("{\"ok\":true}"), method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/game-log-status", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "GET" && method != "HEAD")
            {
                await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    "405 Method Not Allowed", method, ct);
                return;
            }

            byte[] statusJson;
            try
            {
                var status = GameLogService.GetStatus(_root, q.GetValueOrDefault("sid", ""));
                var payload = new { ok = true, status };
                statusJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CamelJson));
            }
            catch (Exception ex)
            {
                LogThrottled($"日志状态端点异常（{_versionName}）：{ex.Message}");
                statusJson = Encoding.UTF8.GetBytes("{\"ok\":false}");
            }
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", statusJson, method, ct);
            return;
        }

        if (path.Equals("/__launcher/api/game-log", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "POST")
            {
                await WriteTextAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    "405 Method Not Allowed", method, ct);
                return;
            }

            string body;
            try
            {
                body = await ReadBodyAsync(stream, leftover, lines, ct);
            }
            catch (Exception ex)
            {
                // 浏览器中断连接时无法再回响应，只记录原因
                LogThrottled($"游戏日志上报读取失败（{_versionName}）：{ex.Message}");
                return;
            }

            var sid = q.GetValueOrDefault("sid", "");
            if (q.GetValueOrDefault("stop", "") == "1")
            {
                int.TryParse(q.GetValueOrDefault("fails", ""), out var fails);
                GameLogService.NoteReportStop(_root, sid, q.GetValueOrDefault("reason", ""), fails);
            }
            GameLogService.Append(_root, sid, body);

            // 必须 200 + JSON：204 / 空体会被浏览器判为无效响应，fetch 直接 reject
            var json = Encoding.UTF8.GetBytes("{\"ok\":true}");
            await WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", json, method, ct);
            return;
        }

        await WriteTextAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "404 Not Found", method, ct);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query.Length == 0) return result;
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair[..eq] : pair;
            var value = eq >= 0 ? pair[(eq + 1)..] : "";
            try
            {
                key = Uri.UnescapeDataString(key.Replace('+', ' '));
                value = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            catch
            {
                // 保留原值
            }
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }

    private const long MaxSaveApiBodyBytes = 32L * 1024 * 1024;

    private static bool IsBodyTooLarge(string[] lines)
    {
        var header = GetHeader(lines, "content-length");
        return header != null && long.TryParse(header, out var length) && length > MaxSaveApiBodyBytes;
    }

    private static async Task<string> ReadBodyAsync(NetworkStream stream, byte[] leftover, string[] lines,
        CancellationToken ct)
    {
        long length = 0;
        var header = GetHeader(lines, "content-length");
        var hasLength = header != null;
        if (hasLength) long.TryParse(header, out length);

        using var ms = new MemoryStream();
        if (leftover.Length > 0) ms.Write(leftover);

        var buffer = new byte[8192];
        if (hasLength)
        {
            var remaining = length - leftover.Length;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read <= 0) break;
                ms.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        else
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read <= 0) break;
                ms.Write(buffer, 0, read);
            }
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task WriteInjectedHtmlAsync(NetworkStream stream, string full, string method, CancellationToken ct)
    {
        byte[] body;
        try
        {
            // 一次性读入内存后注入：Content-Length 与最终字节数必然一致，不会被截断
            var clearCaches = SlotSaveStore.HasCacheClearRequest(_root);
            body = BuildInjectedHtml(await File.ReadAllBytesAsync(full, ct), clearCaches);
        }
        catch (Exception ex)
        {
            // 注入失败时退回原始页面，保证游戏能打开；同时把原因写进启动器日志
            LogThrottled($"注入日志采集脚本失败（{_versionName}）：{Path.GetFileName(full)} - {ex.Message}");
            try
            {
                await WriteFileAsync(stream, full, method, Array.Empty<string>(), ct);
            }
            catch (Exception inner)
            {
                LogThrottled($"读取游戏页面失败（{_versionName}）：{inner.Message}");
                try
                {
                    await WriteTextAsync(stream, 500, "Internal Server Error", "text/plain; charset=utf-8",
                        "500 Internal Server Error", method, ct);
                }
                catch
                {
                    // 连接已断开
                }
            }
            return;
        }

        var head = "HTTP/1.1 200 OK\r\n" +
                   "Content-Type: text/html; charset=utf-8\r\n" +
                   "Content-Length: " + body.Length + "\r\n" +
                   "Cache-Control: no-cache\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        if (method == "HEAD") return;
        await stream.WriteAsync(body, ct);
    }

    /// <summary>
    /// 把采集脚本注入 HTML 字节流。<br/>
    /// 1. 采集脚本只用 ASCII，按 UTF-8 编码后与任何单字节兼容编码（UTF-8 / GBK 等）都不冲突；<br/>
    /// 2. UTF-8 BOM 之前绝不插入，避免 BOM 不在文件开头导致整页按错误编码解析；<br/>
    /// 3. UTF-16 页面先转成 UTF-8 再注入（响应头已声明 utf-8）。
    /// </summary>
    private static byte[] BuildInjectedHtml(byte[] source, bool clearCaches)
    {
        if (source.Length >= 2 &&
            ((source[0] == 0xFF && source[1] == 0xFE) || (source[0] == 0xFE && source[1] == 0xFF)))
        {
            var encoding = source[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            var text = encoding.GetString(source, 2, source.Length - 2);
            return Utf8NoBom.GetBytes(InjectIntoText(text, clearCaches));
        }

        var bom = source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF ? 3 : 0;
        var offset = FindInsertOffset(source, bom);
        var script = PageScriptBytes();
        // 清缓存标记是同步脚本：必须早于游戏脚本执行
        var prefixText = clearCaches ? "<script>window.__DOL_CLEAR_CACHES__=true;</script>" : "";
        var prefix = prefixText.Length == 0 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(prefixText);

        var result = new byte[source.Length + prefix.Length + script.Length];
        var pos = 0;
        Buffer.BlockCopy(source, 0, result, pos, offset);
        pos += offset;
        if (prefix.Length > 0)
        {
            Buffer.BlockCopy(prefix, 0, result, pos, prefix.Length);
            pos += prefix.Length;
        }
        Buffer.BlockCopy(script, 0, result, pos, script.Length);
        pos += script.Length;
        Buffer.BlockCopy(source, offset, result, pos, source.Length - offset);
        return result;
    }

    private static string InjectIntoText(string text, bool clearCaches)
    {
        var insert = IndexOfTagText(text, "head");
        if (insert < 0) insert = IndexOfTagText(text, "html");
        if (insert < 0) insert = IndexOfTagText(text, "!doctype");
        if (insert < 0) insert = 0;
        var prefix = clearCaches ? "<script>window.__DOL_CLEAR_CACHES__=true;</script>" : "";
        return text.Insert(insert, prefix + PageScriptText());
    }

    /// <summary>在 &lt;head&gt; 之后插入；找不到依次退回 &lt;html&gt;、&lt;!DOCTYPE&gt;、文件开头（始终在 BOM 之后）。</summary>
    private static int FindInsertOffset(byte[] data, int start)
    {
        var head = IndexOfTagBytes(data, start, "head");
        if (head >= 0) return head;
        var html = IndexOfTagBytes(data, start, "html");
        if (html >= 0) return html;
        var doctype = IndexOfTagBytes(data, start, "!doctype");
        if (doctype >= 0) return doctype;
        return start;
    }

    /// <summary>返回标签结束 "&gt;" 之后的位置；不是完整标签（如 &lt;header&gt;）会跳过。</summary>
    private static int IndexOfTagBytes(byte[] data, int start, string name)
    {
        for (var i = start; i + name.Length < data.Length; i++)
        {
            if (data[i] != (byte)'<') continue;
            if (!MatchAscii(data, i + 1, name)) continue;
            var after = i + 1 + name.Length;
            if (after < data.Length && !IsTagBoundary((char)data[after])) continue;
            for (var j = after; j < data.Length; j++)
            {
                if (data[j] == (byte)'>') return j + 1;
            }
            return -1;
        }
        return -1;
    }

    private static int IndexOfTagText(string text, string name)
    {
        var index = 0;
        while (index < text.Length)
        {
            index = text.IndexOf("<" + name, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return -1;
            var after = index + 1 + name.Length;
            if (after < text.Length && IsTagBoundary(text[after]))
            {
                var gt = text.IndexOf('>', after);
                if (gt > 0) return gt + 1;
            }
            index++;
        }
        return -1;
    }

    private static bool MatchAscii(byte[] data, int offset, string name)
    {
        if (offset + name.Length > data.Length) return false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = (char)data[offset + i];
            if (char.ToLowerInvariant(c) != char.ToLowerInvariant(name[i])) return false;
        }
        return true;
    }

    private static bool IsTagBoundary(char c) =>
        c == '>' || c == '/' || char.IsWhiteSpace(c);

    /// <summary>限频写启动器日志，避免坏页面反复刷新把 launcher.log 写爆。</summary>
    private void LogThrottled(string message)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (now - _lastProblemLoggedAt < TimeSpan.FromMinutes(1)) return;
            _lastProblemLoggedAt = now;
        }
        LogService.Append(message);
    }

    /// <summary>
    /// 注入到游戏页 / 存档助手页的存档库桥接脚本：
    /// 只处理存档库的 saves / details 表，按槽位从磁盘合并与写回；
    /// 删除版本后首次打开会先清空该 origin 的 IndexedDB / localStorage / 缓存。
    /// </summary>
    private const string IdbBridgeScript = """
<script>
(function () {
  try {
    if (window.__dolIdbBridge) return;
    window.__dolIdbBridge = true;

    var API = '/__launcher/api/';
    var DEFAULT_SAVE_DB = 'degrees-of-lewdity';
    var SAVE_DB = '';
    var MANIFEST = null;
    var DIRTY_SLOTS = new Set();
    var DELETED_SLOTS = new Set();
    var CLEAR_PENDING = false;
    var FLUSH_TIMER = null;
    var FLUSHING = false;
    var IMPORTING = false;
    var MAX_BEACON = 60000;

    function note(text) {
      try { console.log('[DoL IDB] ' + text); } catch (e) { }
    }

    function openDb(name, version, stores) {
      return new Promise(function (resolve, reject) {
        var req = version ? indexedDB.open(name, version) : indexedDB.open(name);
        req.onupgradeneeded = function () {
          var db = req.result;
          var list = stores || [];
          for (var i = 0; i < list.length; i++) {
            if (!db.objectStoreNames.contains(list[i].name)) {
              db.createObjectStore(list[i].name, { keyPath: list[i].keyPath });
            }
          }
        };
        req.onsuccess = function () { resolve(req.result); };
        req.onerror = function () {
          var err = req.error;
          if (version && err && err.name === 'VersionError') {
            try {
              var retry = indexedDB.open(name);
              retry.onsuccess = function () { resolve(retry.result); };
              retry.onerror = function () { reject(err); };
              return;
            } catch (e3) { }
          }
          reject(err);
        };
        req.onblocked = function () { reject(new Error('database blocked by another tab')); };
      });
    }

    function reqCursor(store) {
      return new Promise(function (resolve, reject) {
        var out = [];
        var r = store.openCursor();
        r.onsuccess = function () {
          var c = r.result;
          if (!c) { resolve(out); return; }
          out.push({ key: c.primaryKey, value: c.value });
          c.continue();
        };
        r.onerror = function () { reject(r.error); };
      });
    }

    function txDone(tx) {
      return new Promise(function (resolve, reject) {
        tx.oncomplete = function () { resolve(); };
        tx.onerror = function () { reject(tx.error); };
        tx.onabort = function () { reject(tx.error); };
      });
    }

    function listDbs() {
      try {
        if (indexedDB.databases) {
          return indexedDB.databases().then(function (arr) {
            return arr.map(function (d) { return { name: d.name, version: d.version }; })
                      .filter(function (d) { return !!d.name; });
          });
        }
      } catch (e) { }
      return Promise.resolve([]);
    }

    async function pickExistingSaveDb() {
      try {
        if (!indexedDB.databases) return null;
        var arr = await indexedDB.databases();
        var names = (arr || []).map(function (d) { return d && d.name ? String(d.name) : ''; });
        if (names.indexOf(DEFAULT_SAVE_DB) >= 0) return DEFAULT_SAVE_DB;
        if (names.indexOf('idb') >= 0) return 'idb';
        return null;
      } catch (e) {
        return null;
      }
    }

    function resolveSaveDb(fromManifest) {
      if (fromManifest) return String(fromManifest);
      if (MANIFEST && MANIFEST.saveDb) return String(MANIFEST.saveDb);
      if (SAVE_DB) return SAVE_DB;
      var fromGame = '';
      try {
        if (window.idb && window.idb.dbName) fromGame = String(window.idb.dbName);
      } catch (e) { }
      if (fromGame && fromGame !== 'idb') return fromGame;
      if (fromGame === 'idb' && !MANIFEST) return fromGame;
      return DEFAULT_SAVE_DB;
    }

    function openSaveDb() {
      return openDb(SAVE_DB || DEFAULT_SAVE_DB, undefined, [
        { name: 'saves', keyPath: 'slot' },
        { name: 'details', keyPath: 'slot' }
      ]);
    }

    function readStoreKeys(db, storeName) {
      return new Promise(function (resolve) {
        var out = [];
        try {
          var req = db.transaction(storeName, 'readonly').objectStore(storeName).openCursor();
          req.onsuccess = function () {
            var c = req.result;
            if (!c) { resolve(out); return; }
            var v = c.value;
            var slot = v && v.slot !== undefined ? Number(v.slot) : Number(c.primaryKey);
            if (isFinite(slot) && slot >= 0) out.push(slot);
            c.continue();
          };
          req.onerror = function () { resolve(out); };
        } catch (e) { resolve(out); }
      });
    }

    function getRecord(db, storeName, slot) {
      return new Promise(function (resolve) {
        try {
          var req = db.transaction(storeName, 'readonly').objectStore(storeName).get(slot);
          req.onsuccess = function () {
            resolve(req.result === undefined ? null : req.result);
          };
          req.onerror = function () { resolve(null); };
        } catch (e) { resolve(null); }
      });
    }

    function putRecord(db, storeName, value) {
      return new Promise(function (resolve) {
        try {
          var tx = db.transaction(storeName, 'readwrite');
          tx.objectStore(storeName).put(value);
          txDone(tx).then(function () { resolve(true); }, function () { resolve(false); });
        } catch (e) { resolve(false); }
      });
    }

    function replacer(key, value) {
      if (value instanceof ArrayBuffer) return { __t: 'ab', d: Array.from(new Uint8Array(value)) };
      if (value && ArrayBuffer.isView(value)) return { __t: 'ta', d: Array.from(new Uint8Array(value.buffer)) };
      return value;
    }

    function reviver(key, value) {
      if (value && typeof value === 'object' && value.__t === 'ab' && Array.isArray(value.d)) {
        return new Uint8Array(value.d).buffer;
      }
      if (value && typeof value === 'object' && value.__t === 'ta' && Array.isArray(value.d)) {
        return new Uint8Array(value.d);
      }
      return value;
    }

    async function importSaveDomain(payload) {
      if (!payload || payload.format !== 'dol-saves/1' || !Array.isArray(payload.dbs)) return 0;
      var written = 0;
      for (var i = 0; i < payload.dbs.length; i++) {
        var d = payload.dbs[i];
        if (!d || !Array.isArray(d.stores)) continue;
        var db;
        try {
          db = await openSaveDb();
        } catch (e) {
          note('open save db failed: ' + (e && e.message));
          return written;
        }
        try {
          for (var j = 0; j < d.stores.length; j++) {
            var s = d.stores[j];
            if (!s || (s.name !== 'saves' && s.name !== 'details')) continue;
            var recs = Array.isArray(s.records) ? s.records : [];
            for (var k = 0; k < recs.length; k++) {
              var r = recs[k];
              var value = r && r.value !== undefined ? r.value : null;
              if (!value || value.slot === undefined || value.slot === null) continue;
              if (await putRecord(db, s.name, value)) written++;
            }
          }
        } finally {
          try { db.close(); } catch (e) { }
        }
      }
      return written;
    }

    async function mergeFromDisk(db) {
      if (!MANIFEST || !Array.isArray(MANIFEST.slots)) return;
      var savesKeys = await readStoreKeys(db, 'saves');
      var detailsKeys = await readStoreKeys(db, 'details');
      var hasSave = {};
      var hasDetails = {};
      var i;
      for (i = 0; i < savesKeys.length; i++) hasSave[savesKeys[i]] = true;
      for (i = 0; i < detailsKeys.length; i++) hasDetails[detailsKeys[i]] = true;

      var inManifest = {};
      for (i = 0; i < MANIFEST.slots.length; i++) {
        var entry = MANIFEST.slots[i] || {};
        var slot = Number(entry.slot);
        if (!isFinite(slot) || slot < 0) continue;
        inManifest[slot] = true;
        var needSave = !hasSave[slot];
        var needDetails = !hasDetails[slot] && Number(entry.detailsBytes) > 0;
        if (!needSave && !needDetails) continue;

        try {
          var res = await fetch(API + 'saves/slot?slot=' + slot, { cache: 'no-store' });
          if (!res.ok) {
            note('read slot ' + slot + ' failed: HTTP ' + res.status);
            continue;
          }
          var data = JSON.parse(await res.text(), reviver);
          if (!data || data.ok !== true) continue;
          if (needSave && data.save) await putRecord(db, 'saves', data.save);
          if (needDetails && data.details) await putRecord(db, 'details', data.details);
        } catch (e) {
          note('merge slot ' + slot + ' failed: ' + (e && e.message));
        }
      }

      for (i = 0; i < savesKeys.length; i++) {
        var key = savesKeys[i];
        if (!inManifest[key]) DIRTY_SLOTS.add(key);
      }
      if (DIRTY_SLOTS.size) scheduleFlush();
    }

    async function clearAllOrigin() {
      try {
        var dbs = await listDbs();
        for (var i = 0; i < dbs.length; i++) {
          await new Promise(function (resolve) {
            try {
              var req = indexedDB.deleteDatabase(dbs[i].name);
              req.onsuccess = req.onerror = req.onblocked = function () { resolve(); };
            } catch (e) { resolve(); }
          });
        }
      } catch (e) { }
      try { localStorage.clear(); } catch (e) { }
      try { sessionStorage.clear(); } catch (e) { }
      try {
        if (window.caches && caches.keys) {
          var keys = await caches.keys();
          for (var j = 0; j < keys.length; j++) { try { await caches.delete(keys[j]); } catch (e) { } }
        }
      } catch (e) { }
    }

    async function postText(path, text, final) {
      var url = API + path + (final ? '?final=1' : '');
      if (final && text.length < MAX_BEACON && navigator && navigator.sendBeacon) {
        try {
          if (navigator.sendBeacon(url, new Blob([text], { type: 'application/json;charset=UTF-8' }))) return true;
        } catch (e) { }
      }
      try {
        var res = await fetch(url, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json;charset=UTF-8' },
          body: text,
          keepalive: !!final
        });
        return !!res.ok;
      } catch (e) {
        note('writeback failed (' + path + '): ' + (e && e.message));
        return false;
      }
    }

    function scheduleFlush() {
      if (FLUSH_TIMER) return;
      FLUSH_TIMER = setTimeout(function () {
        FLUSH_TIMER = null;
        flush(false);
      }, 1500);
    }

    async function flushOne(slot, final) {
      var db;
      try {
        db = await openSaveDb();
      } catch (e) {
        note('open save db failed: ' + (e && e.message));
        return false;
      }
      try {
        var save = await getRecord(db, 'saves', slot);
        var details = await getRecord(db, 'details', slot);
        var payload = save
          ? { saveDb: SAVE_DB, op: 'put', slot: slot, save: save, details: details }
          : { saveDb: SAVE_DB, op: 'delete', slot: slot };
        return await postText('saves/slot', JSON.stringify(payload, replacer), final);
      } catch (e) {
        note('read slot ' + slot + ' failed: ' + (e && e.message));
        return false;
      } finally {
        try { db.close(); } catch (e) { }
      }
    }

    async function flush(final) {
      if (FLUSHING || IMPORTING) return;
      SAVE_DB = resolveSaveDb(MANIFEST && MANIFEST.saveDb);
      FLUSHING = true;
      try {
        if (CLEAR_PENDING) {
          var cleared = await postText('saves/clear', JSON.stringify({ saveDb: SAVE_DB }), final);
          if (!cleared) return;
          CLEAR_PENDING = false;
        }

        var dels = Array.from(DELETED_SLOTS);
        for (var i = 0; i < dels.length; i++) {
          var slot = dels[i];
          var okDelete = await postText('saves/slot',
            JSON.stringify({ saveDb: SAVE_DB, op: 'delete', slot: slot }), final);
          if (okDelete) DELETED_SLOTS.delete(slot);
        }

        var dirty = Array.from(DIRTY_SLOTS);
        for (var j = 0; j < dirty.length; j++) {
          var dirtySlot = dirty[j];
          if (await flushOne(dirtySlot, final)) DIRTY_SLOTS.delete(dirtySlot);
        }
      } finally {
        FLUSHING = false;
        if (CLEAR_PENDING || DIRTY_SLOTS.size || DELETED_SLOTS.size) scheduleFlush();
      }
    }

    async function readManifest() {
      try {
        var res = await fetch(API + 'saves/manifest', { cache: 'no-store' });
        if (res.ok) {
          var data = await res.json();
          if (data && data.ok !== false) MANIFEST = data;
        }
      } catch (e) {
        note('read manifest failed: ' + (e && e.message));
      }
      SAVE_DB = (MANIFEST && MANIFEST.saveDb) || await pickExistingSaveDb() ||
        (window.idb && window.idb.dbName) || DEFAULT_SAVE_DB;
    }

    async function boot() {
      var bootData = null;
      try {
        var res = await fetch(API + 'idb-mirror?boot=1', { cache: 'no-store' });
        bootData = JSON.parse(await res.text(), reviver);
      } catch (e) {
        note('read boot failed: ' + (e && e.message));
      }

      if (bootData && bootData.clean) {
        try { await clearAllOrigin(); } catch (e) { }
        note('origin storage cleared after version deletion');
        try { await fetch(API + 'idb-clean-done', { method: 'POST' }); } catch (e) { }
      }

      await readManifest();

      var db;
      try {
        db = await openSaveDb();
      } catch (e) {
        note('open save db failed: ' + (e && e.message));
        return;
      }

      try {
        if (bootData && bootData.import) {
          IMPORTING = true;
          try {
            var count = await importSaveDomain(bootData.import);
            note('imported ' + count + ' save records');
            try { await fetch(API + 'idb-import-done', { method: 'POST' }); } catch (e) { }
          } finally {
            IMPORTING = false;
          }

          try {
            var importedKeys = await readStoreKeys(db, 'saves');
            for (var m = 0; m < importedKeys.length; m++) DIRTY_SLOTS.add(importedKeys[m]);
            if (DIRTY_SLOTS.size) scheduleFlush();
          } catch (e) {
            note('mark imported slots failed: ' + (e && e.message));
          }
        }

        IMPORTING = true;
        try { await mergeFromDisk(db); }
        finally { IMPORTING = false; }
      } finally {
        try { db.close(); } catch (e) { }
      }
    }

    async function initNoBoot() {
      await readManifest();
    }

    var CLEAR_TRIED_KEY = 'dol-cache-clear-tried';

    function inspectDb(name) {
      return new Promise(function (resolve) {
        var req;
        try { req = indexedDB.open(name); } catch (e) { resolve(false); return; }
        var done = false;
        function finish(isSave) {
          if (done) return;
          done = true;
          try { if (req.result) req.result.close(); } catch (e) { }
          resolve(isSave);
        }
        req.onupgradeneeded = function () { finish(false); };
        req.onsuccess = function () {
          var isSave = false;
          try {
            var db = req.result;
            isSave = db.objectStoreNames.contains('saves') && db.objectStoreNames.contains('details');
          } catch (e) { }
          finish(isSave);
        };
        req.onerror = function () { finish(false); };
        req.onblocked = function () { finish(false); };
      });
    }

    function deleteDb(name) {
      return new Promise(function (resolve) {
        var req;
        try { req = indexedDB.deleteDatabase(name); } catch (e) { resolve(false); return; }
        var done = false;
        function finish(ok, why) {
          if (done) return;
          done = true;
          if (why) note('delete ' + name + ': ' + why);
          resolve(ok);
        }
        req.onsuccess = function () { finish(true, ''); };
        req.onerror = function () { finish(false, 'error'); };
        req.onblocked = function () { finish(false, 'blocked'); };
      });
    }

    async function clearNonSaveCaches() {
      var keep = new Set();
      var names = [];
      try {
        if (indexedDB.databases) {
          var arr = await indexedDB.databases();
          var all = (arr || []).map(function (d) { return d && d.name ? String(d.name) : ''; });
          for (var i = 0; i < all.length; i++) if (all[i]) names.push(all[i]);
        }
      } catch (e) { note('list databases failed: ' + (e && e.message)); }
      if (!names.length) {
        names = ['BeautySelectorAddon', 'i18n-cache', 'ModLoader_IndexDBLoader', 'maplebirch', 'bes'];
      }

      try {
        var res = await fetch(API + 'saves/manifest', { cache: 'no-store' });
        if (res.ok) {
          var data = await res.json();
          if (data && data.saveDb) keep.add(String(data.saveDb));
        }
      } catch (e) { note('read manifest failed: ' + (e && e.message)); }

      var deleted = 0;
      for (var n = 0; n < names.length; n++) {
        var name = names[n];
        if (!name || keep.has(name)) continue;
        if (await inspectDb(name)) { keep.add(name); note('keep save db ' + name); continue; }
        if (await deleteDb(name)) deleted++;
      }
      note('cleared ' + deleted + ' mod databases, kept ' + Array.from(keep).join(', '));
      try {
        await fetch(API + 'saves/cache-clear-done?count=' + deleted, { method: 'POST' });
      } catch (e) { note('report clear done failed: ' + (e && e.message)); }
    }

    function runCacheClear() {
      clearNonSaveCaches().then(function () {
        try { location.reload(); } catch (e) { }
      }, function (e) {
        note('clear failed: ' + (e && e.message));
        try { location.reload(); } catch (e2) { }
      });
    }

    function installHooks() {
      try {
        var proto = window.IDBObjectStore && IDBObjectStore.prototype;
        if (!proto) return;

        function watch(store) {
          try {
            return !!store && !!store.transaction && !!store.transaction.db &&
              store.transaction.db.name === SAVE_DB &&
              (store.name === 'saves' || store.name === 'details');
          } catch (e) { return false; }
        }

        function slotOf(value, key) {
          var raw;
          if (value && typeof value === 'object' && value.slot !== undefined) raw = value.slot;
          else raw = key;
          var slot = Number(raw);
          return isFinite(slot) && slot >= 0 ? slot : null;
        }

        ['put', 'add'].forEach(function (m) {
          var orig = proto[m];
          if (typeof orig !== 'function') return;
          proto[m] = function (value, key) {
            var result = orig.apply(this, arguments);
            try {
              if (!IMPORTING && !window.__dolIdbImporting && watch(this)) {
                var slot = slotOf(value, key);
                if (slot !== null) {
                  DIRTY_SLOTS.add(slot);
                  DELETED_SLOTS.delete(slot);
                  scheduleFlush();
                }
              }
            } catch (e) { }
            return result;
          };
        });

        var origDelete = proto['delete'];
        if (typeof origDelete === 'function') {
          proto['delete'] = function (key) {
            var result = origDelete.apply(this, arguments);
            try {
              if (!IMPORTING && !window.__dolIdbImporting && watch(this)) {
                var slot = Number(key);
                if (isFinite(slot) && slot >= 0) {
                  DELETED_SLOTS.add(slot);
                  DIRTY_SLOTS.delete(slot);
                  scheduleFlush();
                }
              }
            } catch (e) { }
            return result;
          };
        }

        var origClear = proto['clear'];
        if (typeof origClear === 'function') {
          proto['clear'] = function () {
            var result = origClear.apply(this, arguments);
            try {
              if (!IMPORTING && !window.__dolIdbImporting && watch(this)) {
                CLEAR_PENDING = true;
                DIRTY_SLOTS.clear();
                DELETED_SLOTS.clear();
              }
            } catch (e) { }
            return result;
          };
        }
      } catch (e) { note('hook failed: ' + (e && e.message)); }
    }

    try { SAVE_DB = resolveSaveDb(null); } catch (e) { SAVE_DB = DEFAULT_SAVE_DB; }
    installHooks();

    window.__dolIdbResync = async function () {
      var db;
      try {
        db = await openSaveDb();
        var savesKeys = await readStoreKeys(db, 'saves');
        var detailsKeys = await readStoreKeys(db, 'details');
        var seen = new Set();
        var keys = [];
        var i;
        for (i = 0; i < savesKeys.length; i++) {
          if (!seen.has(savesKeys[i])) { seen.add(savesKeys[i]); keys.push(savesKeys[i]); }
        }
        for (i = 0; i < detailsKeys.length; i++) {
          if (!seen.has(detailsKeys[i])) { seen.add(detailsKeys[i]); keys.push(detailsKeys[i]); }
        }
        DIRTY_SLOTS.clear();
        DELETED_SLOTS.clear();
        CLEAR_PENDING = true;
        for (i = 0; i < keys.length; i++) DIRTY_SLOTS.add(keys[i]);
        scheduleFlush();
      } catch (e) {
        note('resync failed: ' + (e && e.message));
      } finally {
        if (db) { try { db.close(); } catch (e) { } }
      }
    };

    try {
      setInterval(function () {
        try {
          if (window.idb && window.idb.dbName && window.idb.dbName !== SAVE_DB) {
            pickExistingSaveDb().then(function (name) {
              if (name && name !== SAVE_DB) SAVE_DB = name;
            }, function () { });
          }
        } catch (e) { }
        if (CLEAR_PENDING || DIRTY_SLOTS.size || DELETED_SLOTS.size) flush(false);
      }, 15000);
    } catch (e) { }

    try {
      document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'hidden') flush(true);
      });
      window.addEventListener('pagehide', function () { flush(true); });
    } catch (e) { }

    if (window.__DOL_CLEAR_CACHES__) {
      // 同步停止解析：必须早于任何 await，防止游戏脚本继续执行
      try { window.stop(); } catch (e) { }
      try {
        document.documentElement.innerHTML =
          '<body style="background:#141414;color:#ddd;font:14px sans-serif;padding:24px">正在清理 Mod 缓存（保留存档），请稍候…</body>';
      } catch (e) { }
      var tried = false;
      try { tried = sessionStorage.getItem(CLEAR_TRIED_KEY) === '1'; } catch (e) { }
      if (tried) {
        note('cache clear already tried once, skip');
        fetch(API + 'saves/cache-clear-done?count=0', { method: 'POST' })
          .catch(function (e) { note('report clear done failed: ' + (e && e.message)); })
          .then(function () { try { location.reload(); } catch (e) { } });
      } else {
        try { sessionStorage.setItem(CLEAR_TRIED_KEY, '1'); } catch (e) { }
        runCacheClear();
      }
    } else {
      try { sessionStorage.removeItem(CLEAR_TRIED_KEY); } catch (e) { }
      if (window.__dolIdbNoBoot) initNoBoot();
      else boot();
    }
  } catch (e) { }
})();
</script>
""";

    /// <summary>
    /// 注入到游戏页面里的日志采集脚本：把 console 与未捕获错误 POST 回启动器。
    /// 脚本内容只用 ASCII（中文用 \u 转义），注入到任何编码的页面都不会被破坏。
    /// </summary>
    private const string GameLogScript = """
<script>
(function () {
  try {
    if (window.__dolLogHooked) return;
    window.__dolLogHooked = true;

    var rawWarn = (typeof console !== 'undefined' && console.warn) ? console.warn : null;
    function pad(n) { return (n < 10 ? '0' : '') + n; }
    var now = new Date();
    var SID = now.getFullYear() + pad(now.getMonth() + 1) + pad(now.getDate()) + '-' +
              pad(now.getHours()) + pad(now.getMinutes()) + pad(now.getSeconds());
    var ENDPOINT = '/__launcher/api/game-log?sid=' + SID;
    var MAX_LINE = 1500;
    var MAX_CHUNK = 8000;
    var MAX_QUEUE = __MAX_QUEUE__;
    var MAX_QUEUE_LINES = 2000;
    var MAX_FAILS = 3;
    // 详细模式：为报错补采堆栈与调用位置
    var DETAIL = __DETAIL__;
    var MAX_STACK = 4000;
    var RETRY_DELAYS = [1000, 4000, 12000];
    var queue = [];
    var queued = 0;
    var timer = null;
    var fails = 0;
    var stopped = false;
    var retryAt = 0;

    // UTF-8 byte width of the code point starting at index i
    function utf8Width(text, i) {
      var code = text.charCodeAt(i);
      if (code < 0x80) return 1;
      if (code < 0x800) return 2;
      if (code >= 0xD800 && code <= 0xDBFF && i + 1 < text.length) {
        var next = text.charCodeAt(i + 1);
        if (next >= 0xDC00 && next <= 0xDFFF) return 4;
      }
      return 3;
    }

    // Split by UTF-8 byte budget, never in the middle of a surrogate pair
    function splitChunks(text) {
      var out = [];
      var start = 0;
      var bytes = 0;
      var i = 0;
      while (i < text.length) {
        var width = utf8Width(text, i);
        if (bytes + width > MAX_CHUNK && i > start) {
          out.push(text.slice(start, i));
          start = i;
          bytes = 0;
        }
        bytes += width;
        i += (width === 4 ? 2 : 1);
      }
      if (start < text.length) out.push(text.slice(start));
      return out;
    }

    function stopUrl(reason) {
      return ENDPOINT + '&stop=1&fails=' + fails +
             '&reason=' + encodeURIComponent(String(reason || 'unknown'));
    }

    // Count a failed report, back off, and stop for good once the limit is reached
    function noteFail(reason) {
      try {
        if (stopped) return;
        fails++;
        var delay = RETRY_DELAYS[Math.min(fails - 1, RETRY_DELAYS.length - 1)];
        retryAt = Date.now() + (delay || 1000);
        if (fails < MAX_FAILS) return;

        stopped = true;
        queue.length = 0;
        queued = 0;
        if (timer) { clearTimeout(timer); timer = null; }

        var note = '=== \u65e5\u5fd7\u4e0a\u62a5\u8fde\u7eed\u5931\u8d25 ' + fails + ' \u6b21\uff0c\u5df2\u505c\u6b62\u5199\u5165 logs/game-*.log\uff08\u6e38\u620f\u672c\u8eab\u4e0d\u53d7\u5f71\u54cd\uff09 ===';
        try { if (rawWarn) rawWarn.call(console, '[DoL \u6e38\u620f\u65e5\u5fd7] ' + note); } catch (e) { }

        // Send the note once and never re-enter the collector, so failures cannot loop
        var body = note + '\n';
        var url = stopUrl(reason);
        try {
          if (navigator && navigator.sendBeacon) {
            if (navigator.sendBeacon(url, new Blob([body], { type: 'text/plain;charset=UTF-8' }))) return;
          }
        } catch (e) { }
        try {
          if (typeof fetch === 'function') {
            fetch(url, {
              method: 'POST',
              body: body,
              keepalive: true,
              headers: { 'Content-Type': 'text/plain;charset=UTF-8' }
            }).catch(function () { });
          }
        } catch (e) { }
      } catch (e) { }
    }

    function postFetch(chunk, urgent) {
      try {
        if (typeof fetch !== 'function') { noteFail('fetch unavailable'); return; }
        var opt = {
          method: 'POST',
          body: chunk,
          headers: { 'Content-Type': 'text/plain;charset=UTF-8' }
        };
        // keepalive 与 sendBeacon 共用同一个 64KB 在途配额，普通上报不占用它，
        // 否则日志一多就会连续 "Failed to fetch"（页面关闭时才用 urgent 保证送达）
        if (urgent) opt.keepalive = true;
        fetch(ENDPOINT, opt).then(function (r) {
          try {
            if (r && r.ok) { fails = 0; retryAt = 0; return; }
            noteFail(r ? ('HTTP ' + r.status) : 'empty response');
          } catch (e) { }
        }).catch(function (err) {
          try { noteFail((err && (err.message || err.type)) || 'network error'); } catch (e) { }
        });
      } catch (e) {
        try { noteFail((e && e.message) || 'fetch exception'); } catch (x) { }
      }
    }

    function post(chunk, urgent) {
      if (stopped || !chunk) return;
      if (urgent) {
        try {
          if (navigator && navigator.sendBeacon) {
            var blob = new Blob([chunk], { type: 'text/plain;charset=UTF-8' });
            if (navigator.sendBeacon(ENDPOINT, blob)) return;
          }
        } catch (e) { }
      }
      postFetch(chunk, !!urgent);
    }

    function schedule(delay) {
      try {
        if (timer || stopped) return;
        timer = setTimeout(function () { timer = null; flush(false); }, delay);
      } catch (e) { }
    }

    function flush(force, urgent) {
      try {
        if (timer) { clearTimeout(timer); timer = null; }
        if (stopped || !queue.length) return;
        if (!force && retryAt > Date.now()) {
          schedule(Math.max(200, retryAt - Date.now()));
          return;
        }
        var text = queue.join('\n') + '\n';
        queue.length = 0;
        queued = 0;
        var parts = splitChunks(text);
        for (var i = 0; i < parts.length; i++) {
          if (stopped) break;
          post(parts[i], !!urgent);
        }
      } catch (e) { }
    }

    function safeText(value) {
      try {
        if (typeof value === 'string') return value;
        if (value instanceof Error) return value.stack || value.message || String(value);
        if (value && typeof value === 'object') {
          var json = JSON.stringify(value);
          return typeof json === 'string' ? json : String(value);
        }
        return String(value);
      } catch (e) {
        try { return String(value); } catch (x) { return '[unserializable]'; }
      }
    }

    function stackText(err) {
      try {
        var text = err && err.stack ? String(err.stack) : '';
        if (!text) return '';
        return text.length > MAX_STACK ? text.slice(0, MAX_STACK) + '...(truncated)' : text;
      } catch (e) { return ''; }
    }

    function hasErrorArg(args) {
      try {
        for (var i = 0; i < args.length; i++) {
          if (args[i] instanceof Error) return true;
        }
      } catch (e) { }
      return false;
    }

    function push(level, args) {
      try {
        if (stopped) return;
        var important = (level === 'error' || level === 'warn' || level === 'reject');
        var parts = [];
        var count = (args && typeof args.length === 'number') ? args.length : 0;
        for (var i = 0; i < count; i++) {
          var s = safeText(args[i]);
          if (!important && s.length > MAX_LINE) {
            s = s.slice(0, 200) + '...[\u5171 ' + s.length + ' \u5b57\u7b26\uff0c\u5df2\u7701\u7565]';
          }
          parts.push(s);
        }
        var line = new Date().toISOString() + ' [' + level + '] ' + parts.join(' ');
        try { uiNote(level, parts.join(' ')); } catch (u) { }
        if (line.length > MAX_CHUNK) {
          flush(true, false);
          if (stopped) return;
          var longParts = splitChunks(line + '\n');
          for (var j = 0; j < longParts.length; j++) {
            if (stopped) break;
            post(longParts[j], false);
          }
          return;
        }
        if (!important && (queued + line.length > MAX_QUEUE || queue.length >= MAX_QUEUE_LINES)) return;
        queue.push(line);
        queued += line.length + 1;
        if (important || queue.length >= 100 || queued >= MAX_CHUNK - 1000) flush(true, false);
        else schedule(__FLUSH_MS__);
      } catch (e) { }
    }

    // ---------- live diagnostics: floating ball + log panel ----------
    var RULES = __RULES__;
    var LEVEL_NAMES = ['Information', 'Warning', 'Warning (severe)', 'Error', 'Fatal'];
    var entries = [];
    var worst = 0;
    var uiRoot = null, ballEl = null, panelEl = null, listEl = null, headEl = null;
    var ballX = -1, ballY = -1, docked = false, dockSide = 'left';
    var dockTimer = null, uiBuilt = false, fatalAt = 0, panelOpen = false;

    function baseLevelOf(tag) {
      if (tag === 'warn') return 1;
      if (tag === 'error' || tag === 'reject') return 3;
      if (tag === 'fatal') return 4;
      return 0;
    }

    function classify(text, tag) {
      var level = baseLevelOf(tag);
      var hints = [];
      var lower = String(text || '').toLowerCase();
      for (var i = 0; i < RULES.length; i++) {
        if (lower.indexOf(RULES[i].keyword) < 0) continue;
        if (RULES[i].level > level) level = RULES[i].level;
        if (hints.indexOf(RULES[i].hint) < 0) hints.push(RULES[i].hint);
      }
      if (!hints.length && level >= 3) {
        hints.push('\u6ca1\u6709\u5339\u914d\u5230\u5df2\u77e5\u539f\u56e0\u6a21\u5f0f\uff0c\u8bf7\u7ed3\u5408\u4e0a\u4e0b\u6587\u4e0e\u5b8c\u6574\u5185\u5bb9\u5224\u65ad\u3002');
      }
      return { level: level, hints: hints };
    }

    function uiNote(tag, text) {
      try {
        var info = classify(text, tag);
        entries.push({ tag: tag, text: text, level: info.level, hints: info.hints, time: new Date() });
        if (entries.length > 400) entries.splice(0, entries.length - 400);
        if (info.level > worst) worst = info.level;
        paintBall();
        if (info.level === 4) notifyFatal(text);
        if (panelOpen) renderPanel();
      } catch (e) { }
    }

    function notifyFatal(text) {
      try {
        if (Date.now() - fatalAt < 15000) return;
        fatalAt = Date.now();
        var msg = 'Fatal: ' + text;
        try {
          if (typeof fetch === 'function') {
            fetch('/__launcher/api/game-fatal', {
              method: 'POST',
              body: msg,
              headers: { 'Content-Type': 'text/plain;charset=UTF-8' }
            }).catch(function () { });
          }
        } catch (e) { }
        showBanner(msg);
      } catch (e) { }
    }

    function ensureUi() {
      if (uiBuilt) return;
      uiBuilt = true;
      try {
        var host = document.createElement('div');
        host.setAttribute('data-dol-ui', '1');
        host.style.cssText = 'position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;';
        try { uiRoot = host.attachShadow ? host.attachShadow({ mode: 'open' }) : host; }
        catch (e) { uiRoot = host; }

        var style = document.createElement('style');
        style.textContent =
          '.ball{position:fixed;display:flex;align-items:center;justify-content:center;color:#fff;' +
          'font:600 12px/1 "Microsoft YaHei UI",Segoe UI,sans-serif;cursor:pointer;user-select:none;' +
          'touch-action:none;pointer-events:auto;box-shadow:0 6px 18px rgba(0,0,0,.35);' +
          'transition:width .18s ease-out,height .18s ease-out,left .18s ease-out,top .18s ease-out,background .25s ease;' +
          'background:#3b82f6;width:46px;height:46px;border-radius:23px;}' +
          '.ball.docked{width:28px;height:46px;border-radius:8px;}' +
          '.panel{position:fixed;display:none;flex-direction:column;width:390px;background:rgba(24,26,32,.97);' +
          'color:#e8eaf0;border:1px solid rgba(255,255,255,.14);border-radius:12px;overflow:hidden;' +
          'box-shadow:0 16px 48px rgba(0,0,0,.5);font:12px/1.5 "Microsoft YaHei UI",Segoe UI,sans-serif;' +
          'pointer-events:auto;}' +
          '.panel.open{display:flex;}' +
          '.head{display:flex;align-items:center;gap:8px;padding:10px 12px;border-bottom:1px solid rgba(255,255,255,.12);}' +
          '.title{font-weight:600;font-size:13px;}' +
          '.meta{opacity:.7;font-size:11px;margin-left:6px;}' +
          '.close{margin-left:auto;cursor:pointer;opacity:.7;font-size:15px;padding:0 4px;}' +
          '.close:hover{opacity:1;}' +
          '.list{overflow:auto;padding:4px 0;}' +
          '.item{padding:6px 12px;border-left:3px solid #3b82f6;}' +
          '.item.l1{border-left-color:#f59e0b;}' +
          '.item.l2{border-left-color:#ef4444;background:rgba(239,68,68,.08);}' +
          '.item.l3{border-left-color:#dc2626;background:rgba(220,38,38,.10);}' +
          '.item.l4{border-left-color:#a21caf;background:rgba(162,28,175,.14);}' +
          '.msg{white-space:pre-wrap;word-break:break-word;font-family:Consolas,monospace;font-size:11px;}' +
          '.hint{margin-top:3px;color:#ffd28a;font-size:11px;}' +
          '.tag{display:inline-block;margin-right:6px;padding:0 5px;border-radius:4px;' +
          'background:rgba(255,255,255,.14);font-size:10px;}' +
          '.banner{position:fixed;left:50%;top:16px;transform:translateX(-50%);max-width:80vw;' +
          'background:#a21caf;color:#fff;padding:10px 16px;border-radius:10px;display:flex;gap:10px;' +
          'align-items:center;box-shadow:0 10px 36px rgba(0,0,0,.5);' +
          'font:12px/1.5 "Microsoft YaHei UI",Segoe UI,sans-serif;pointer-events:auto;z-index:3;}';
        uiRoot.appendChild(style);

        ballEl = document.createElement('div');
        ballEl.className = 'ball';
        ballEl.addEventListener('pointerdown', startDrag);
        ballEl.addEventListener('mouseenter', function () {
          if (dockTimer) { clearTimeout(dockTimer); dockTimer = null; }
        });
        ballEl.addEventListener('mouseleave', function () { startDockTimer(); });
        uiRoot.appendChild(ballEl);

        panelEl = document.createElement('div');
        panelEl.className = 'panel';
        var head = document.createElement('div');
        head.className = 'head';
        headEl = document.createElement('div');
        headEl.className = 'title';
        var close = document.createElement('div');
        close.className = 'close';
        close.textContent = '\u00d7';
        close.addEventListener('click', function () { hidePanel(); });
        head.appendChild(headEl);
        head.appendChild(close);
        listEl = document.createElement('div');
        listEl.className = 'list';
        panelEl.appendChild(head);
        panelEl.appendChild(listEl);
        uiRoot.appendChild(panelEl);

        document.documentElement.appendChild(host);
        placeBall();
        paintBall();
      } catch (e) { }
    }

    function placeBall() {
      if (!ballEl) return;
      var w = window.innerWidth, h = window.innerHeight;
      var size = docked ? 28 : 46;
      if (ballX < 0) ballX = 10;
      if (ballY < 0) ballY = Math.round(h * 0.4);
      ballX = Math.max(4, Math.min(ballX, w - size - 4));
      ballY = Math.max(54, Math.min(ballY, h - size - 10));
      ballEl.style.left = Math.round(ballX) + 'px';
      ballEl.style.top = Math.round(ballY) + 'px';
    }

    function paintBall() {
      if (!ballEl) return;
      var colors = ['#3b82f6', '#f59e0b', '#ef4444', '#dc2626', '#a21caf'];
      ballEl.style.background = colors[Math.max(0, Math.min(worst, 4))];
      ballEl.className = docked ? 'ball docked' : 'ball';
      var text;
      if (docked) text = dockSide === 'left' ? '\u203a' : '\u2039';
      else text = entries.length > 99 ? '99+' : String(entries.length);
      ballEl.textContent = text;
    }

    function dock() {
      if (!ballEl) return;
      var w = window.innerWidth;
      dockSide = ballX < w / 2 ? 'left' : 'right';
      docked = true;
      ballX = dockSide === 'left' ? 2 : w - 30;
      placeBall();
      paintBall();
    }

    function undock() {
      if (!docked) return;
      docked = false;
      paintBall();
    }

    function startDockTimer() {
      if (dockTimer) clearTimeout(dockTimer);
      dockTimer = setTimeout(function () {
        dockTimer = null;
        if (panelOpen) return;
        dock();
      }, 4000);
    }

    function startDrag(ev) {
      try {
        if (ev.button != null && ev.button !== 0) return;
        ev.preventDefault();
        // 拖动过程中不要有过渡，否则鼠标已经到位、悬浮球还在慢慢追
        ballEl.style.transition = 'none';
        var startX = ev.clientX, startY = ev.clientY, ox = ballX, oy = ballY, moved = false;
        if (dockTimer) { clearTimeout(dockTimer); dockTimer = null; }
        undock();
        function onMove(e) {
          moved = moved || Math.abs(e.clientX - startX) + Math.abs(e.clientY - startY) > 4;
          ballX = ox + (e.clientX - startX);
          ballY = oy + (e.clientY - startY);
          placeBall();
        }
        function onUp() {
          window.removeEventListener('pointermove', onMove, true);
          window.removeEventListener('pointerup', onUp, true);
          ballEl.style.transition = '';
          if (!moved) { togglePanel(); return; }
          startDockTimer();
        }
        window.addEventListener('pointermove', onMove, true);
        window.addEventListener('pointerup', onUp, true);
      } catch (e) { }
    }

    function togglePanel() {
      if (panelOpen) hidePanel(); else showPanel();
    }

    function showPanel() {
      ensureUi();
      if (!panelEl) return;
      panelOpen = true;
      if (dockTimer) { clearTimeout(dockTimer); dockTimer = null; }
      renderPanel();
      positionPanel();
      panelEl.className = 'panel open';
    }

    function hidePanel() {
      panelOpen = false;
      if (panelEl) panelEl.className = 'panel';
    }

    function positionPanel() {
      if (!panelEl || !ballEl) return;
      var w = window.innerWidth, h = window.innerHeight;
      var width = Math.min(390, w - 24);
      var height = Math.min(Math.round(h * 0.62), 520);
      var left = dockSide === 'left' ? ballX + 40 : ballX - width - 8;
      left = Math.max(8, Math.min(left, w - width - 8));
      var top = Math.max(10, Math.min(ballY - 30, h - height - 10));
      panelEl.style.left = Math.round(left) + 'px';
      panelEl.style.top = Math.round(top) + 'px';
      panelEl.style.width = width + 'px';
      panelEl.style.maxHeight = height + 'px';
    }

    function renderPanel() {
      if (!listEl) return;
      headEl.textContent = '\u5b9e\u65f6\u65e5\u5fd7\uff1a' + entries.length +
        ' \u6761 \u00b7 \u5f53\u524d\u7b49\u7ea7 ' + LEVEL_NAMES[worst];
      listEl.textContent = '';
      var start = Math.max(0, entries.length - 200);
      for (var j = start; j < entries.length; j++) {
        var e = entries[j];
        var item = document.createElement('div');
        item.className = 'item l' + e.level;
        var tag = document.createElement('span');
        tag.className = 'tag';
        tag.textContent = LEVEL_NAMES[e.level];
        item.appendChild(tag);
        var time = document.createElement('span');
        time.className = 'meta';
        time.textContent = e.time.toTimeString().slice(0, 8);
        item.appendChild(time);
        var msg = document.createElement('div');
        msg.className = 'msg';
        msg.textContent = e.text;
        item.appendChild(msg);
        for (var k = 0; k < e.hints.length; k++) {
          var hint = document.createElement('div');
          hint.className = 'hint';
          hint.textContent = '\u53ef\u80fd\u539f\u56e0\uff1a' + e.hints[k];
          item.appendChild(hint);
        }
        listEl.appendChild(item);
      }
      listEl.scrollTop = listEl.scrollHeight;
    }

    function showBanner(text) {
      ensureUi();
      if (!uiRoot) return;
      try {
        var banner = document.createElement('div');
        banner.className = 'banner';
        var span = document.createElement('span');
        span.textContent = text.length > 400 ? text.slice(0, 400) + '...' : text;
        var close = document.createElement('div');
        close.className = 'close';
        close.textContent = '\u00d7';
        close.addEventListener('click', function () { try { banner.remove(); } catch (e) { } });
        banner.appendChild(span);
        banner.appendChild(close);
        uiRoot.appendChild(banner);
        setTimeout(function () { try { banner.remove(); } catch (e) { } }, 30000);
      } catch (e) { }
    }

    function initUi() { try { ensureUi(); } catch (e) { } }
    try {
      if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initUi);
      else initUi();
      window.addEventListener('resize', function () {
        placeBall();
        if (panelOpen) positionPanel();
      });
    } catch (e) { }

    var levels = ['log', 'info', 'warn', 'error', 'debug'];
    for (var i = 0; i < levels.length; i++) {
      (function (k) {
        try {
          var orig = console[k];
          if (typeof orig !== 'function') return;
          console[k] = function () {
            try {
              if (DETAIL && (k === 'error' || k === 'warn') && !hasErrorArg(arguments)) {
                // 字符串形式报错本身没有堆栈，补一份调用位置，便于回溯调用链
                var extra = [];
                for (var a = 0; a < arguments.length; a++) extra.push(arguments[a]);
                extra.push('\n[call site]\n' + stackText(new Error()));
                push(k, extra);
              } else {
                push(k, arguments);
              }
            } catch (e) { }
            return orig.apply(console, arguments);
          };
        } catch (e) { }
      })(levels[i]);
    }

    window.addEventListener('error', function (e) {
      try {
        var where = ((e && e.filename) || '') + ':' + ((e && e.lineno) || 0) +
                    ':' + ((e && e.colno) || 0);
        var head = (e && e.message ? e.message : 'error') + ' @ ' + where;
        var stack = DETAIL ? stackText(e && e.error) : '';
        push('error', [stack ? head + '\n' + stack : head]);
      } catch (x) { }
    });
    window.addEventListener('unhandledrejection', function (e) {
      try {
        var r = e ? e.reason : null;
        var text = r && r.stack ? stackText(r) : String((r && r.message) || r);
        push('reject', [text]);
      } catch (x) { }
    });
    window.addEventListener('pagehide', function () { try { flush(true, true); } catch (e) { } });
    try {
      document.addEventListener('visibilitychange', function () {
        try { if (document.visibilityState === 'hidden') flush(true, true); } catch (e) { }
      });
    } catch (e) { }

    queue.push('=== game session ' + new Date().toString() + ' ===');
    push('info', ['game page opened: ' + location.href]);
    try { setInterval(function () { flush(false); }, 5000); } catch (e) { }

    // Heartbeat: keep this page registered while it stays open; closing it stops the beat.
    var PAGE = 'g' + Math.random().toString(36).slice(2);
    var HEARTBEAT = '/__launcher/api/heartbeat?page=' + PAGE;
    var PAGE_CLOSED = '/__launcher/api/page-closed?page=' + PAGE;
    function heartbeat() {
      try {
        if (typeof fetch !== 'function') return;
        fetch(HEARTBEAT, { method: 'POST', keepalive: true }).catch(function () { });
      } catch (e) { }
    }
    try { heartbeat(); setInterval(heartbeat, __HEARTBEAT_MS__); } catch (e) { }
    // 主动上报关闭：刷新时旧页面也会发，但新页面 6 秒内的心跳会取消这次关闭
    window.addEventListener('pagehide', function () {
      try {
        if (navigator.sendBeacon) navigator.sendBeacon(PAGE_CLOSED, '');
        else fetch(PAGE_CLOSED, { method: 'POST', keepalive: true }).catch(function () { });
      } catch (e) { }
    });
  } catch (e) { }
})();
</script>
""";

    private static async Task WriteFileAsync(NetworkStream stream, string full, string method, string[] headerLines, CancellationToken ct)
    {
        await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        var length = fs.Length;
        var contentType = MimeOf(full);

        long start = 0;
        long end = length - 1;
        var partial = false;

        var range = GetHeader(headerLines, "range");
        if (range != null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range[6..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var startText = spec[..dash].Trim();
                var endText = spec[(dash + 1)..].Trim();
                if (startText.Length == 0)
                {
                    // bytes=-N 取最后 N 字节
                    if (long.TryParse(endText, out var suffix) && suffix > 0)
                    {
                        start = Math.Max(0, length - suffix);
                        end = length - 1;
                        partial = true;
                    }
                }
                else if (long.TryParse(startText, out var s))
                {
                    start = s;
                    if (endText.Length > 0 && long.TryParse(endText, out var e)) end = Math.Min(e, length - 1);
                    if (start >= length)
                    {
                        var msg = Encoding.ASCII.GetBytes("416 Range Not Satisfiable");
                        var h = "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + length +
                                "\r\nContent-Length: " + msg.Length + "\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(h), ct);
                        await stream.WriteAsync(msg, ct);
                        return;
                    }
                    partial = true;
                }
            }
        }

        if (end < start) end = length - 1;
        var count = end - start + 1;

        var sb = new StringBuilder();
        sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(count).Append("\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        if (partial)
        {
            sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
        }
        sb.Append("Cache-Control: no-cache\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        if (method == "HEAD") return;

        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[128 * 1024];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read <= 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }

    private static async Task WriteBytesAsync(NetworkStream stream, int code, string reason, string contentType,
        byte[] body, string method, CancellationToken ct)
    {
        var head = "HTTP/1.1 " + code + " " + reason + "\r\n" +
                   "Content-Type: " + contentType + "\r\n" +
                   "Content-Length: " + body.Length + "\r\n" +
                   "Cache-Control: no-cache\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        if (method == "HEAD") return;
        if (body.Length > 0) await stream.WriteAsync(body, ct);
    }

    private static async Task WriteTextAsync(NetworkStream stream, int code, string reason, string contentType,
        string text, string method, CancellationToken ct)
        => await WriteBytesAsync(stream, code, reason, contentType, Encoding.UTF8.GetBytes(text), method, ct);

    private static string? GetHeader(string[] lines, string name)
    {
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }
        return null;
    }

    private static async Task<(string Header, byte[] Leftover)?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buffer = new byte[8192];
        var headerEnd = -1;
        while (ms.Length < 64 * 1024)
        {
            int n;
            try { n = await stream.ReadAsync(buffer, 0, buffer.Length, ct); }
            catch { return null; }
            if (n <= 0) break;
            ms.Write(buffer, 0, n);
            headerEnd = IndexOfHeaderEnd(ms.GetBuffer(), (int)ms.Length);
            if (headerEnd >= 0) break;
        }

        if (ms.Length == 0) return null;
        var bytes = ms.ToArray();
        var length = headerEnd >= 0 ? headerEnd : bytes.Length;
        var leftover = headerEnd >= 0 && headerEnd + 4 < bytes.Length ? bytes[(headerEnd + 4)..] : Array.Empty<byte>();
        return (Encoding.Latin1.GetString(bytes, 0, length), leftover);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10) return i;
        }
        return -1;
    }

    private static string MimeOf(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".ogg" or ".oga" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".webm" => "video/webm",
            ".mp4" => "video/mp4",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".zip" => "application/zip",
            ".txt" or ".md" => "text/plain; charset=utf-8",
            ".map" => "application/json",
            _ => "application/octet-stream",
        };
    }
}
