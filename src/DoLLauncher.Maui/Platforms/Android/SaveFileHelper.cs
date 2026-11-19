using Android.App;
using Android.Content;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>SAF 保存的结果：已保存 / 用户取消 / 当前环境不支持。</summary>
public enum SaveFileResult
{
    Saved,
    Canceled,
    Unsupported,
}

/// <summary>
/// 用 SAF（ACTION_CREATE_DOCUMENT）让用户选择保存位置，再把应用缓存里的文件复制过去。
/// 结果由 MainActivity.OnActivityResult 转交给 <see cref="HandleResult"/>。
/// </summary>
public static class SaveFileHelper
{
    private const int RequestCode = 47001;

    private static TaskCompletionSource<SaveFileResult>? _pending;
    private static string? _sourcePath;

    /// <summary>没有正在进行的保存时才能发起新的保存。</summary>
    public static bool IsBusy => _pending != null;

    public static Task<SaveFileResult> SaveAsync(string sourcePath, string fileName,
        string mimeType = "application/octet-stream")
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity == null || _pending != null) return Task.FromResult(SaveFileResult.Unsupported);

        try
        {
            var tcs = new TaskCompletionSource<SaveFileResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            _sourcePath = sourcePath;

            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType(mimeType);
            intent.PutExtra(Intent.ExtraTitle, fileName);
            activity.StartActivityForResult(intent, RequestCode);
            return tcs.Task;
        }
        catch (Exception ex)
        {
            LogService.Error("打开系统保存对话框失败", ex);
            _pending = null;
            _sourcePath = null;
            return Task.FromResult(SaveFileResult.Unsupported);
        }
    }

    /// <summary>MainActivity 收到结果时调用；返回 false 表示不是本帮助类发起的请求。</summary>
    public static bool HandleResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != RequestCode) return false;

        var pending = _pending;
        var source = _sourcePath;
        _pending = null;
        _sourcePath = null;
        if (pending == null) return true;

        var uri = data?.Data;
        if (resultCode != Result.Ok || uri == null || source == null || !File.Exists(source))
        {
            pending.TrySetResult(SaveFileResult.Canceled);
            return true;
        }

        _ = CopyAsync(pending, source, uri);
        return true;
    }

    private static async Task CopyAsync(TaskCompletionSource<SaveFileResult> pending,
        string sourcePath, Android.Net.Uri target)
    {
        try
        {
            await Task.Run(() =>
            {
                var resolver = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.ContentResolver
                               ?? throw new IOException("无法访问系统文件服务。");
                using var input = File.OpenRead(sourcePath);
                using var output = resolver.OpenOutputStream(target)
                                   ?? throw new IOException("无法写入所选位置。");
                input.CopyTo(output);
            });
            pending.TrySetResult(SaveFileResult.Saved);
        }
        catch (Exception ex)
        {
            LogService.Error("复制导出文件到所选位置失败", ex);
            pending.TrySetResult(SaveFileResult.Unsupported);
        }
    }
}
