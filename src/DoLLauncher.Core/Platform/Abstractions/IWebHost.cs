namespace DoLLauncher.Platform.Abstractions;

/// <summary>
/// 应用内网页承载抽象，供 Android 端用 WebView 打开游戏页面。
/// 桌面端继续使用外部浏览器承载，不实现该接口。
/// </summary>
public interface IWebHost : IDisposable
{
    /// <summary>页面是否正在显示。</summary>
    bool IsOpen { get; }

    /// <summary>在应用内打开指定地址。</summary>
    void Show(string url);

    /// <summary>关闭应用内页面。</summary>
    void Close();
}
