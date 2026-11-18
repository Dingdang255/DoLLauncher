namespace DoLLauncher.Platform.Abstractions;

/// <summary>
/// 本地静态服务器的平台抽象：桌面端由 <c>StaticServer</c>（TcpListener）实现，
/// Android 端可替换为应用内可用的监听实现或直接走 WebView 本地资源拦截。
/// </summary>
public interface ILocalServer : IDisposable
{
    /// <summary>监听端口。</summary>
    int Port { get; }

    /// <summary>是否正在监听。</summary>
    bool IsListening { get; }

    /// <summary>游戏页面地址。</summary>
    string GameUrl { get; }

    /// <summary>存档助手页面地址。</summary>
    string SaveHelperUrl { get; }

    /// <summary>最近一次页面心跳时间（UTC）；没有收到心跳时为 null。</summary>
    DateTime? LastHeartbeatUtc { get; }

    /// <summary>是否还有页面开着。</summary>
    bool HasPages { get; }

    /// <summary>最后一个页面主动上报关闭的时间（UTC）。</summary>
    DateTime? PageClosedUtc { get; }

    /// <summary>开始监听。</summary>
    void Start();

    /// <summary>停止监听。</summary>
    void Stop();
}
