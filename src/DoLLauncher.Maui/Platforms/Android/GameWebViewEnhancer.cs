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

    /// <summary>
    /// 游戏内的 &lt;input type="file"&gt;（导入存档等）默认不会做任何事，
    /// 必须在这里拉起系统文件选择器，否则点按钮没有任何反应。
    /// </summary>
    public override bool OnShowFileChooser(Android.Webkit.WebView? webView,
        Android.Webkit.IValueCallback? filePathCallback,
        Android.Webkit.WebChromeClient.FileChooserParams? fileChooserParams)
        => GameFileChooser.TryShow(filePathCallback, fileChooserParams);
}

/// <summary>游戏 WebView 的文件选择：拉起系统文件管理器并把结果回传给网页。</summary>
internal static class GameFileChooser
{
    private const int RequestCode = 47110;

    private static Android.Webkit.IValueCallback? _callback;

    public static bool TryShow(Android.Webkit.IValueCallback? callback,
        Android.Webkit.WebChromeClient.FileChooserParams? parameters)
    {
        if (callback == null) return false;

        // 上一次没结果就被取消时先回空，避免 WebView 一直等待。
        DropPending();

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity == null)
        {
            callback.OnReceiveValue(null);
            return false;
        }

        Android.Content.Intent intent;
        try
        {
            intent = parameters?.CreateIntent()
                     ?? new Android.Content.Intent(Android.Content.Intent.ActionGetContent)
                         .SetType("*/*");
            if (parameters == null) intent.AddCategory(Android.Content.Intent.CategoryOpenable);
        }
        catch (Exception ex)
        {
            LogService.Error("创建文件选择意图失败", ex);
            callback.OnReceiveValue(null);
            return false;
        }

        _callback = callback;
        try
        {
            activity.StartActivityForResult(intent, RequestCode);
            LogService.Append("[游戏页] 已拉起系统文件选择器");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("拉起系统文件选择器失败", ex);
            DropPending();
            callback.OnReceiveValue(null);
            return false;
        }
    }

    /// <summary>由 MainActivity.OnActivityResult 调用；返回 true 表示是本模块的请求。</summary>
    public static bool HandleResult(int requestCode, Android.App.Result resultCode,
        Android.Content.Intent? data)
    {
        if (requestCode != RequestCode) return false;

        var callback = _callback;
        _callback = null;
        if (callback == null) return true;

        Android.Net.Uri[]? uris = null;
        if (resultCode == Android.App.Result.Ok && data != null)
            uris = AndroidWebChromeClient.FileChooserParams.ParseResult((int)resultCode, data);

        LogService.Append(uris is { Length: > 0 }
            ? $"[游戏页] 已选择 {uris.Length} 个文件"
            : "[游戏页] 未选择文件");
        callback.OnReceiveValue(uris);
        return true;
    }

    private static void DropPending()
    {
        var pending = _callback;
        _callback = null;
        if (pending == null) return;
        try { pending.OnReceiveValue(null); } catch { }
    }
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
        // 全屏退出后按游戏页状态恢复：游戏页仍在时只恢复导航栏，状态栏继续保持收起。
        GameSystemBars.Apply();
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

/// <summary>
/// 游戏页的系统栏状态：进入游戏页后收起状态栏，避免覆盖左侧栏顶部的收起/展开按钮；
/// 返回启动器时恢复。网页全屏退出后同样只恢复导航栏，状态栏保持收起。
/// </summary>
internal static class GameSystemBars
{
    public static bool StatusBarHidden { get; private set; }

    public static void EnterGame()
    {
        StatusBarHidden = true;
        Apply();
    }

    public static void ExitGame()
    {
        StatusBarHidden = false;
        Apply();
    }

    /// <summary>按当前状态应用系统栏：游戏页只收起状态栏并保留导航栏。</summary>
    public static void Apply()
    {
        try
        {
            var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
            if (window?.DecorView == null) return;

            var controller = new WindowInsetsControllerCompat(window, window.DecorView);
            controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            if (StatusBarHidden)
            {
                controller.Hide(WindowInsetsCompat.Type.StatusBars());
                controller.Show(WindowInsetsCompat.Type.NavigationBars());
            }
            else
            {
                controller.Show(WindowInsetsCompat.Type.SystemBars());
            }
        }
        catch (Exception ex)
        {
            LogService.Error("切换游戏状态栏失败", ex);
        }
    }
}
