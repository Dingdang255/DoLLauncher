using System.IO;
using DoLLauncher.Core.Browser;
using DoLLauncher.Core.Data;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>
/// 删除版本后的浏览器数据清理（需求 2）：关闭页面后不打开任何浏览器窗口，
/// 尽力删除该端口 origin 在独立 Profile 下的 IndexedDB 目录；同时写入「端口待清理」标记，
/// 由下次同端口打开游戏页的桥接脚本清空 localStorage / IndexedDB / 缓存。
/// </summary>
public static class BrowserDataCleaner
{
    public static void CleanVersion(VersionInfo version, Action<string> log)
    {
        var port = version.Port;
        var removed = 0;
        var failed = 0;
        try
        {
            var profileDir = BrowserService.ResolveProfileDir(AppConfig.Load().General);
            var origin = $"http_127.0.0.1_{port}";
            var defaultDir = Path.Combine(profileDir, "Default");
            if (Directory.Exists(defaultDir))
            {
                removed += DeleteOriginDirs(Path.Combine(defaultDir, "IndexedDB"), origin, ref failed);
                removed += DeleteOriginDirs(Path.Combine(defaultDir, "Session Storage"), origin, ref failed);
                removed += DeleteOriginDirs(Path.Combine(defaultDir, "File System"), origin, ref failed);
            }
        }
        catch (Exception ex)
        {
            failed++;
            log($"清理浏览器数据失败：{ex.Message}");
        }

        // 无论文件是否删掉，都留标记：下次同端口打开页面时由页面脚本彻底清空
        IdbMirrorStore.WriteCleanMarker(port, "删除版本 " + version.Folder);
        IdbMirrorStore.DeleteVersionData(version.FullPath);

        if (failed == 0)
        {
            log(removed > 0
                ? $"已清理端口 {port} 的浏览器数据（{removed} 项）。"
                : $"端口 {port} 没有需要清理的浏览器数据，已记录待清理标记。");
        }
        else
        {
            log($"端口 {port} 有 {failed} 项浏览器数据未能立即删除（可能被占用），下次打开同端口时会自动清空。");
        }
    }

    private static int DeleteOriginDirs(string parent, string origin, ref int failed)
    {
        if (!Directory.Exists(parent)) return 0;
        var removed = 0;
        try
        {
            foreach (var dir in Directory.GetDirectories(parent))
            {
                if (!Path.GetFileName(dir).Contains(origin, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch
                {
                    failed++;
                }
            }
        }
        catch
        {
            failed++;
        }
        return removed;
    }
}
