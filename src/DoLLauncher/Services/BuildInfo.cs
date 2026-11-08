using System.Reflection;

namespace DoLLauncher.Services;

/// <summary>
/// 构建时注入的版本信息：tag、提交短哈希与构建时间，格式为 <c>tag|commit|时间</c>。
/// 由 build.ps1 / CI 通过 <c>-p:InformationalVersion</c> 写入；未注入时显示开发版。
/// </summary>
public static class BuildInfo
{
    private static readonly string Raw = ReadRaw();

    public static string Tag { get; } = Part(0, "dev");
    public static string Commit { get; } = Part(1, "local");
    public static string BuildTime { get; } = Part(2, "未知");

    /// <summary>形如「v1.2.0 · a1b2c3d · 2026-11-08 10:38」。</summary>
    public static string Display => $"{Tag} · {Commit} · {BuildTime}";

    private static string ReadRaw()
    {
        var attr = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attr?.InformationalVersion ?? "";
    }

    private static string Part(int index, string fallback)
    {
        var parts = Raw.Split('|');
        if (parts.Length < 3) return fallback;
        var value = parts[index].Trim();
        return value.Length == 0 ? fallback : value;
    }
}
