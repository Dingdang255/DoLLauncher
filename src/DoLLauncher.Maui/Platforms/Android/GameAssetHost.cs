using System.Security.Cryptography;
using System.Text;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 本地游戏页的静态资源映射：把 file:// 目录登记成一个虚拟 token，
/// 通过 <c>https://appassets.androidplatform.net/local/{token}/...</c> 交给 WebView 加载，
/// 由 <see cref="GameWebViewClient"/> 在 ShouldInterceptRequest 中读取真实文件。
/// 这样不依赖 AndroidX WebKit，也不受 file:// 跨域限制影响。
/// </summary>
internal static class GameAssetHost
{
    public const string Host = "appassets.androidplatform.net";

    private static readonly Dictionary<string, string> Roots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把 file:// 地址转换为虚拟 https 地址；不是本地文件时返回 null。</summary>
    public static string? MapFileUrl(string? fileUrl)
    {
        if (!Uri.TryCreate(fileUrl ?? string.Empty, UriKind.Absolute, out var uri) || !uri.IsFile) return null;

        var path = uri.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return null;

        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name)) return null;

        var token = TokenOf(directory);
        Roots[token] = directory;
        return $"https://{Host}/local/{token}/{Uri.EscapeDataString(name)}";
    }

    /// <summary>判断并读取虚拟地址对应的本地文件；不匹配时返回 false。</summary>
    public static bool TryServe(string? url, out Android.Webkit.WebResourceResponse? response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return false;

        var segments = uri.AbsolutePath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string root;
        string relative;

        if (segments.Length >= 2 && segments[0].Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            var token = segments[1];
            if (!Roots.TryGetValue(token, out var found)) return false;
            root = found;
            relative = string.Join("/", segments.Skip(2));
        }
        else
        {
            // 页面里写死「/img/x.png」这类根路径时，只有一个已登记目录就按它解析
            if (Roots.Count != 1) return false;
            root = Roots.Values.First();
            relative = string.Join("/", segments);
        }

        relative = Uri.UnescapeDataString(relative);
        if (relative.Length == 0) relative = "index.html";

        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(full)) return false;

        try
        {
            var mime = MimeOf(full);
            var encoding = IsTextMime(mime) ? "UTF-8" : null;
            response = new Android.Webkit.WebResourceResponse(mime, encoding, File.OpenRead(full));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTextMime(string mime) =>
        mime.StartsWith("text/", StringComparison.Ordinal) ||
        mime.Contains("javascript", StringComparison.Ordinal) ||
        mime.Contains("json", StringComparison.Ordinal) ||
        mime.Contains("svg", StringComparison.Ordinal);

    private static string TokenOf(string directory)
    {
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(directory.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".js" or ".mjs" => "application/javascript",
        ".css" => "text/css",
        ".json" or ".map" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".wasm" => "application/wasm",
        ".txt" or ".log" => "text/plain",
        _ => "application/octet-stream",
    };
}
