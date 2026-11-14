using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Platform.Windows;

/// <summary>把平台层消息提示委托给自绘的 AppDialog。</summary>
public sealed class WindowsDialogHost : IDialogHost
{
    public void Info(string message, string title = AppDialog.DefaultTitle) => AppDialog.Info(message, title);

    public void Warn(string message, string title = AppDialog.DefaultTitle) => AppDialog.Warn(message, title);

    public void Error(string message, string title = AppDialog.DefaultTitle) => AppDialog.Error(message, title);

    public bool Confirm(string message, string title = AppDialog.DefaultTitle,
        string confirmText = "确定", bool danger = false) =>
        AppDialog.Confirm(message, title, confirmText, danger);
}
