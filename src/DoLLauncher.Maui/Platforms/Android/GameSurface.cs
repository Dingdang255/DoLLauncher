using Android.App;
using Android.Views;
using Android.Widget;
using DoLLauncher.Services;
using AndroidWebView = Android.Webkit.WebView;
using AndroidViewGroup = Android.Views.ViewGroup;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 游戏页的常驻 WebView 宿主。对应 WPF 端「游戏页跑在独立浏览器窗口里」：
/// 返回启动器只是把画面隐藏，页面和服务器都还活着，再进来还是同一局游戏。
/// 只有明确关闭游戏 / 停止服务器时才销毁。
/// </summary>
internal static class GameSurface
{
    private static AndroidWebView? _view;
    private static FrameLayout? _overlay;
    private static string? _folder;

    /// <summary>页面回调：每次进入游戏页时由 GamePage 重新赋值，避免指向已关闭的旧页面。</summary>
    public static Action<string>? OpenExternal;
    public static Action<string>? Error;
    public static Action? Ready;
    public static Action? CloseRequested;

    public static AndroidWebView? View => _view;

    public static bool HasSession(string folder) =>
        _view != null && string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 确保该版本有一个游戏 WebView：同版本直接复用（不重新加载），
    /// 换版本或上次会话已销毁时新建。返回是否复用了已有会话。
    /// </summary>
    public static bool Enter(Activity activity, string folder, string url, bool fileMode)
    {
        if (HasSession(folder))
        {
            Show();
            return true;
        }

        DisposeSession();

        var decor = activity.Window?.DecorView as AndroidViewGroup
            ?? throw new InvalidOperationException("找不到 Activity 的根视图");

        var overlay = new FrameLayout(activity);
        overlay.SetBackgroundColor(Android.Graphics.Color.Black);
        var view = new AndroidWebView(activity);

        GameWebViewEnhancer.Attach(view,
            u => OpenExternal?.Invoke(u),
            m => Error?.Invoke(m),
            () => Ready?.Invoke(),
            () => CloseRequested?.Invoke());

        overlay.AddView(view, new FrameLayout.LayoutParams(
            AndroidViewGroup.LayoutParams.MatchParent, AndroidViewGroup.LayoutParams.MatchParent));

        _overlay = overlay;
        _view = view;
        _folder = folder;

        decor.AddView(overlay, new AndroidViewGroup.LayoutParams(
            AndroidViewGroup.LayoutParams.MatchParent, AndroidViewGroup.LayoutParams.MatchParent));

        // http 版本追加一次性参数，避免 WebView 复用缓存里的旧页面（旧页面没有最新的注入脚本）。
        view.LoadUrl(fileMode ? url : AppendCacheBust(url));
        return false;
    }

    /// <summary>显示游戏画面（重新进入游戏页时调用）。</summary>
    public static void Show()
    {
        if (_overlay == null) return;
        try
        {
            var decor = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView as AndroidViewGroup;
            if (decor != null && !ReferenceEquals(_overlay.Parent, decor))
            {
                if (_overlay.Parent is AndroidViewGroup old) old.RemoveView(_overlay);
                decor.AddView(_overlay, new AndroidViewGroup.LayoutParams(
                    AndroidViewGroup.LayoutParams.MatchParent, AndroidViewGroup.LayoutParams.MatchParent));
            }
            _overlay.Visibility = ViewStates.Visible;
            _overlay.BringToFront();
        }
        catch (Exception ex)
        {
            LogService.Error("显示游戏画面失败", ex);
        }
    }

    /// <summary>隐藏游戏画面，WebView 与页面状态保持不变。</summary>
    public static void Hide()
    {
        if (_overlay == null) return;
        try { _overlay.Visibility = ViewStates.Gone; }
        catch (Exception ex) { LogService.Error("隐藏游戏画面失败", ex); }
    }

    /// <summary>销毁该版本的游戏会话；folder 为 null 时销毁当前会话。</summary>
    public static void CloseFor(string folder)
    {
        if (_folder != null && !string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase)) return;
        DisposeSession();
    }

    /// <summary>销毁 WebView 并移除覆盖层。</summary>
    public static void DisposeSession()
    {
        var view = _view;
        var overlay = _overlay;
        _view = null;
        _overlay = null;
        _folder = null;
        OpenExternal = null;
        Error = null;
        Ready = null;
        CloseRequested = null;

        try
        {
            if (overlay?.Parent is AndroidViewGroup parent) parent.RemoveView(overlay);
        }
        catch (Exception ex)
        {
            LogService.Error("移除游戏覆盖层失败", ex);
        }

        try { overlay?.RemoveAllViews(); } catch { }
        try { view?.Destroy(); } catch (Exception ex) { LogService.Error("销毁游戏 WebView 失败", ex); }
    }

    private static string AppendCacheBust(string url)
    {
        var sep = url.Contains('?') ? '&' : '?';
        return url + sep + "__t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
