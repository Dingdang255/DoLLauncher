namespace DoLLauncher.Core.Import;

/// <summary>
/// 判断一个构建（资产名或版本目录名）是否带 ModLoader。
/// 依据实测：汉化仓库 <c>v0.4.3.3-chs-alpha2.2.0</c> 之前是 <c>dol-*</c> 旧产物；
/// Lyra 在 <c>v0.4.3.3-alpha2.2.0-1118</c> 之前（小写 <c>dol-</c> 开头）也不含 ModLoader。
/// 这些版本无法安装或加载外部模组。
/// </summary>
public static class VersionAge
{
    public static bool SupportsModLoader(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;

        var stem = name.Trim();
        if (stem.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith(".dolpack", StringComparison.OrdinalIgnoreCase))
            stem = Path.GetFileNameWithoutExtension(stem);

        if (stem.StartsWith("polyfill-", StringComparison.OrdinalIgnoreCase))
            stem = stem["polyfill-".Length..];

        if (stem.StartsWith("DoL-ModLoader", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.Contains("-Lyra-", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.StartsWith("Neo-", StringComparison.OrdinalIgnoreCase)) return true;

        // 大写 DoL- 的 chsmods 构建（0.4.3.3-alpha2.2.0 起）已带 ModLoader；
        // 小写 dol- 的早期构建没有。
        return stem.StartsWith("DoL-", StringComparison.Ordinal);
    }

    /// <summary>
    /// 版本目录名是否带 ModLoader。安装器沿用资产名建目录，所以只看小写 <c>dol-</c> 前缀；
    /// 手工添加的、命名无法识别的目录按「支持」处理，不误报版本过旧。
    /// </summary>
    public static bool FolderSupportsModLoader(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return true;
        var name = Path.GetFileName(folder.Trim());
        return !name.StartsWith("dol-", StringComparison.Ordinal);
    }
}
