namespace DoLLauncher.Platform.Abstractions;

/// <summary>
/// 统一的消息提示入口，让应用服务层不直接依赖具体 UI 框架。
/// 全部为异步契约：桌面框架的输入循环必须保持运行，不能在 UI 线程上同步等待弹窗。
/// </summary>
public interface IDialogHost
{
    /// <summary>普通提示，只有一个「确定」。</summary>
    Task InfoAsync(string message, string title = "DoL 启动器");

    /// <summary>需要注意的提示（橙色标识）。</summary>
    Task WarnAsync(string message, string title = "DoL 启动器");

    /// <summary>错误提示（红色标识）。</summary>
    Task ErrorAsync(string message, string title = "DoL 启动器");

    /// <summary>确认框：返回 true 表示用户点了确认。</summary>
    Task<bool> ConfirmAsync(string message, string title = "DoL 启动器",
        string confirmText = "确定", bool danger = false);
}
