using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Platform.Windows;

/// <summary>把日志写入委托给现有的 LogService，保持落盘行为不变。</summary>
public sealed class FileLogSink : ILogSink
{
    public void Write(string message) => LogService.Append(message);
}
