using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>
/// 「可能冲突」检测：市场简介与 README 里常有「与某某某冲突」「不兼容」之类的声明，
/// 索引没有单独的冲突字段，只能从文本里识别。只对已启用的本地模组生效，只警告不阻止。
/// </summary>
public static class ModConflictService
{
    private const string SourceIndex = "index";
    private const string SourceReadme = "readme";

    private static readonly string[] ConflictKeywords =
    {
        "冲突", "不兼容", "不能同时", "不可同时", "无法同时",
        "incompatible", "conflict", "cannot be used together", "not compatible",
    };

    private static readonly string[] GenericNames =
        { "汉化", "汉化mod", "modloader", "modhub", "游戏本体", "readme", "dol", "dolmods" };

    /// <summary>扫描目标版本里所有已启用模组，返回去重后的冲突对（已启用 + 双方都在本地）。</summary>
    public static async Task<List<ModConflict>> CheckAsync(IEnumerable<ModInfo> mods, MarketCatalog catalog,
        Action<string>? log = null)
    {
        var enabled = mods.Where(m => m.Enabled).ToList();
        var result = new List<ModConflict>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var local in enabled)
        {
            var market = MatchMarketMod(local, catalog);
            var source = market ?? new MarketMod { Name = local.Name };

            // boot.json 里的 description 常常和市场简介一致，重复的冲突对由 seen 去重
            AddFromText(local, source, local.Description, SourceIndex, enabled, catalog, seen, result);

            if (market == null) continue;
            AddFromText(local, market, market.Description, SourceIndex, enabled, catalog, seen, result);

            if (market.Repo.Length == 0) continue;
            try
            {
                var readme = await DownloadService.FetchRepoReadmeAsync(market.Repo);
                if (readme.Length == 0) continue;
                var section = DownloadService.ReadmeSectionFor(readme, market);
                AddFromText(local, market, section, SourceReadme, enabled, catalog, seen, result);
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 README 冲突声明失败：" + ex.Message);
            }
        }

        return result;
    }

    /// <summary>安装前：新模组（还没写盘）与本地已启用模组之间的声明式冲突。</summary>
    public static async Task<List<ModConflict>> CheckAgainstAsync(MarketMod incoming, IEnumerable<ModInfo> installed,
        MarketCatalog catalog, Action<string>? log = null)
    {
        var enabled = installed.Where(m => m.Enabled).ToList();
        var result = new List<ModConflict>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var local = new ModInfo
        {
            Name = incoming.Name,
            FileName = incoming.VerifiedAssetFileName,
            Version = incoming.Version,
            Enabled = true,
        };

        AddFromText(local, incoming, incoming.Description, SourceIndex, enabled, catalog, seen, result);

        if (incoming.Repo.Length > 0)
        {
            try
            {
                var readme = await DownloadService.FetchRepoReadmeAsync(incoming.Repo);
                if (readme.Length > 0)
                {
                    var section = DownloadService.ReadmeSectionFor(readme, incoming);
                    AddFromText(local, incoming, section, SourceReadme, enabled, catalog, seen, result);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 README 冲突声明失败：" + ex.Message);
            }
        }

        // 反向：已装模组的简介 / README 里也可能写着「与即将安装的某某冲突」，
        // 只扫新模组自己的文字会漏掉这种情况（例如已装的秋枫白桦框架声明与简易框架冲突）。
        foreach (var installedMod in enabled)
        {
            if (SameMod(installedMod, local)) continue;
            var market = MatchMarketMod(installedMod, catalog);
            // 本地已有的是同一个市场条目（升级同名模组）时不算冲突
            if (market != null && incoming.Id.Length > 0 &&
                string.Equals(market.Id, incoming.Id, StringComparison.OrdinalIgnoreCase)) continue;
            var source = market ?? new MarketMod { Name = installedMod.Name };

            AddReverse(installedMod, source, installedMod.Description, incoming, local, SourceIndex, seen, result);
            if (market == null) continue;
            AddReverse(installedMod, market, market.Description, incoming, local, SourceIndex, seen, result);

            if (market.Repo.Length == 0) continue;
            try
            {
                var readme = await DownloadService.FetchRepoReadmeAsync(market.Repo);
                if (readme.Length == 0) continue;
                var section = DownloadService.ReadmeSectionFor(readme, market);
                AddReverse(installedMod, market, section, incoming, local, SourceReadme, seen, result);
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 README 冲突声明失败：" + ex.Message);
            }
        }

        return result;
    }

    /// <summary>反向匹配：已装模组的文字里点到了即将安装模组的名字。</summary>
    private static void AddReverse(ModInfo installed, MarketMod installedSource, string text,
        MarketMod incoming, ModInfo localIncoming, string origin, HashSet<string> seen,
        List<ModConflict> result)
    {
        if (text.Length == 0) return;

        var names = new List<string>();
        void AddName(string raw)
        {
            var name = raw.Trim();
            if (name.Length < 3) return;
            if (GenericNames.Contains(name.ToLowerInvariant())) return;
            if (name.All(c => c < 128) && name.Length < 4) return;
            names.Add(name);
        }
        AddName(incoming.Name);
        AddName(incoming.Id);
        foreach (var boot in incoming.BootNames) AddName(boot);
        foreach (var alias in incoming.Aliases) AddName(alias);
        if (names.Count == 0) return;

        foreach (var sentence in ConflictSentences(text))
        {
            if (!names.Any(n => ContainsName(sentence, n))) continue;

            var pairKey = PairKey(installedSource, installed, incoming, localIncoming);
            if (!seen.Add(pairKey)) continue;

            result.Add(new ModConflict
            {
                PairKey = pairKey,
                LeftName = localIncoming.Name,
                LeftFile = localIncoming.FileName,
                RightName = installed.Name,
                RightFile = installed.FileName,
                Reason = Shorten(sentence),
                Source = origin,
            });
        }
    }

    /// <summary>把一个本地模组对应到市场条目（先精确、再长度 ≥4 的双向包含）。</summary>
    public static MarketMod? MatchMarketMod(ModInfo local, MarketCatalog catalog)
    {
        var name = DownloadService.NormalizeName(local.Name);
        if (name.Length == 0) return null;

        MarketMod? best = null;
        var bestScore = 0;

        foreach (var mod in catalog.Mods)
        {
            foreach (var key in KeysOf(mod))
            {
                if (key == name) return mod;
            }
        }

        foreach (var mod in catalog.Mods)
        {
            foreach (var key in KeysOf(mod))
            {
                if (key.Length < 4 || name.Length < 4) continue;
                if (key.Contains(name) || name.Contains(key))
                {
                    var score = Math.Min(key.Length, name.Length);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = mod;
                    }
                }
            }
        }
        return best;
    }

    private static IEnumerable<string> KeysOf(MarketMod mod)
    {
        var keys = new List<string>();
        void Add(string raw)
        {
            var key = DownloadService.NormalizeName(raw);
            if (key.Length > 0) keys.Add(key);
        }
        Add(mod.Name);
        Add(mod.Id);
        foreach (var boot in mod.BootNames) Add(boot);
        foreach (var alias in mod.Aliases) Add(alias);
        return keys;
    }

    /// <summary>从一段文本里找出写着冲突的句子，再匹配市场里已知的模组名。</summary>
    private static void AddFromText(ModInfo local, MarketMod source, string text, string origin,
        List<ModInfo> enabled, MarketCatalog catalog, HashSet<string> seen, List<ModConflict> result)
    {
        if (text.Length == 0) return;

        var candidates = new List<(string Text, MarketMod Mod)>();
        foreach (var mod in catalog.Mods)
        {
            if (source.Id.Length > 0 && string.Equals(mod.Id, source.Id, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var raw in new[] { mod.Name }.Concat(mod.Aliases).Concat(mod.BootNames))
            {
                var name = raw.Trim();
                if (name.Length < 3) continue;
                if (GenericNames.Contains(name.ToLowerInvariant())) continue;
                if (name.All(c => c < 128) && name.Length < 4) continue;
                candidates.Add((name, mod));
            }
        }
        candidates = candidates.OrderByDescending(c => c.Text.Length).ToList();

        foreach (var sentence in ConflictSentences(text))
        {
            var hits = new List<MarketMod>();
            foreach (var candidate in candidates)
            {
                if (!ContainsName(sentence, candidate.Text)) continue;
                if (hits.Any(h => IdEquals(h, candidate.Mod))) continue;
                hits.Add(candidate.Mod);
            }

            foreach (var hit in hits)
            {
                var targets = DownloadService.FindInstalledMatches(hit, enabled)
                    .Where(t => !SameMod(t, local)).ToList();
                if (targets.Count == 0) continue;

                var pairKey = PairKey(source, local, hit, targets[0]);
                if (!seen.Add(pairKey)) continue;

                result.Add(new ModConflict
                {
                    PairKey = pairKey,
                    LeftName = local.Name,
                    LeftFile = local.FileName,
                    RightName = targets[0].Name,
                    RightFile = targets[0].FileName,
                    Reason = Shorten(sentence),
                    Source = origin,
                });
            }
        }
    }

    private static bool IdEquals(MarketMod a, MarketMod b)
    {
        if (a.Id.Length > 0 && b.Id.Length > 0)
            return string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameMod(ModInfo a, ModInfo b)
    {
        if (DownloadService.NormalizeName(a.Name) == DownloadService.NormalizeName(b.Name)) return true;
        if (a.FileName.Length > 0 && b.FileName.Length > 0)
            return string.Equals(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>冲突对键：双方市场 id（没有 id 时退回规范化名字）排序后连接，按版本持久化。</summary>
    private static string PairKey(MarketMod source, ModInfo localSource, MarketMod target, ModInfo localTarget)
    {
        var left = source.Id.Length > 0 ? source.Id : DownloadService.NormalizeName(localSource.Name);
        var right = target.Id.Length > 0 ? target.Id : DownloadService.NormalizeName(localTarget.Name);
        if (left.Length == 0) left = DownloadService.NormalizeName(localSource.Name);
        if (right.Length == 0) right = DownloadService.NormalizeName(localTarget.Name);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0
            ? left + "|" + right
            : right + "|" + left;
    }

    private static IEnumerable<string> ConflictSentences(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            foreach (var sentence in Regex.Split(line, @"[。；;!！?？]"))
            {
                var current = sentence.Trim().TrimStart('-', '*', '>', '#', ' ');
                if (current.Length == 0) continue;
                if (!ConflictKeywords.Any(k => current.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                yield return current;
            }
        }
    }

    private static bool ContainsName(string text, string name) =>
        name.All(c => c < 128)
            ? Regex.IsMatch(text, "(?<![A-Za-z0-9])" + Regex.Escape(name) + "(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase)
            : text.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string sentence) =>
        sentence.Length > 160 ? sentence[..160] + "…" : sentence;
}
