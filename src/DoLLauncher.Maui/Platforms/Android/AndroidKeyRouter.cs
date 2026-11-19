using Android.Views;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 外接键盘事件路由：MainActivity 先询问当前页面是否消费按键（Ctrl+A / Delete），
/// 输入框获得焦点时不拦截，避免影响正常文字输入。
/// </summary>
internal static class AndroidKeyRouter
{
    /// <summary>当前页面的按键处理器；返回 true 表示已消费。</summary>
    public static Func<Android.Views.KeyEvent, bool>? Handler { get; set; }

    public static bool Dispatch(Android.Views.KeyEvent e)
    {
        if (e.Action != KeyEventActions.Down) return false;

        // 输入框（重命名、提示框里的 EditText）优先，不要让快捷键抢走按键。
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity?.CurrentFocus is Android.Widget.EditText) return false;

        try
        {
            return Handler?.Invoke(e) == true;
        }
        catch (Exception ex)
        {
            DoLLauncher.Services.LogService.Error("处理外接键盘按键失败", ex);
            return false;
        }
    }
}
