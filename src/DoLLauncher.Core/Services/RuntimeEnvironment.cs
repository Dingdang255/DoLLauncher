namespace DoLLauncher.Services;

/// <summary>
/// 运行环境的平台补充信息：由各前端在启动时填写，供设置页、诊断页与诊断包共享。
/// 桌面端没有 WebView，保持为空即可。
/// </summary>
public static class RuntimeEnvironment
{
    /// <summary>Android System WebView / Chrome 的 Chromium 版本号，例如 116.0.5845.92。</summary>
    public static string? WebViewVersion { get; set; }
}
