using DoLLauncher.Services;

namespace DoLLauncher.Maui.Controls;

/// <summary>统一的动效开关：设置里关闭「界面过渡动画」后所有淡入 / 位移都跳过。</summary>
internal static class UiMotion
{
    public static bool Enabled => AppConfig.Load().General.ShowAnimations;
}
