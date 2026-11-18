namespace DoLLauncher.Platform.Abstractions;

/// <summary>日志写入出口。</summary>
public interface ILogSink
{
    /// <summary>写入一条日志。</summary>
    void Write(string message);

    /// <summary>写入一条错误日志；实现方按「日志详细度」决定是否附带完整堆栈。</summary>
    void WriteError(string context, Exception ex);
}
