using Android.Content;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 分享 / 打开 zip 的统一导入入口：把 content:// 拷到缓存，再按现有的
/// PackService 流程分流成「版本包」或「模组包」；主界面的手动导入也复用这里。
/// </summary>
internal static class ShareImportHelper
{
    private static readonly object Sync = new();
    private static readonly List<PendingPack> Pending = new();
    private static bool _processing;

    private sealed record PendingPack(string Path, string Name);

    /// <summary>MainActivity 收到 ACTION_SEND / ACTION_VIEW 时调用；拷贝完成后自动处理。</summary>
    public static void AcceptIntent(Android.Content.Intent? intent)
    {
        if (intent == null) return;
        try
        {
            var uris = CollectUris(intent);
            if (uris.Count == 0) return;

            LogService.Append($"收到外部打开的 {uris.Count} 个文件，准备导入。");
            var context = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            _ = CopyAndEnqueueAsync(context, uris);
        }
        catch (Exception ex)
        {
            LogService.Error("处理外部打开的文件失败", ex);
        }
    }

    /// <summary>把排队中的分享文件执行导入；没有页面可用时保留队列，等主界面出现再处理。</summary>
    public static async Task ProcessPendingAsync(Page? page = null)
    {
        List<PendingPack> batch;
        lock (Sync)
        {
            if (_processing || Pending.Count == 0) return;
            _processing = true;
            batch = Pending.ToList();
            Pending.Clear();
        }

        var owner = page ?? CurrentPage;
        if (owner == null)
        {
            lock (Sync) Pending.InsertRange(0, batch);
            _processing = false;
            return;
        }

        try
        {
            await RunImportAsync(owner, batch);
        }
        catch (Exception ex)
        {
            LogService.Error("处理分享导入失败", ex);
        }
        finally
        {
            lock (Sync) _processing = false;
        }

        var hasMore = false;
        lock (Sync) hasMore = Pending.Count > 0;
        if (hasMore) _ = ProcessPendingAsync();
    }

    /// <summary>主界面「导入版本」：把选择器返回的文件交给同一套导入流程。</summary>
    public static async Task<int> ImportPickedAsync(Page page, IReadOnlyList<FileResult> files)
    {
        var packs = new List<PendingPack>();
        try
        {
            foreach (var file in files)
            {
                var name = string.IsNullOrWhiteSpace(file.FileName) ? "pack.zip" : file.FileName;
                var path = await CopyPickedAsync(file, name);
                packs.Add(new PendingPack(path, name));
            }
        }
        catch (Exception ex)
        {
            LogService.Error("读取待导入文件失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("读取文件失败：" + ex.Message, "导入");
            Cleanup(packs);
            return 0;
        }

        if (packs.Count == 0) return 0;
        return await RunImportAsync(page, packs);
    }

    private static async Task<int> RunImportAsync(Page owner, List<PendingPack> packs)
    {
        var imported = 0;
        var skipped = new List<string>();
        var failed = new List<string>();

        try
        {
            var names = string.Join("\n", packs.Select(p => "· " + p.Name));
            if (!await PlatformServices.Dialogs.ConfirmAsync(
                    $"将处理 {packs.Count} 个包：\n{names}\n\n版本包会新建版本；模组包会让你选择要导入的版本。\n\n继续吗？",
                    "导入版本 / 模组包", "开始导入"))
            {
                LogService.Append("用户取消导入。");
                return 0;
            }

            foreach (var pack in packs)
            {
                try
                {
                    var inspection = await Task.Run(() => PackService.Inspect(pack.Path));
                    if (inspection.IsUnknown)
                    {
                        failed.Add($"{pack.Name}：不是可识别的版本包或模组包（文件可能为空或已损坏）");
                        LogService.Warn($"导入跳过：{pack.Name} 无法识别为版本包或模组包。");
                        continue;
                    }

                    if (inspection.IsModPack)
                    {
                        var target = await PickTargetVersionAsync(owner, pack.Name);
                        if (target == null)
                        {
                            skipped.Add($"{pack.Name}：未选择目标版本");
                            continue;
                        }

                        var packPath = pack.Path;
                        var result = await Task.Run(() => PackService.Import(packPath, inspection, target.FullPath,
                            msg => LogService.Append("[分享导入] " + msg)));
                        LogService.Append($"[分享导入] {pack.Name} → {target.DisplayName}：成功 {result.ModCount} 个");
                        imported++;
                        if (result.FailedMods.Count > 0)
                            skipped.Add($"{pack.Name}：{result.FailedMods.Count} 个模组未通过校验");
                        continue;
                    }

                    var gamePack = pack.Path;
                    var info = await Task.Run(() => PackService.Import(gamePack, inspection, null,
                        msg => LogService.Append("[分享导入] " + msg)));
                    LogService.Append($"[分享导入] 完成：{info.NewVersionFolder}");
                    imported++;
                }
                catch (Exception ex)
                {
                    LogService.Error($"导入 {pack.Name} 失败", ex);
                    failed.Add($"{pack.Name}：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("分享导入流程失败", ex);
            failed.Add("导入流程异常：" + ex.Message);
        }
        finally
        {
            Cleanup(packs);
        }

        await PlatformServices.Dialogs.InfoAsync(BuildSummary(imported, skipped, failed), "导入结果");
        return imported;
    }

    /// <summary>模组包需要选择目标版本；由用户决定，取消则跳过这个包。</summary>
    private static async Task<VersionInfo?> PickTargetVersionAsync(Page owner, string displayName)
    {
        List<VersionInfo> versions;
        try
        {
            versions = await Task.Run(VersionService.Scan);
        }
        catch (Exception ex)
        {
            LogService.Error("扫描版本列表失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("扫描版本列表失败：" + ex.Message, "导入模组包");
            return null;
        }

        var candidates = versions
            .Where(v => !string.IsNullOrWhiteSpace(v.FullPath) && !v.IsFileMode)
            .ToList();
        if (candidates.Count == 0)
        {
            await PlatformServices.Dialogs.WarnAsync(
                "还没有可接收模组的版本，请先导入或下载游戏本体。", "导入模组包");
            return null;
        }

        var options = candidates.Select((v, i) => $"{i + 1}. {v.DisplayName}").ToArray();
        var choice = await owner.DisplayActionSheetAsync(
            $"「{displayName}」是模组包，导入到哪个版本？", "取消", null!, options);
        if (string.IsNullOrEmpty(choice)) return null;
        var index = Array.IndexOf(options, choice);
        return index < 0 ? null : candidates[index];
    }

    // ---------- content:// 拷贝 ----------

    private static async Task CopyAndEnqueueAsync(Android.Content.Context context,
        List<Android.Net.Uri> uris)
    {
        foreach (var uri in uris)
        {
            var pack = await CopyToCacheAsync(context, uri);
            if (pack == null) continue;
            lock (Sync) Pending.Add(pack);
        }

        MainThread.BeginInvokeOnMainThread(() => _ = ProcessPendingAsync());
    }

    private static async Task<PendingPack?> CopyToCacheAsync(Android.Content.Context context,
        Android.Net.Uri uri)
    {
        try
        {
            var resolver = context.ContentResolver
                           ?? throw new IOException("无法访问系统文件服务。");
            var name = QueryDisplayName(resolver, uri);
            var dir = Path.Combine(FileSystem.CacheDirectory, "import-shared");
            Directory.CreateDirectory(dir);
            var target = UniquePath(dir, SafeFileName(name));

            await Task.Run(() =>
            {
                using var input = resolver.OpenInputStream(uri)
                                  ?? throw new IOException("无法读取分享的文件。");
                using var output = File.Create(target);
                input.CopyTo(output);
            });

            LogService.Append($"[分享导入] 已复制到缓存：{target}");
            return new PendingPack(target, name);
        }
        catch (Exception ex)
        {
            LogService.Error("复制分享文件失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("读取分享文件失败：" + ex.Message, "导入");
            return null;
        }
    }

    private static async Task<string> CopyPickedAsync(FileResult file, string name)
    {
        var dir = Path.Combine(FileSystem.CacheDirectory, "import");
        Directory.CreateDirectory(dir);
        var target = UniquePath(dir, SafeFileName(name));

        await using (var source = await file.OpenReadAsync())
        await using (var dest = File.Create(target))
        {
            await source.CopyToAsync(dest);
        }
        return target;
    }

    private static List<Android.Net.Uri> CollectUris(Android.Content.Intent intent)
    {
        var uris = new List<Android.Net.Uri>();
        if (intent.Action == Android.Content.Intent.ActionSend)
        {
            var clip = intent.ClipData;
            if (clip != null)
            {
                for (var i = 0; i < clip.ItemCount; i++)
                {
                    var uri = clip.GetItemAt(i)?.Uri;
                    if (uri != null) uris.Add(uri);
                }
            }

            if (uris.Count == 0 && intent.HasExtra(Android.Content.Intent.ExtraStream))
            {
#pragma warning disable CS0618, CA1422
                if (intent.GetParcelableExtra(Android.Content.Intent.ExtraStream) is Android.Net.Uri extra)
                    uris.Add(extra);
#pragma warning restore CS0618, CA1422
            }
        }
        else if (intent.Data != null)
        {
            uris.Add(intent.Data);
        }
        return uris;
    }

    private static string QueryDisplayName(ContentResolver resolver, Android.Net.Uri uri)
    {
        try
        {
            var projection = new[] { Android.Provider.IOpenableColumns.DisplayName };
            using var cursor = resolver.Query(uri, projection, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(Android.Provider.IOpenableColumns.DisplayName);
                if (index >= 0)
                {
                    var name = cursor.GetString(index);
                    if (!string.IsNullOrWhiteSpace(name)) return name!;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("读取分享文件名失败", ex);
        }
        return uri.LastPathSegment ?? "shared.zip";
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? "shared.zip" : trimmed;
    }

    private static string UniquePath(string dir, string fileName)
    {
        var target = Path.Combine(dir, fileName);
        if (!File.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 999; i++)
        {
            target = Path.Combine(dir, $"{stem}-{i}{ext}");
            if (!File.Exists(target)) return target;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static void Cleanup(List<PendingPack> packs)
    {
        foreach (var pack in packs)
        {
            try
            {
                if (File.Exists(pack.Path)) File.Delete(pack.Path);
            }
            catch (Exception ex)
            {
                LogService.Warn($"删除导入缓存失败（{pack.Path}）：{ex.Message}");
            }
        }
    }

    private static string BuildSummary(int imported, List<string> skipped, List<string> failed)
    {
        var text = $"成功 {imported} 个，跳过 {skipped.Count} 个，失败 {failed.Count} 个。";
        if (skipped.Count > 0) text += "\n\n跳过：\n" + FormatList(skipped);
        if (failed.Count > 0) text += "\n\n失败：\n" + FormatList(failed);
        return text;
    }

    private static string FormatList(List<string> items)
    {
        const int max = 6;
        var text = string.Join("\n", items.Take(max).Select(s => "· " + s));
        return items.Count > max ? text + $"\n…另有 {items.Count - max} 个" : text;
    }

    private static Page? CurrentPage
    {
        get
        {
            var root = Application.Current?.Windows.FirstOrDefault()?.Page;
            return root is NavigationPage nav
                ? nav.Navigation.NavigationStack.LastOrDefault() ?? nav.CurrentPage
                : root;
        }
    }
}
