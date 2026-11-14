using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DoLLauncher.Core.Diagnostics;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>
/// 游戏日志的报错归纳：把 logs\game-*.log 里的 warning / error / reject 分组，
/// 给出次数、出现时间、完整内容与可能原因，便于自己或他人（含 AI）快速定位。
/// </summary>
public static class GameLogAnalysis
{
    private sealed class Finding
    {
        public string Level = "error";
        public int Count;
        public string First = string.Empty;
        public string Last = string.Empty;
        public string Sample = string.Empty;
    }

    /// <summary>分析结果：头部信息、错误组、警告组、结尾提示，分段返回便于流式展示。</summary>
    private sealed class LogReport
    {
        public List<string> Header { get; } = new();
        public List<string> ErrorBlocks { get; } = new();
        public List<string> WarnBlocks { get; } = new();
        public List<string> Footer { get; } = new();

        public IEnumerable<string> Blocks()
        {
            foreach (var line in Header) yield return line;
            if (ErrorBlocks.Count > 0)
            {
                yield return "";
                yield return "== 错误（error / reject） ==";
                foreach (var block in ErrorBlocks)
                {
                    yield return block;
                    yield return "";
                }
            }
            if (WarnBlocks.Count > 0)
            {
                yield return "";
                yield return "== 警告（warn） ==";
                foreach (var block in WarnBlocks)
                {
                    yield return block;
                    yield return "";
                }
            }
            foreach (var line in Footer) yield return line;
        }

        public string ToText() => string.Join(Environment.NewLine, Blocks());
    }

    private static readonly Regex LinePattern = new(@"^(\S+) \[(log|info|warn|error|debug|reject)\] (.*)$",
        RegexOptions.Compiled);

    /// <summary>同步生成完整分析文本（用于导出 analysis.txt）。</summary>
    public static string Analyze(VersionInfo version, int maxFiles = 5) => BuildReport(version, maxFiles).ToText();

    /// <summary>
    /// 流式分析：先把文件读进内存并在后台线程分组，然后一段一段地交给界面，
    /// 避免一次塞入超长文本导致诊断页卡死。
    /// </summary>
    public static async Task AnalyzeStreamAsync(VersionInfo version, Action<string> append,
        int maxFiles = 5, CancellationToken ct = default)
    {
        var report = await Task.Run(() => BuildReport(version, maxFiles), ct);
        foreach (var block in report.Blocks())
        {
            ct.ThrowIfCancellationRequested();
            append(block);
            await Task.Delay(1, ct);
        }
    }

    private static LogReport BuildReport(VersionInfo version, int maxFiles)
    {
        var report = new LogReport();
        report.Header.Add("== 游戏日志报错分析 ==");
        report.Header.Add($"版本：{version.DisplayName}（{version.Folder}）");
        report.Header.Add($"时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        report.Header.Add("");

        var dir = GameLogService.Dir(version.FullPath);
        if (!Directory.Exists(dir))
        {
            report.Header.Add("还没有游戏日志目录：请先在启动器里启动一次游戏。");
            return report;
        }

        var files = new DirectoryInfo(dir).GetFiles("game-*.log")
            .OrderByDescending(f => f.LastWriteTime)
            .Take(Math.Max(1, maxFiles))
            .ToList();
        if (files.Count == 0)
        {
            report.Header.Add("日志目录里还没有 game-*.log 文件。");
            return report;
        }

        report.Header.Add("分析文件：" + string.Join("、", files.Select(f => f.Name)));
        report.Header.Add("");

        var findings = new Dictionary<string, Finding>(StringComparer.Ordinal);
        int warnings = 0, errors = 0, rejects = 0, total = 0;

        foreach (var file in files)
        {
            try
            {
                foreach (var raw in File.ReadLines(file.FullName, Encoding.UTF8))
                {
                    total++;
                    var m = LinePattern.Match(raw);
                    if (!m.Success) continue;
                    var level = m.Groups[2].Value.ToLowerInvariant();
                    if (level != "warn" && level != "error" && level != "reject") continue;
                    if (level == "warn") warnings++;
                    else if (level == "error") errors++;
                    else rejects++;

                    var message = m.Groups[3].Value;
                    var key = message.Length > 160 ? message[..160] : message;
                    if (!findings.TryGetValue(key, out var finding))
                    {
                        finding = new Finding { Level = level, First = m.Groups[1].Value, Sample = Clip(message) };
                        findings[key] = finding;
                    }
                    finding.Count++;
                    finding.Last = m.Groups[1].Value;
                    if (message.Length > finding.Sample.Length) finding.Sample = Clip(message);
                }
            }
            catch (Exception ex)
            {
                report.Header.Add($"读取 {file.Name} 失败：{ex.Message}");
            }
        }

        report.Header.Add($"共读取 {total} 行：error {errors} 条，reject {rejects} 条，warning {warnings} 条。");
        report.Header.Add($"同类问题共 {findings.Count} 组，按级别与出现次数排列。");

        if (findings.Count == 0)
        {
            report.Header.Add("");
            report.Header.Add("[完成] 没有发现 warning / error / reject 记录。");
            return report;
        }

        // 错误（reject / error）优先，然后才是警告；同级按次数从多到少
        var ordered = findings.Values
            .OrderBy(f => LevelRank(f.Level))
            .ThenByDescending(f => f.Count)
            .ToList();

        var index = 0;
        foreach (var f in ordered)
        {
            index++;
            var block = new StringBuilder();
            block.AppendLine($"--- 问题 {index}（{f.Count} 次，{f.Level}）---");
            block.AppendLine("时间：" + (f.First == f.Last ? f.First : f.First + " ~ " + f.Last));
            block.AppendLine("内容：" + f.Sample);
            foreach (var hint in Explain(f.Sample)) block.AppendLine("可能原因：" + hint);
            block.AppendLine();
            var text = block.ToString().TrimEnd();

            if (f.Level == "warn") report.WarnBlocks.Add(text);
            else report.ErrorBlocks.Add(text);
        }

        report.Footer.Add("提示：把导出的诊断包发给别人或 AI 时，logs\\game-*.log 与 analysis.txt 就是分析依据。");
        return report;
    }

    private static int LevelRank(string level) => level switch
    {
        "reject" => 0,
        "error" => 1,
        _ => 2,
    };

    /// <summary>
    /// 单条样例最多保留 1200 字符：游戏里一条报错可能塞进整份模组源码（几十万字符），
    /// 原样显示会让诊断页排版极慢，这里截断并注明，完整内容仍在 logs 目录里。
    /// </summary>
    private const int SampleLimit = 1200;

    private static string Clip(string text) =>
        text.Length <= SampleLimit
            ? text
            : text[..SampleLimit] + $"…（已省略 {text.Length - SampleLimit} 字符，完整内容见 logs）";

    private static IEnumerable<string> Explain(string message)
    {
        var hints = GameLogRules.Classify(message, GameLogLevel.Information).Hints;
        return hints.Count > 0
            ? hints
            : new[] { "没有匹配到已知模式，请对照上面完整内容，并查看日志前后几行确认触发场景。" };
    }
}
