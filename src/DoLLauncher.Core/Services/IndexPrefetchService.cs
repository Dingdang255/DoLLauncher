using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>
/// 下载中心索引的内存缓存：启动预取与页面加载共用同一个 Task，
/// 避免「启动时同时预取」只发请求、进页面又重新拉一遍。
/// </summary>
public static class IndexPrefetchService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Task<List<GameRelease>>> GameTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, Task<(List<GameRelease> Releases, int LastPage)>> PackTasks = new();
    private static Task<MarketCatalog>? _marketTask;
    private static string? _marketSourceKey;
    private static bool _started;

    /// <summary>游戏本体全部 Release；同一来源复用同一个请求。</summary>
    public static Task<List<GameRelease>> GetGameReleasesAsync(GameSource source, bool forceRefresh = false)
    {
        lock (Sync)
        {
            if (forceRefresh) GameTasks.Remove(source.Id);
            if (!GameTasks.TryGetValue(source.Id, out var task))
            {
                task = DownloadService.FetchAllGameReleasesAsync(source);
                GameTasks[source.Id] = task;
            }
            return task;
        }
    }

    /// <summary>模组市场索引；forceRefresh 时重新拉取。</summary>
    public static Task<MarketCatalog> GetMarketAsync(bool forceRefresh = false, Action<string>? log = null)
    {
        // 切换主源 / 备用源后不能再复用旧索引。
        var sourceKey = AppConfig.Load().General.MarketSource;
        lock (Sync)
        {
            if (forceRefresh || !string.Equals(sourceKey, _marketSourceKey, StringComparison.OrdinalIgnoreCase))
            {
                _marketTask = null;
                _marketSourceKey = sourceKey;
            }
            _marketTask ??= DownloadService.FetchMarketAsync(log);
            return _marketTask;
        }
    }

    /// <summary>Lyra 整合包某一页；按页码缓存。</summary>
    public static Task<(List<GameRelease> Releases, int LastPage)> GetPackPageAsync(
        int page, bool forceRefresh = false)
    {
        page = Math.Max(1, page);
        lock (Sync)
        {
            if (forceRefresh) PackTasks.Remove(page);
            if (!PackTasks.TryGetValue(page, out var task))
            {
                task = DownloadService.FetchLyraReleasesPageAsync(page);
                PackTasks[page] = task;
            }
            return task;
        }
    }

    /// <summary>
    /// 按「启动时预取索引」设置预热三份索引。不阻塞 UI；失败只写日志，
    /// 页面真正加载时仍会显示对应错误并允许重试。
    /// </summary>
    public static async Task StartAsync()
    {
        lock (Sync)
        {
            if (_started) return;
            _started = true;
        }

        var config = AppConfig.Load();
        var mode = config.General.Prefetch;
        if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Append("[预取] 已关闭启动预取");
            return;
        }

        var source = DownloadService.CurrentGameSource();
        LogService.Append($"[预取] 启动时并行预取索引：游戏本体（{source.Name}）/ 模组市场 / 整合包");

        var game = GetGameReleasesAsync(source);
        var market = GetMarketAsync(log: msg => LogService.Append("[预取] " + msg));
        var pack = GetPackPageAsync(1);

        try
        {
            await Task.WhenAll(new Task[] { game, market, pack });
            LogService.Append(
                $"[预取] 完成：游戏 {game.Result.Count} 个版本，模组 {market.Result.Mods.Count} 个，" +
                $"整合包第 1 页 {pack.Result.Releases.Count} 个");
        }
        catch (Exception ex)
        {
            LogService.Error("启动预取索引失败", ex);
        }
    }
}
