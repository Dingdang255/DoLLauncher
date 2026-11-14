namespace DoLLauncher.Platform.Abstractions;

/// <summary>日志写入出口。</summary>
public interface ILogSink
{
    /// <summary>写入一条日志。</summary>
    void Write(string message);
}
