using System.Runtime.InteropServices;
using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Platform.Windows;

/// <summary>Windows 主题平台实现：复用 ThemeService 的标题栏样式，DPI 与像素对齐走 user32。</summary>
public sealed class WindowsThemePlatform : IThemePlatform
{
    public bool SystemUsesDarkMode
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public void ApplyTitleBar(nint hwnd, bool dark) => ThemeService.ApplyTitleBar(hwnd, dark);

    public double GetDpiScale(nint hwnd)
    {
        if (hwnd == 0) return 1d;
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96d : 1d;
        }
        catch
        {
            // 老系统没有 GetDpiForWindow 时按 100% 处理
            return 1d;
        }
    }

    public void SnapWindowToPixels(nint hwnd, double widthDip)
    {
        if (hwnd == 0 || widthDip <= 0) return;
        try
        {
            var scale = GetDpiScale(hwnd);
            var widthPx = (int)Math.Ceiling(widthDip * scale);
            if (!GetWindowRect(hwnd, out var rect)) return;
            var heightPx = rect.Bottom - rect.Top;
            SetWindowPos(hwnd, 0, 0, 0, widthPx, heightPx, SwpNoMove | SwpNoZOrder | SwpNoActivate);
        }
        catch
        {
            // 取不到窗口尺寸时保持原样
        }
    }

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out PixelRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter,
        int x, int y, int width, int height, uint flags);
}
