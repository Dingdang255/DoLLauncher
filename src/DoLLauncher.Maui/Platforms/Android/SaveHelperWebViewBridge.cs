using Android.Runtime;
using DoLLauncher.Services;
using AndroidJavascriptInterface = Android.Webkit.JavascriptInterfaceAttribute;
using AndroidWebView = Android.Webkit.WebView;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 存档助手页的保存桥。Android WebView 里 a[download] / blob 下载不会落地，
/// 页面检测到 window.DoLSave 后改为调用 saveText，由这里写入缓存再走 SAF 保存对话框。
/// </summary>
internal static class SaveHelperWebViewBridge
{
    public static void Attach(AndroidWebView? view)
    {
        if (view == null) return;
        try
        {
            view.AddJavascriptInterface(new SavePageJsBridge(view), "DoLSave");
        }
        catch (Exception ex)
        {
            LogService.Error("初始化存档助手保存桥失败", ex);
        }
    }
}

internal sealed class SavePageJsBridge : Java.Lang.Object
{
    private readonly AndroidWebView _view;

    public SavePageJsBridge(AndroidWebView view) => _view = view;

    /// <summary>只接受本机启动器页面的调用，避免其它页面弹出保存对话框。</summary>
    private bool IsTrusted()
    {
        var url = _view.Url;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    [AndroidJavascriptInterface]
    [Java.Interop.Export("saveText")]
    public void SaveText(string fileName, string content)
    {
        if (!IsTrusted())
        {
            LogService.Append("忽略非本机页面的 DoLSave.saveText 调用");
            return;
        }
        if (string.IsNullOrWhiteSpace(fileName) || content == null) return;

        // JS 桥回调不在 UI 线程，SAF 对话框必须回到主线程发起。
        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => _ = SaveAsync(fileName, content));
    }

    private async Task SaveAsync(string fileName, string content)
    {
        string? temp = null;
        try
        {
            var safeName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "dol-save.txt";

            temp = Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, safeName);
            await File.WriteAllTextAsync(temp, content, new System.Text.UTF8Encoding(false));

            var mime = safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? "application/json"
                : "text/plain";
            LogService.Append($"存档助手导出 {safeName}（{content.Length} 字符），等待选择保存位置");

            var result = await SaveFileHelper.SaveAsync(temp, safeName, mime);
            var message = result switch
            {
                SaveFileResult.Saved => "已保存 " + safeName,
                SaveFileResult.Canceled => "已取消保存",
                _ => "无法保存文件",
            };
            LogService.Append("存档助手导出结果：" + message);
            ShowToast(message);
        }
        catch (Exception ex)
        {
            LogService.Error("存档助手导出文件失败", ex);
            ShowToast("保存失败：" + ex.Message);
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch { /* 缓存文件清理失败不影响结果 */ }
            }
        }
    }

    private static void ShowToast(string text)
    {
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity != null)
                Android.Widget.Toast.MakeText(activity, text, Android.Widget.ToastLength.Short)?.Show();
        }
        catch { /* Toast 失败只影响提示，不抛出 */ }
    }
}
