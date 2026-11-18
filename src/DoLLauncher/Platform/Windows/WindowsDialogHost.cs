using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Platform.Windows;

/// <summary>把平台层消息提示委托给自绘的 AppDialog。</summary>
public sealed class WindowsDialogHost : IDialogHost
{
    public Task InfoAsync(string message, string title = AppDialog.DefaultTitle)
    {
        AppDialog.Info(message, title);
        return Task.CompletedTask;
    }

    public Task WarnAsync(string message, string title = AppDialog.DefaultTitle)
    {
        AppDialog.Warn(message, title);
        return Task.CompletedTask;
    }

    public Task ErrorAsync(string message, string title = AppDialog.DefaultTitle)
    {
        AppDialog.Error(message, title);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string message, string title = AppDialog.DefaultTitle,
        string confirmText = "确定", bool danger = false) =>
        Task.FromResult(AppDialog.Confirm(message, title, confirmText, danger));
}
