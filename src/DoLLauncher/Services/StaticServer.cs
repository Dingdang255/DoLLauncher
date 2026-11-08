using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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

    public int Port { get; }

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

    public void Start()
    {
        lock (_sync)
        {
            if (_listener != null) return;
            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start(128);
            _listener = listener;
            _cts = new CancellationTokenSource();
        }
        _ = Task.Run(() => AcceptLoop(_cts!.Token));
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
            var body = Encoding.UTF8.GetBytes(SaveHelperPage.Build(_versionName));
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

        await WriteFileAsync(stream, full, method, lines, ct);
    }

    /// <summary>存档助手页用到的备份接口：列表、读取、写入、删除。</summary>
    private async Task HandleApiAsync(NetworkStream stream, string method, string path, string query,
        string[] lines, byte[] leftover, CancellationToken ct)
    {
        var q = ParseQuery(query);
        var file = q.GetValueOrDefault("file", "");

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
