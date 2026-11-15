using System.Collections.Concurrent;

namespace DoLLauncher.Services;

/// <summary>正在执行剧情分支模拟的版本，用于版本列表显示「模拟中」。</summary>
public static class SimulationTracker
{
    private static readonly ConcurrentDictionary<string, byte> Folders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>标记该版本正在模拟。</summary>
    public static void Set(string folder)
    {
        if (folder.Length > 0) Folders[folder] = 0;
    }

    /// <summary>取消模拟标记。</summary>
    public static void Clear(string folder)
    {
        if (folder.Length > 0) Folders.TryRemove(folder, out _);
    }

    /// <summary>该版本是否正在模拟。</summary>
    public static bool IsSimulating(string folder) =>
        folder.Length > 0 && Folders.ContainsKey(folder);

    /// <summary>记录这个版本的游戏页是「模拟」拉起来的。</summary>
    public static void MarkPage(string folder)
    {
        if (folder.Length > 0) Pages[folder] = 0;
    }

    /// <summary>该版本是否有模拟页。</summary>
    public static bool HasPage(string folder) =>
        folder.Length > 0 && Pages.ContainsKey(folder);

    /// <summary>清除模拟页标记（停止模拟时）。</summary>
    public static void UnmarkPage(string folder)
    {
        if (folder.Length > 0) Pages.TryRemove(folder, out _);
    }
}
