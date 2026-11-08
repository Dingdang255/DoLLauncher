using System.IO;
using System.Text;

namespace DoLLauncher.Services;

/// <summary>把启动器的运行记录同时落盘，便于诊断时查看和打包。</summary>
public static class LogService
{
    private static readonly object Sync = new();

    public static string FilePath => Path.Combine(Paths.Root, "logs", "launcher.log");

    public static void Append(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (dir != null) Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            lock (Sync) File.AppendAllText(FilePath, line, new UTF8Encoding(false));
        }
        catch
        {
            // 日志写失败不影响主流程
        }
    }

    public static string ReadTail(int lines = 400)
    {
        if (!File.Exists(FilePath)) return "（还没有启动器日志）";
        try
        {
            var all = File.ReadAllLines(FilePath, Encoding.UTF8);
            var start = Math.Max(0, all.Length - lines);
            return string.Join(Environment.NewLine, all[start..]);
        }
        catch (Exception ex)
        {
            return "读取日志失败：" + ex.Message;
        }
    }
}
