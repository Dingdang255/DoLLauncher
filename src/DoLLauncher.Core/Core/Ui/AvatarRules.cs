using System.IO;

namespace DoLLauncher.Core.Ui;

/// <summary>
/// 玩家头像的校验规则：只允许常见位图格式，并限制文件体积。
/// 纯规则，不依赖 WPF，便于后续跨平台复用。
/// </summary>
public static class AvatarRules
{
    public static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>头像文件体积上限（8 MB）。</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>校验文件是否可作为头像；失败时返回中文原因。</summary>
    public static (bool Ok, string Reason) Validate(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.Length == 0) return (false, "还没有选择头像文件。");
        if (!File.Exists(text)) return (false, "头像文件不存在。");

        var ext = Path.GetExtension(text).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            return (false, $"不支持的图片格式：{ext}。请使用 PNG / JPG / BMP / GIF。");
        }

        try
        {
            var length = new FileInfo(text).Length;
            if (length <= 0) return (false, "头像文件内容为空。");
            if (length > MaxBytes)
            {
                return (false, $"图片体积 {length / 1024.0 / 1024.0:F1} MB，超过 {MaxBytes / 1024 / 1024} MB 上限。");
            }
        }
        catch (Exception ex)
        {
            return (false, "无法读取头像文件：" + ex.Message);
        }

        return (true, string.Empty);
    }
}

/// <summary>
/// 需求 16 预留：从指定存档中自动提取角色状态并生成头像。
/// 目前存档里没有稳定的角色立绘 / 头像字段，因此暂不提供实现，
/// 后续接入剧情数据或 Mod 补充信息后，只需实现本接口并在设置页启用。
/// </summary>
public interface IAvatarSource
{
    /// <summary>实现名称，用于设置页说明。</summary>
    string Name { get; }

    /// <summary>当前是否具备生成条件。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用时的中文原因。</summary>
    string UnavailableReason { get; }

    /// <summary>尝试生成头像并写入 <paramref name="outputPath"/>；成功返回该路径。</summary>
    Task<string?> TryCreateAsync(string versionFolder, string outputPath, CancellationToken cancellationToken = default);
}
