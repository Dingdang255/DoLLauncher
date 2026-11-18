namespace DoLLauncher.Themes;

/// <summary>
/// 与 WPF 版 ThemeService 中的亮 / 暗调色板保持一致。
/// 这里只放原始配色，主色替换与饱和度混合统一交给 Core 的 ThemePaletteBuilder。
/// </summary>
public static class ThemePalette
{
    public static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["BgBrush"] = "#FFF5F5F5",
        ["PanelBrush"] = "#FFFFFFFF",
        ["CardBrush"] = "#FFFFFFFF",
        ["CardHoverBrush"] = "#FFF5F7FA",
        ["CardSelectedBrush"] = "#FFEAF3FF",
        ["BorderBrush"] = "#FFE3E3E3",
        ["TextBrush"] = "#FF222222",
        ["MutedBrush"] = "#FF737373",
        ["AccentBrush"] = "#FF1370F3",
        ["AccentHoverBrush"] = "#FF0B5BCB",
        ["AccentSoftBrush"] = "#FFD5E6FD",
        ["AccentPaleBrush"] = "#FFEAF3FF",
        ["AccentStrongBrush"] = "#FF1370F3",
        ["AccentOnBrush"] = "#FFFFFFFF",
        ["DangerBrush"] = "#FFD93B3B",
        ["WarnBrush"] = "#FFD97706",
        ["WarnPaleBrush"] = "#FFFDF3E3",
        ["NavHoverBrush"] = "#FFF0F2F5",
        ["WhiteBrush"] = "#FFFFFFFF",
        ["TabChipActiveBgBrush"] = "#FF1370F3",
        ["TabChipActiveFgBrush"] = "#FFFFFFFF",
        ["TabChipIdleFgBrush"] = "#FF444444",
        ["TabChipHoverBgBrush"] = "#FFE2E8F0",
        ["DangerBorderBrush"] = "#FFF0C6C4",
        ["DangerPaleBrush"] = "#FFFDF2F1",
        ["WarnBorderBrush"] = "#FFF2D3A8",
        ["CheckBorderBrush"] = "#FFC8C8C8",
        ["SuccessBrush"] = "#FF3F9D63",
        ["RunningBrush"] = "#FF3F9D63",
        ["RunningBgBrush"] = "#FFE9F6EE",
        ["ProgressTrackBrush"] = "#FFEDEDED",
    };

    public static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["BgBrush"] = "#FF1C1C1E",
        ["PanelBrush"] = "#FF242426",
        ["CardBrush"] = "#FF2C2C2F",
        ["CardHoverBrush"] = "#FF35353A",
        ["CardSelectedBrush"] = "#FF2A3B55",
        ["BorderBrush"] = "#FF3E3E42",
        ["TextBrush"] = "#FFEAEAEA",
        ["MutedBrush"] = "#FF9A9A9F",
        ["AccentBrush"] = "#FF4C9AFF",
        ["AccentHoverBrush"] = "#FF6FB2FF",
        ["AccentSoftBrush"] = "#FF2B4A70",
        ["AccentPaleBrush"] = "#FF22344C",
        ["AccentStrongBrush"] = "#FF4C9AFF",
        ["AccentOnBrush"] = "#FF111417",
        ["DangerBrush"] = "#FFFF6B6B",
        ["WarnBrush"] = "#FFFFB454",
        ["WarnPaleBrush"] = "#FF3A3224",
        ["NavHoverBrush"] = "#FF313136",
        ["WhiteBrush"] = "#FFFFFFFF",
        ["TabChipActiveBgBrush"] = "#FF22344C",
        ["TabChipActiveFgBrush"] = "#FF4C9AFF",
        ["TabChipIdleFgBrush"] = "#FF9A9A9F",
        ["TabChipHoverBgBrush"] = "#FF313136",
        ["DangerBorderBrush"] = "#FF5C2A2A",
        ["DangerPaleBrush"] = "#FF3A2424",
        ["WarnBorderBrush"] = "#FF5A452B",
        ["CheckBorderBrush"] = "#FF5A5A5E",
        ["SuccessBrush"] = "#FF4CC97E",
        ["RunningBrush"] = "#FF4CC97E",
        ["RunningBgBrush"] = "#FF1E3327",
        ["ProgressTrackBrush"] = "#FF3A3A3E",
    };
}
