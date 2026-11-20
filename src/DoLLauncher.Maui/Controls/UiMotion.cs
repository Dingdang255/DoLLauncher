using DoLLauncher.Services;

namespace DoLLauncher.Maui.Controls;

/// <summary>统一的动效开关：设置里关闭「界面过渡动画」后所有淡入 / 位移都跳过。</summary>
internal static class UiMotion
{
    public static bool Enabled => AppConfig.Load().General.ShowAnimations;

    /// <summary>折叠面板展开 / 收起：淡入淡出 + 轻微纵向位移，收起结束后再隐藏控件。</summary>
    public static async Task TogglePanelAsync(View body, bool expand, double offset = 8)
    {
        if (!Enabled)
        {
            body.IsVisible = expand;
            body.Opacity = 1;
            body.TranslationY = 0;
            return;
        }

        if (expand)
        {
            body.IsVisible = true;
            body.Opacity = 0;
            body.TranslationY = -offset;
            await Task.WhenAll(
                body.FadeToAsync(1, 140, Easing.CubicOut),
                body.TranslateToAsync(0, 0, 160, Easing.CubicOut));
        }
        else
        {
            await Task.WhenAll(
                body.FadeToAsync(0, 110, Easing.CubicIn),
                body.TranslateToAsync(0, -offset, 120, Easing.CubicIn));
            body.IsVisible = false;
            body.Opacity = 1;
            body.TranslationY = 0;
        }
    }

    /// <summary>箭头旋转，与面板展开状态同步。</summary>
    public static async Task RotateAsync(VisualElement view, double target, uint length = 160)
    {
        if (!Enabled)
        {
            view.Rotation = target;
            return;
        }
        await view.RotateToAsync(target, length, Easing.CubicOut);
    }
}
