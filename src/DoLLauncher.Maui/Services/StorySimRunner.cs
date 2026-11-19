using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DoLLauncher.Maui.Services;

/// <summary>一次模拟的最终状态。</summary>
internal enum StorySimState
{
    Done,
    Error,
    Timeout,
    PostFailed,
}

/// <summary>模拟结束后的文案。</summary>
internal sealed record StorySimResult(StorySimState State, string Message);

/// <summary>
/// 向本地服务器的 /__launcher/api/story-sim/run 下发命令，并轮询 /status 直到结束。
/// 轮询节奏与超时时间与 WPF 端保持一致（2 秒一次，最多 15 分钟）。
/// </summary>
internal static class StorySimRunner
{
    private const int PollCount = 450;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static async Task<StorySimResult> RunAsync(
        string baseUrl, string payload, string label,
        Action<string> onStatus, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        using var post = new HttpRequestMessage(
            HttpMethod.Post, baseUrl + "/__launcher/api/story-sim/run")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(post, ct);
        if (!response.IsSuccessStatusCode)
            return new StorySimResult(StorySimState.PostFailed,
                "下发命令失败：HTTP " + (int)response.StatusCode);

        onStatus("等待游戏页面加载并模拟…");

        for (var i = 0; i < PollCount; i++)
        {
            await Task.Delay(PollInterval, ct);

            string text;
            try
            {
                text = await http.GetStringAsync(
                    baseUrl + "/__launcher/api/story-sim/status", ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 页面尚未加载完成或服务器短暂忙：下一轮再试。
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                // 状态上报中途被截断时忽略这一轮。
                continue;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("status", out var status) ||
                    status.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var state = ReadString(status, "state");
                var message = ReadString(status, "message");
                if (state is "queued" or "running")
                {
                    onStatus(string.IsNullOrWhiteSpace(message) ? "模拟中…" : message);
                    continue;
                }

                if (state == "done")
                {
                    var root = ReadString(status, "root");
                    var nodes = ReadInt(status, "nodes");
                    var summary = label + "完成：" + root + " · " +
                                  nodes.ToString(CultureInfo.InvariantCulture) + " 节点";
                    if (status.TryGetProperty("counts", out var counts) &&
                        counts.ValueKind == JsonValueKind.Object)
                    {
                        summary += "（" + ReadInt(counts, "success") + " 成功 / " +
                                   ReadInt(counts, "failed") + " 失败 / " +
                                   ReadInt(counts, "merged") + " 合并 / " +
                                   ReadInt(counts, "skipped") + " 跳过）";
                    }

                    return new StorySimResult(StorySimState.Done, summary);
                }

                if (state == "error")
                {
                    return new StorySimResult(StorySimState.Error,
                        "模拟失败：" + (string.IsNullOrWhiteSpace(message) ? "未知原因" : message));
                }
            }
        }

        return new StorySimResult(StorySimState.Timeout,
            "等待超时，请确认游戏页面已经打开。");
    }

    private static string ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return "";
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return "";
        return value.GetString() ?? "";
    }

    private static int ReadInt(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return 0;
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        return value.TryGetInt32(out var number) ? number : 0;
    }
}
