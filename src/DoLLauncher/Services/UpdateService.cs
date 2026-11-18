using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DoLLauncher.Services;

public sealed record LauncherAsset(string Name, string DownloadUrl, long Size);

/// <summary>GitHub 最新 Release 信息：tag、发布页地址、更新日志与发布包列表。</summary>
public sealed record LauncherRelease(string Tag, string Url, string Body, IReadOnlyList<LauncherAsset> Assets)
{
    /// <summary>启动器发布包（zip）：优先名字里带 Launcher 的资产。</summary>
    public LauncherAsset? FindPackage() =>
        Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("launcher", StringComparison.OrdinalIgnoreCase))
        ?? Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
}

/// <summary>启动器自身的更新检查与安装：读取 GitHub 最新 Release，与当前 tag 比较。</summary>
public static class UpdateService
{
    private const string Repo = "Dingdang255/DoLLauncher";

    public static async Task<LauncherRelease> CheckAsync()
    {
        var text = await DownloadService.GetGithubApiAsync($"repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var assets = new List<LauncherAsset>();
        if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetArray.EnumerateArray())
            {
                var name = PropString(asset, "name");
                var url = PropString(asset, "browser_download_url");
                if (name.Length == 0 || url.Length == 0) continue;
                assets.Add(new LauncherAsset(name, url, PropLong(asset, "size")));
            }
        }

        return new LauncherRelease(
            PropString(root, "tag_name").Trim(),
            PropString(root, "html_url").Trim(),
            PropString(root, "body"),
            assets);
    }

    private static string PropString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long PropLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    /// <summary>
    /// 远端 tag 是否比当前版本新。允许 v 前缀，按数字段比较；
    /// 任一方不是数字版本时，只要字符串不同就按「有更新」处理。
    /// </summary>
    public static bool IsNewer(string remoteTag, string currentTag)
    {
        var remote = ParseVersion(remoteTag);
        var current = ParseVersion(currentTag);
        if (remote == null || current == null) return remoteTag != currentTag;

        var count = Math.Max(remote.Count, current.Count);
        for (var i = 0; i < count; i++)
        {
            var r = i < remote.Count ? remote[i] : 0;
            var c = i < current.Count ? current[i] : 0;
            if (r != c) return r > c;
        }
        return false;
    }

    private static List<int>? ParseVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var parts = text.Split('.');
        var numbers = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            // 只接受纯数字段；带后缀（如 1.0.0-beta）时退回字符串比较
            if (!int.TryParse(part, out var value)) return null;
            numbers.Add(value);
        }
        return numbers.Count == 0 ? null : numbers;
    }

    public static string DownloadTempDir => Path.Combine(Path.GetTempPath(), "DoLLauncher-update");

    /// <summary>把发布包下载到临时目录，返回本地 zip 路径。</summary>
    public static async Task<string> DownloadPackageAsync(LauncherAsset asset, Action<double> progress)
    {
        Directory.CreateDirectory(DownloadTempDir);
        var path = Path.Combine(DownloadTempDir, asset.Name);
        if (File.Exists(path)) File.Delete(path);
        await DownloadService.DownloadLauncherPackageAsync(asset.DownloadUrl, path, progress);
        return path;
    }

    /// <summary>
    /// 解压下载好的发布包，并启动一个独立脚本：等当前进程退出后替换 exe 并重新拉起。
    /// 调用成功后应立即退出启动器。
    /// </summary>
    public static void ScheduleApply(string zipPath)
    {
        var temp = DownloadTempDir;
        var extract = Path.Combine(temp, "extract");
        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        ZipFile.ExtractToDirectory(zipPath, extract, true);

        var newExe = Directory
            .EnumerateFiles(extract, "DoLLauncher.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (newExe == null) throw new InvalidOperationException("更新包里没有找到 DoLLauncher.exe。");

        var targetExe = CurrentExePath();
        var targetDir = Path.GetDirectoryName(targetExe);
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            throw new InvalidOperationException("找不到启动器安装目录。");

        EnsureWritable(targetDir);

        var stage = Path.Combine(temp, "DoLLauncher.new.exe");
        File.Copy(newExe, stage, true);

        var script = Path.Combine(temp, "apply-update.ps1");
        // Windows PowerShell 5.1 读取无 BOM 的 UTF-8 会乱码，路径里可能有中文，必须带 BOM
        File.WriteAllText(script, BuildApplyScript(stage, targetExe), new UTF8Encoding(true));

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);
    }

    private static string CurrentExePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) &&
            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path))
        {
            return path;
        }
        return Path.Combine(AppContext.BaseDirectory, "DoLLauncher.exe");
    }

    /// <summary>启动器目录不可写时（例如装在 Program Files）提前失败，避免退出后才替换失败。</summary>
    private static void EnsureWritable(string dir)
    {
        var probe = Path.Combine(dir, ".dol-update-probe");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("启动器目录不可写，无法自动替换文件。", ex);
        }
    }

    private static string BuildApplyScript(string source, string target)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var pid = Environment.ProcessId;
        return $$"""
$ErrorActionPreference = 'SilentlyContinue'
try { Wait-Process -Id {{pid}} -Timeout 60 } catch { }
Start-Sleep -Milliseconds 500
for ($i = 0; $i -lt 40; $i++) {
    try {
        Copy-Item -LiteralPath {{Quote(source)}} -Destination {{Quote(target)}} -Force -ErrorAction Stop
        break
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
Start-Process -FilePath {{Quote(target)}}
""";
    }
}
