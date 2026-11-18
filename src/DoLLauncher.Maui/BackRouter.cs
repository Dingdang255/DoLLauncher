namespace DoLLauncher.Maui;

/// <summary>
/// Android 返回键路由：MAUI 10 不再调用 Page.OnBackButtonPressed，
/// 由当前页面注册处理器，未处理时交给默认导航逻辑。
/// </summary>
public static class BackRouter
{
    public static Func<bool>? Handler { get; set; }
}
