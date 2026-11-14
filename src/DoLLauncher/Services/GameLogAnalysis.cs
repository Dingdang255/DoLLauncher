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

    private static readonly Regex LinePattern = new(@"^(\S+) \[(log|info|warn|error|debug|reject)\] (.*)$",
        RegexOptions.Compiled);

    public static string Analyze(VersionInfo version, int maxFiles = 5)
    {
        var lines = new List<string>
        {
            "== 游戏日志报错分析 ==",
            $"版本：{version.DisplayName}（{version.Folder}）",
            $"时间：{DateTime.Now:yyyy-MM-dd HH:mm}",
            "",
        };

        var dir = GameLogService.Dir(version.FullPath);
        if (!Directory.Exists(dir))
        {
            lines.Add("还没有游戏日志目录：请先在启动器里启动一次游戏。");
            return string.Join(Environment.NewLine, lines);
        }

        var files = new DirectoryInfo(dir).GetFiles("game-*.log")
            .OrderByDescending(f => f.LastWriteTime)
            .Take(Math.Max(1, maxFiles))
            .ToList();
        if (files.Count == 0)
        {
            lines.Add("日志目录里还没有 game-*.log 文件。");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add("分析文件：" + string.Join("、", files.Select(f => f.Name)));
        lines.Add("");

        var findings = new Dictionary<string, Finding>(StringComparer.Ordinal);
        int warnings = 0, errors = 0, total = 0;

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
                    if (level == "warn") warnings++; else errors++;

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
                lines.Add($"读取 {file.Name} 失败：{ex.Message}");
            }
        }

        lines.Add($"共读取 {total} 行：warning {warnings} 条，error/reject {errors} 条。");
        lines.Add("");

        if (findings.Count == 0)
        {
            lines.Add("[完成] 没有发现 warning / error / reject 记录。");
            return string.Join(Environment.NewLine, lines);
        }

        var ordered = findings.Values.OrderByDescending(f => f.Count).ToList();
        var index = 0;
        foreach (var f in ordered.Take(20))
        {
            index++;
            lines.Add($"--- 问题 {index}（{f.Count} 次，{f.Level}）---");
            lines.Add("时间：" + (f.First == f.Last ? f.First : f.First + " ~ " + f.Last));
            lines.Add("内容：" + f.Sample);
            foreach (var hint in Explain(f.Sample)) lines.Add("可能原因：" + hint);
            lines.Add("");
        }

        if (ordered.Count > 20) lines.Add($"（同类问题较多，仅列出前 20 组，共 {ordered.Count} 组；完整内容见 logs 目录）");
        lines.Add("提示：把导出的诊断包发给别人或 AI 时，logs\\game-*.log 与 analysis.txt 就是分析依据。");
        return string.Join(Environment.NewLine, lines);
    }

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
