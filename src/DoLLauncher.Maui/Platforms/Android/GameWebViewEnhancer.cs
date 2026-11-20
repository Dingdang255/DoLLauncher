using Android.Runtime;
using AndroidX.Core.View;
using DoLLauncher.Services;
using AndroidCustomViewCallback = Android.Webkit.WebChromeClient.ICustomViewCallback;
using AndroidDownloadListener = Android.Webkit.IDownloadListener;
using AndroidJavascriptInterface = Android.Webkit.JavascriptInterfaceAttribute;
using AndroidRenderProcessGoneDetail = Android.Webkit.RenderProcessGoneDetail;
using AndroidView = Android.Views.View;
using AndroidViewGroup = Android.Views.ViewGroup;
using AndroidWebChromeClient = Android.Webkit.WebChromeClient;
using AndroidWebResourceError = Android.Webkit.WebResourceError;
using AndroidWebResourceRequest = Android.Webkit.IWebResourceRequest;
using AndroidWebResourceResponse = Android.Webkit.WebResourceResponse;
using AndroidWebView = Android.Webkit.WebView;
using AndroidWebViewClient = Android.Webkit.WebViewClient;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 游戏页 WebView 增强：UA、本地资源映射、外链 / 下载拦截、JS 桥与全屏。
/// 只处理 Android 平台视图，不改变 MAUI 页面本身的关页策略与生命周期。
/// </summary>
internal static class GameWebViewEnhancer
{
    public static void Attach(AndroidWebView view, Action<string> openExternal, Action<string> onError,
        Action onReady, Action onClose)
    {
        try
        {
            var settings = view.Settings;
            if (settings != null)
            {
                var original = settings.UserAgentString ?? string.Empty;
                if (!original.Contains("DoLLauncher/", StringComparison.Ordinal))
                {
                    var version = BuildInfo.Tag == "dev" ? "dev" : BuildInfo.Tag;
                    settings.UserAgentString = original + $" DoLLauncher/{version} (Android)";
                }

                settings.JavaScriptEnabled = true;
                settings.DomStorageEnabled = true;
                settings.AllowFileAccess = true;
            }

#if DEBUG
            // 方便在 chrome://inspect 中排查页面白屏 / 渲染问题
            AndroidWebView.SetWebContentsDebuggingEnabled(true);
#endif

            var client = new GameWebViewClient(view, openExternal, onError, onReady, onClose);
            view.SetWebViewClient(client);
            view.SetWebChromeClient(new GameChromeClient());
            view.SetDownloadListener(new GameDownloadListener(openExternal));
            view.AddJavascriptInterface(new GameJsBridge(client), "DoL");
        }
        catch (Exception ex)
        {
            LogService.Error("初始化游戏 WebView 失败", ex);
        }
    }
}

internal sealed class GameWebViewClient : AndroidWebViewClient
{
    private readonly AndroidWebView _view;
    private readonly Action<string> _openExternal;
    private readonly Action<string> _onError;
    private readonly Action _onReady;
    private readonly Action _onClose;
    private bool _mainFrameError;

    public GameWebViewClient(AndroidWebView view, Action<string> openExternal, Action<string> onError,
        Action onReady, Action onClose)
    {
        _view = view;
        _openExternal = openExternal;
        _onError = onError;
        _onReady = onReady;
        _onClose = onClose;
    }

    public string LauncherVersion => BuildInfo.Tag == "dev" ? $"dev-{BuildInfo.Commit}" : BuildInfo.Tag;

    /// <summary>JS 桥来源校验：仅本机与虚拟资源域名允许调用。</summary>
    public bool IsTrustedOrigin()
    {
        var url = _view.Url;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return IsInternalHost(uri.Host);
    }

    public void NotifyReadyFromPage()
    {
        if (!IsTrustedOrigin())
        {
            LogService.Append("忽略非白名单页面的 DoL.notifyReady 调用");
            return;
        }
        _view.Post(() => _onReady());
    }

    public void CloseFromPage()
    {
        if (!IsTrustedOrigin())
        {
            LogService.Append("忽略非白名单页面的 DoL.close 调用");
            return;
        }
        _view.Post(() => _onClose());
    }

    public override AndroidWebResourceResponse? ShouldInterceptRequest(AndroidWebView? view,
        AndroidWebResourceRequest? request)
    {
        var url = request?.Url?.ToString();
        if (GameAssetHost.TryServe(url, out var response)) return response;
        return base.ShouldInterceptRequest(view, request);
    }

    public override bool ShouldOverrideUrlLoading(AndroidWebView? view, AndroidWebResourceRequest? request)
    {
        return HandleNavigation(request?.Url?.ToString() ?? string.Empty);
    }

    public override void OnReceivedError(AndroidWebView? view, AndroidWebResourceRequest? request,
        AndroidWebResourceError? error)
    {
        var url = request?.Url?.ToString() ?? string.Empty;
        var description = error?.Description?.ToString() ?? string.Empty;
        var code = error == null ? 0 : (int)error.ErrorCode;

        if (request?.IsForMainFrame == true)
        {
            _mainFrameError = true;
            LogService.Append($"游戏页主框架加载失败：{code} {description} {url}");
            _view.Post(() => _onError($"加载失败（{code}）：{description}\n{url}"));
            return;
        }

        LogService.Append($"游戏页子资源加载失败：{code} {description} {url}");
    }

    public override void OnPageStarted(AndroidWebView? view, string? url, Android.Graphics.Bitmap? favicon)
    {
        _mainFrameError = false;
        base.OnPageStarted(view, url, favicon);
    }

    public override void OnPageFinished(AndroidWebView? view, string? url)
    {
        base.OnPageFinished(view, url);
        // 主框架失败时保留错误层，等待用户点击重试
        if (_mainFrameError) return;
        _view.Post(() => _onReady());
    }

    /// <summary>
    /// WebView 渲染进程被系统回收 / 崩溃时必须返回 true，
    /// 否则 Chromium 会主动终止整个应用（SIGTRAP）。
    /// </summary>
    public override bool OnRenderProcessGone(AndroidWebView? view, AndroidRenderProcessGoneDetail? detail)
    {
        var crashed = detail != null && OperatingSystem.IsAndroidVersionAtLeast(26) && detail.DidCrash();
        LogService.Append($"游戏页 WebView 渲染进程退出（crash={crashed}），已切换到错误提示。");
        _mainFrameError = true;
        _view.Post(() => _onError(
            "网页渲染进程已退出，可能是内存不足或系统限制。\n可以点击重试，或用浏览器打开。"));
        return true;
    }

    private bool HandleNavigation(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsInternalHost(uri.Host))
                return false; // 本机 / 虚拟资源域名继续内嵌

            LogService.Append($"游戏页外链交给系统浏览器：{url}");
            _openExternal(url);
            return true;
        }

        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Append($"游戏页出现写死的 file:// 绝对路径，WebView 无法加载：{url}");
            return false;
        }

        LogService.Append($"游戏页外部 Scheme 交给系统处理：{url}");
        _openExternal(url);
        return true;
    }

    private static bool IsInternalHost(string host) =>
        host.Equals(GameAssetHost.Host, StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}

/// <summary>window.DoL：只暴露 notifyReady / close / getVersion 三个受控方法。</summary>
internal sealed class GameJsBridge : Java.Lang.Object
{
    private readonly GameWebViewClient _client;

    public GameJsBridge(GameWebViewClient client) => _client = client;

    [AndroidJavascriptInterface]
    [Java.Interop.Export("notifyReady")]
    public void NotifyReady() => _client?.NotifyReadyFromPage();

    [AndroidJavascriptInterface]
    [Java.Interop.Export("close")]
    public void Close() => _client?.CloseFromPage();

    [AndroidJavascriptInterface]
    [Java.Interop.Export("getVersion")]
    public string GetVersion() => _client?.LauncherVersion ?? "dev";
}

internal sealed class GameChromeClient : AndroidWebChromeClient
{
    public override void OnShowCustomView(AndroidView? view, AndroidCustomViewCallback? callback)
    {
        if (view == null)
        {
            callback?.OnCustomViewHidden();
            return;
        }

        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView is not AndroidViewGroup decor)
        {
            callback?.OnCustomViewHidden();
            return;
        }

        GameFullscreen.Show(decor, view, callback);
    }

    public override void OnHideCustomView() => GameFullscreen.Hide();
}

internal sealed class GameDownloadListener : Java.Lang.Object, AndroidDownloadListener
{
    private readonly Action<string> _openExternal;

    public GameDownloadListener(Action<string> openExternal) => _openExternal = openExternal;

    public void OnDownloadStart(string? url, string? userAgent, string? contentDisposition, string? mimetype,
        long contentLength)
    {
        LogService.Append($"游戏页下载请求转系统浏览器：{url}（{mimetype}，{contentLength} 字节）");
        if (!string.IsNullOrWhiteSpace(url)) _openExternal(url);
    }
}

/// <summary>视频 / 网页全屏：把自定义 View 挂到 DecorView 并隐藏系统栏，退出时恢复。</summary>
internal static class GameFullscreen
{
    private static AndroidView? _view;
    private static AndroidViewGroup? _decor;
    private static AndroidCustomViewCallback? _callback;

    public static bool IsActive => _view != null;

    public static void Show(AndroidViewGroup decor, AndroidView view, AndroidCustomViewCallback? callback)
    {
        Hide();
        _view = view;
        _decor = decor;
        _callback = callback;

        view.SetBackgroundColor(Android.Graphics.Color.Black);
        decor.AddView(view, new AndroidViewGroup.LayoutParams(
            AndroidViewGroup.LayoutParams.MatchParent,
            AndroidViewGroup.LayoutParams.MatchParent));
        SetSystemBarsHidden(true);
    }

    public static void Hide()
    {
        var view = _view;
        var decor = _decor;
        var callback = _callback;
        _view = null;
        _decor = null;
        _callback = null;

        if (view != null && decor != null)
        {
            try { decor.RemoveView(view); }
            catch { /* 视图已被系统移除时忽略 */ }
        }

        callback?.OnCustomViewHidden();
        SetSystemBarsHidden(false);
    }

    private static void SetSystemBarsHidden(bool hidden)
    {
        try
        {
            var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
            if (window?.DecorView == null) return;

            var controller = new WindowInsetsControllerCompat(window, window.DecorView);
            if (hidden)
            {
                controller.Hide(WindowInsetsCompat.Type.SystemBars());
                controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            }
            else
            {
                controller.Show(WindowInsetsCompat.Type.SystemBars());
            }
        }
        catch (Exception ex)
        {
            LogService.Error("切换游戏全屏失败", ex);
        }
    }
}
