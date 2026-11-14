namespace DoLLauncher.Platform.Abstractions;

/// <summary>统一的消息提示入口，让应用服务层不直接依赖 WPF 对话框。</summary>
public interface IDialogHost
{
    /// <summary>普通提示，只有一个「确定」。</summary>
    void Info(string message, string title = "DoL 启动器");

    /// <summary>需要注意的提示（橙色标识）。</summary>
    void Warn(string message, string title = "DoL 启动器");

    /// <summary>错误提示（红色标识）。</summary>
    void Error(string message, string title = "DoL 启动器");

    /// <summary>确认框：返回 true 表示用户点了确认。</summary>
    bool Confirm(string message, string title = "DoL 启动器",
        string confirmText = "确定", bool danger = false);
}
