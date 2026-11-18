using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>把用户拖动过的分栏尺寸存进 launcher 配置，下次启动继续沿用。</summary>
public static class LayoutStore
{
    public static double? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var cfg = AppConfig.Load();
        return cfg.Layout.Sizes.TryGetValue(key, out var value) && value > 0 ? value : null;
    }

    public static void Set(string key, double value)
    {
        if (string.IsNullOrWhiteSpace(key) || value <= 0) return;
        var cfg = AppConfig.Load();
        cfg.Layout.Sizes[key] = Math.Round(value, 1);
        try { AppConfig.Save(cfg); } catch { }
    }

    /// <summary>自动布局下的比例（0-1，表示分割条前一侧占可用空间的比例）。</summary>
    public static double? GetRatio(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var cfg = AppConfig.Load();
        return cfg.Layout.Ratios.TryGetValue(key, out var value) && value > 0.02 && value < 0.98
            ? value
            : null;
    }

    public static void SetRatio(string key, double ratio)
    {
        if (string.IsNullOrWhiteSpace(key) || ratio <= 0.02 || ratio >= 0.98) return;
        var cfg = AppConfig.Load();
        cfg.Layout.Ratios[key] = Math.Round(ratio, 4);
        try { AppConfig.Save(cfg); } catch { }
    }

    public static void Clear()
    {
        var cfg = AppConfig.Load();
        if (cfg.Layout.Sizes.Count == 0 && cfg.Layout.Ratios.Count == 0) return;
        cfg.Layout.Sizes.Clear();
        cfg.Layout.Ratios.Clear();
        try { AppConfig.Save(cfg); } catch { }
    }
}
