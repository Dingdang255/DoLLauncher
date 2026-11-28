namespace DoLLauncher.Maui.Controls;

/// <summary>
/// 给页面级遮罩补一个空手势，确保遮罩显示期间触摸不会穿透到下层控件。
/// Android 上带背景色的 Grid / BoxView 默认不消费触摸，只设 InputTransparent=false 并不够。
/// </summary>
internal static class ModalShield
{
    public static void BlockInput(View? view)
    {
        if (view == null || view.GestureRecognizers.Count > 0) return;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => { };
        view.GestureRecognizers.Add(tap);
    }
}
