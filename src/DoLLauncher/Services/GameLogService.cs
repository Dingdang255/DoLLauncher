using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DoLLauncher.Services;

/// <summary>本次会话的日志上报状态快照（只读，供诊断页与状态接口展示）。</summary>
public sealed record GameLogStatus(
    string SessionId,
    string File,
    long Lines,
    long Bytes,
    DateTime? LastWriteAt,
    int Failures,
    DateTime? LastFailureAt,
    string? LastFailure,
    bool Stopped,
    string? StopReason);

/// <summary>
/// 游戏页面的控制台日志落盘。由内置服务器在游戏 HTML 里注入的脚本把 console / 报错
/// POST 回来，每次启动写一份 logs\\game-yyyyMMdd-HHmmss.log；
/// 单文件达到上限后按设置新建 -2、-3… 或停止写入；文件数超过保留份数自动删最旧。
/// 同时在内存里记录本次会话的写入/上报状态，供诊断页与状态接口查询。
/// </summary>
public static class GameLogService
{
    private sealed class SessionState
    {
        public string VersionKey = string.Empty;
        public string SessionId = string.Empty;
        public string FileName = string.Empty;
        public long Lines;
        public long Bytes;
        public DateTime? LastWriteAt;
        public int Failures;
        public DateTime? LastFailureAt;
        public string? LastFailure;
        public bool Stopped;
        public string? StopReason;
        public DateTime UpdatedAt = DateTime.Now;
    }

    private const int MaxStatusSessions = 64;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, SessionState> Status = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex PartPattern = new(@"^game-(\d{8}-\d{6})(?:-(\d+))?\.log$", RegexOptions.IgnoreCase);

    public static string Dir(string versionDir) => Path.Combine(versionDir, "logs");

    public static string FileFor(string versionDir, string sessionId) =>
        Path.Combine(Dir(versionDir), $"game-{NormalizeSession(sessionId)}.log");

    /// <summary>把一段日志追加到本次会话的文件；失败不抛异常，避免影响游戏页面。</summary>
    public static void Append(string versionDir, string sessionId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var sid = NormalizeSession(sessionId);
        try
        {
            var dir = Dir(versionDir);
            Directory.CreateDirectory(dir);
            var (maxMb, overflow) = Settings();
            var maxBytes = maxMb <= 0 ? long.MaxValue : maxMb * 1024L * 1024L;

            lock (Sync)
            {
                var file = CurrentFile(dir, sid);
                var info = new FileInfo(file);

                if (info.Exists && info.Length >= maxBytes)
                {
                    if (overflow == "stop")
                    {
                        if (Warned.Add(file))
                        {
                            File.AppendAllText(file, "=== 单文件已达上限，本次会话后续日志不再写入 ===\n", Utf8);
                        }
                        MarkStopped(versionDir, sid, file, "单文件已达上限，按设置停止写入");
                        return;
                    }
                    file = NextPartFile(dir, sid);
                    info = new FileInfo(file);
                }

                if (!info.Exists) Prune(dir, Keep());
                File.AppendAllText(file, text, Utf8);
                MarkWritten(versionDir, sid, file, text);
            }
        }
        catch (Exception ex)
        {
            MarkFailed(versionDir, sid, ex);
        }
    }

    /// <summary>浏览器端连续上报失败后主动停止采集：记录说明，供诊断页与日志排查。</summary>
    public static void NoteReportStop(string versionDir, string sessionId, string reason, int failures)
    {
        try
        {
            var sid = NormalizeSession(sessionId);
            var text = string.IsNullOrWhiteSpace(reason) ? "浏览器端日志上报连续失败" : Trim(reason, 200);
            lock (Sync)
            {
                var state = GetOrCreate(versionDir, sid);
                state.Stopped = true;
                state.StopReason = text;
                state.LastFailure = text;
                state.LastFailureAt = DateTime.Now;
                state.Failures = Math.Max(state.Failures, Math.Max(1, failures));
                state.UpdatedAt = DateTime.Now;
            }
            LogService.Append($"游戏日志上报已停止（sid={sid}）：{text}");
        }
        catch
        {
            // 状态记录失败不影响采集链路
        }
    }

    private static readonly Dictionary<string, DateTime> FatalNotifiedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>页面上报 Fatal 级日志时触发（参数：版本名、内容）；在线程池线程上触发。</summary>
    public static event Action<string, string>? FatalDetected;

    /// <summary>记录页面主动上报的致命错误，并按 30 秒节流通知界面弹出独立提示窗。</summary>
    public static void NoteFatal(string versionDir, string versionName, string message)
    {
        try
        {
            var text = (message ?? string.Empty).Trim();
            if (text.Length == 0) return;
            if (text.Length > 800) text = text[..800];

            var key = VersionKey(versionDir);
            var notify = false;
            lock (Sync)
            {
                if (!FatalNotifiedAt.TryGetValue(key, out var last) ||
                    DateTime.UtcNow - last >= TimeSpan.FromSeconds(30))
                {
                    FatalNotifiedAt[key] = DateTime.UtcNow;
                    notify = true;
                }
            }

            LogService.Append($"游戏出现致命错误（{versionName}）：{text}");
            if (!notify) return;

            try { FatalDetected?.Invoke(versionName, text); }
            catch
            {
                // 通知失败不影响页面
            }
        }
        catch
        {
            // 状态记录失败不影响采集链路
        }
    }

    /// <summary>读取本次会话的上报状态；不指定 sessionId 时返回该版本最近一次会话。</summary>
    public static GameLogStatus? GetStatus(string versionDir, string? sessionId = null)
    {
        try
        {
            var vkey = VersionKey(versionDir);
            var sid = CleanSession(sessionId);
            lock (Sync)
            {
                SessionState? latest = null;
                foreach (var state in Status.Values)
                {
                    if (!string.Equals(state.VersionKey, vkey, StringComparison.Ordinal)) continue;
                    if (sid != null)
                    {
                        if (string.Equals(state.SessionId, sid, StringComparison.Ordinal)) return Snapshot(state);
                        continue;
                    }
                    if (latest == null || state.UpdatedAt > latest.UpdatedAt) latest = state;
                }
                return latest == null ? null : Snapshot(latest);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>诊断页展示用的一句话状态。</summary>
    public static string StatusSummary(string versionDir, string? sessionId = null)
    {
        try
        {
            var status = GetStatus(versionDir, sessionId);
            if (status == null) return "最近一次日志：本次运行还没有收到游戏页面上报（启动游戏后自动采集）。";

            var text = $"最近一次日志：文件 {status.File}，写入 {status.Lines} 行";
            if (status.Stopped)
            {
                var reason = string.IsNullOrWhiteSpace(status.StopReason) ? "连续上报失败" : status.StopReason;
                return $"{text}；已停止写入：{reason}";
            }
            if (status.Failures > 0)
            {
                var reason = string.IsNullOrWhiteSpace(status.LastFailure) ? "未知原因" : status.LastFailure;
                return $"{text}；连续失败 {status.Failures} 次：{reason}";
            }
            return $"{text}；上报正常";
        }
        catch (Exception ex)
        {
            return "日志上报状态读取失败：" + ex.Message;
        }
    }

    /// <summary>本次会话当前应该写入的文件：已有分份则取最后一份。</summary>
    private static string CurrentFile(string dir, string sid)
    {
        var best = 0;
        try
        {
            foreach (var path in Directory.GetFiles(dir, "game-*.log"))
            {
                var m = PartPattern.Match(Path.GetFileName(path));
                if (!m.Success || m.Groups[1].Value != sid) continue;
                var part = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                if (part > best) best = part;
            }
        }
        catch
        {
            // 目录读取失败时退回基础文件名
        }
        return best <= 1
            ? Path.Combine(dir, $"game-{sid}.log")
            : Path.Combine(dir, $"game-{sid}-{best}.log");
    }

    private static string NextPartFile(string dir, string sid)
    {
        var part = 2;
        var m = PartPattern.Match(Path.GetFileName(CurrentFile(dir, sid)));
        if (m.Success && m.Groups[2].Success) part = int.Parse(m.Groups[2].Value) + 1;
        return Path.Combine(dir, $"game-{sid}-{part}.log");
    }

    private static (int MaxMb, string Overflow) Settings()
    {
        try
        {
            var g = AppConfig.Load().General;
            var overflow = string.Equals(g.GameLogOverflow, "stop", StringComparison.OrdinalIgnoreCase) ? "stop" : "newfile";
            return (Math.Max(0, g.GameLogMaxMb), overflow);
        }
        catch
        {
            return (30, "newfile");
        }
    }

    private static int Keep()
    {
        try { return Math.Max(1, AppConfig.Load().General.GameLogKeep); }
        catch { return 20; }
    }

    /// <summary>只保留最新的 keep-1 份（调用方随后会新建一份，总数正好等于 keep）。</summary>
    private static void Prune(string dir, int keep)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("game-*.log")
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
            for (var i = keep - 1; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch
        {
            // 清理失败不影响写入
        }
    }

    private static string NormalizeSession(string sessionId) =>
        CleanSession(sessionId) ?? DateTime.Now.ToString("yyyyMMdd-HHmmss");

    private static string? CleanSession(string? sessionId)
    {
        var cleaned = new string((sessionId ?? string.Empty).Where(c => char.IsDigit(c) || c == '-').ToArray());
        if (cleaned.Length < 8 || cleaned.Length > 20) return null;
        return cleaned;
    }

    // ---------- 会话状态（内存，不落盘） ----------

    private static string VersionKey(string versionDir)
    {
        try
        {
            return Path.GetFullPath(versionDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
        }
        catch
        {
            return (versionDir ?? string.Empty).ToLowerInvariant();
        }
    }

    private static SessionState GetOrCreate(string versionDir, string sid)
    {
        var key = VersionKey(versionDir) + "|" + sid;
        if (Status.TryGetValue(key, out var state)) return state;

        if (Status.Count >= MaxStatusSessions) TrimStatus();
        state = new SessionState { VersionKey = VersionKey(versionDir), SessionId = sid };
        Status[key] = state;
        return state;
    }

    private static void TrimStatus()
    {
        try
        {
            var stale = Status
                .OrderBy(p => p.Value.UpdatedAt)
                .Take(Math.Max(1, Status.Count / 2))
                .Select(p => p.Key)
                .ToList();
            foreach (var key in stale) Status.Remove(key);
        }
        catch
        {
            // 裁剪失败时继续保留新记录
        }
    }

    /// <summary>成功写入一段文本；调用方已持有 Sync 锁。</summary>
    private static void MarkWritten(string versionDir, string sid, string file, string text)
    {
        try
        {
            var state = GetOrCreate(versionDir, sid);
            state.FileName = Path.GetFileName(file);
            state.Lines += CountLines(text);
            state.Bytes += Utf8.GetByteCount(text);
            state.LastWriteAt = DateTime.Now;
            state.UpdatedAt = DateTime.Now;
            if (!state.Stopped) state.Failures = 0;
        }
        catch
        {
            // 状态统计失败不影响落盘
        }
    }

    private static void MarkStopped(string versionDir, string sid, string file, string reason)
    {
        try
        {
            var state = GetOrCreate(versionDir, sid);
            state.FileName = Path.GetFileName(file);
            state.Stopped = true;
            state.StopReason = reason;
            state.UpdatedAt = DateTime.Now;
        }
        catch
        {
            // 忽略
        }
    }

    private static void MarkFailed(string versionDir, string sid, Exception ex)
    {
        try
        {
            var count = 0;
            lock (Sync)
            {
                var state = GetOrCreate(versionDir, sid);
                state.Failures++;
                state.LastFailureAt = DateTime.Now;
                state.LastFailure = ex.Message;
                state.UpdatedAt = DateTime.Now;
                count = state.Failures;
            }
            // 只记录关键路径，避免高频日志写爆 launcher.log
            if (count == 1 || count % 50 == 0)
            {
                LogService.Append($"游戏日志写入失败（{versionDir}，第 {count} 次）：{ex.Message}");
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static GameLogStatus Snapshot(SessionState state) => new(
        state.SessionId,
        state.FileName,
        state.Lines,
        state.Bytes,
        state.LastWriteAt,
        state.Failures,
        state.LastFailureAt,
        state.LastFailure,
        state.Stopped,
        state.StopReason);

    private static long CountLines(string text)
    {
        if (text.Length == 0) return 0;
        long lines = 0;
        foreach (var c in text)
        {
            if (c == '\n') lines++;
        }
        if (text[^1] != '\n') lines++;
        return lines;
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
