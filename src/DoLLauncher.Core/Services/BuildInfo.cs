using System.Reflection;

namespace DoLLauncher.Services;

/// <summary>
/// 构建时注入的版本信息：tag、提交短哈希与构建时间，格式为 <c>tag|commit|时间</c>。
/// 由 build.ps1 / CI 通过 <c>-p:InformationalVersion</c> 写入；未注入时显示开发版。
/// </summary>
public static class BuildInfo
{
    private static string? _override;
    private static string? _raw;

    private static string Raw => _raw ??= ReadRaw();

    public static string Tag => Part(0, "dev");
    public static string Commit => Part(1, "local");
    public static string BuildTime => Part(2, "未知");

    /// <summary>开发版显示「开发版构建 · 构建时间」；正式版本显示 tag / 提交 / 构建时间。</summary>
    public static string Display => Tag == "dev"
        ? $"开发版构建 · {BuildTime}"
        : $"{Tag} · {Commit} · {BuildTime}";

    /// <summary>
    /// Android 端没有传统意义上的入口程序集，由 MauiProgram 在启动时把本程序的
    /// AssemblyInformationalVersion（构建注入的 tag|commit|时间）交给这里。
    /// </summary>
    public static void Override(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw!.Split('|').Length < 3) return;
        _override = raw;
        _raw = raw;
    }

    private static string ReadRaw()
    {
        if (!string.IsNullOrEmpty(_override)) return _override!;

        // 桌面端入口程序集是 DoLLauncher，Android 端是 DoLLauncher.Maui：
        // 构建脚本把 tag|commit|时间注入入口程序集，Core 自身可能没有，逐个尝试。
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(BuildInfo).Assembly })
        {
            var attr = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var value = attr?.InformationalVersion ?? string.Empty;
            if (value.Split('|').Length >= 3) return value;
        }
        return string.Empty;
    }

    private static string Part(int index, string fallback)
    {
        var parts = Raw.Split('|');
        if (parts.Length < 3) return fallback;
        // 部分构建环境会给 InformationalVersion 追加「+提交哈希」，这里一并去掉
        var value = parts[index].Split('+')[0].Trim();
        return value.Length == 0 ? fallback : value;
    }
}
