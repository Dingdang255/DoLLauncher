using System.IO;
using System.Text;

namespace DoLLauncher.Services;

/// <summary>日志级别：仅告警与错误会带级别标记，信息级保持原有纯文本格式。</summary>
public enum LogLevel
{
    Info = 0,
    Warn = 1,
    Error = 2,
}

/// <summary>把启动器的运行记录同时落盘，便于诊断时查看和打包。</summary>
public static class LogService
{
    private static readonly object Sync = new();
    private static volatile bool _detailed;

    public static string FilePath => Path.Combine(Paths.Root, "logs", "launcher.log");

    /// <summary>未处理异常的全量记录（含堆栈与内部异常链），独立于 launcher.log。</summary>
    public static string ErrorFilePath => Path.Combine(Paths.Root, "launcher-error.log");

    /// <summary>
    /// 详细模式：错误会连同完整堆栈、内部异常与调用链写入日志。
    /// 由设置页的「日志详细度」控制，关闭时行为与旧版一致（只记 Message）。
    /// </summary>
    public static bool Detailed
    {
        get => _detailed;
        set => _detailed = value;
    }

    /// <summary>按配置项（standard / detailed）切换详细模式。</summary>
    public static void Configure(string? verbosity) => Detailed = IsDetailed(verbosity);

    public static bool IsDetailed(string? verbosity) =>
        string.Equals(verbosity, "detailed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verbosity, "verbose", StringComparison.OrdinalIgnoreCase);

    /// <summary>写入一条信息级日志。</summary>
    public static void Append(string message) => Write(LogLevel.Info, message);

    /// <summary>写入一条告警级日志。</summary>
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    /// <summary>
    /// 记录一次失败。详细模式下追加完整异常（调用链、堆栈与内部异常），
    /// 标准模式下只保留 Message，避免日志体积失控。
    /// </summary>
    public static void Error(string context, Exception? ex = null)
    {
        if (ex == null)
        {
            Write(LogLevel.Error, context);
            return;
        }

        Write(LogLevel.Error, Detailed
            ? context + Environment.NewLine + ex
            : context + "：" + ex.Message);
    }

    /// <summary>
    /// 把未处理异常的完整内容（堆栈 + 内部异常链）追加到 launcher-error.log。
    /// 该文件不参与日志详细度开关，任何情况下都保留全量信息。
    /// </summary>
    public static void Crash(Exception ex)
    {
        try
        {
            var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                       ex + Environment.NewLine + Environment.NewLine;
            lock (Sync) File.AppendAllText(ErrorFilePath, text, new UTF8Encoding(false));
        }
        catch
        {
            // 日志写失败不影响主流程
        }
    }

    private static void Write(LogLevel level, string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var tag = level == LogLevel.Info ? "" : "[" + level.ToString().ToUpperInvariant() + "] ";
            var builder = new StringBuilder();
            var normalized = (message ?? string.Empty).Replace("\r\n", "\n");
            foreach (var line in normalized.Split('\n'))
            {
                // 多行（堆栈）续行缩进，保留每行都属于同一条日志的观感
                builder.Append(builder.Length == 0
                        ? $"[{stamp}] {tag}{line}"
                        : "    " + line)
                    .Append(Environment.NewLine);
            }

            lock (Sync) File.AppendAllText(FilePath, builder.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 日志写失败不影响主流程
        }
    }
}
