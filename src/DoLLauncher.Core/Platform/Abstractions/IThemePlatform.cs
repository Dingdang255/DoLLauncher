namespace DoLLauncher.Platform.Abstractions;

/// <summary>主题相关的平台能力：系统深浅色、标题栏样式、DPI 与像素对齐。</summary>
public interface IThemePlatform
{
    /// <summary>系统当前是否使用深色模式。</summary>
    bool SystemUsesDarkMode { get; }

    /// <summary>按窗口句柄设置标题栏深色样式。</summary>
    void ApplyTitleBar(nint hwnd, bool dark);

    /// <summary>取窗口所在显示器的 DPI 缩放比例（1.0 = 100%）。</summary>
    double GetDpiScale(nint hwnd);

    /// <summary>把 DIP 宽度按物理像素取整后应用到窗口。</summary>
    void SnapWindowToPixels(nint hwnd, double widthDip);
}
