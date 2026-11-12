using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SimdCodecsViewer;

/// <summary>
/// Windows 深色标题栏：窗口内容切成暗色后，DWM 绘制的系统标题栏不会自动跟随
/// （这正是截图里「标题栏还是白色」的原因）。需要在窗口句柄就绪后显式调用
/// DwmSetWindowAttribute：
///   - DWMWA_USE_IMMERSIVE_DARK_MODE(20)：让标题栏走系统暗色（Win10 1903+ / Win11），
///     更老的 Win10 build 只认 19，失败时回退重试；
///   - DWMWA_CAPTION_COLOR(35) / DWMWA_TEXT_COLOR(36)：Win11 22000+ 可直接把标题栏
///     底色与文字色设成与 UI 一致，视觉上完全融合；Win10 不认识这两个值会返回非 0，
///     静默忽略即可（已拿到标准暗色标题栏）。
///
/// 关键坑（实测）：Avalonia Win32 的 WindowImpl.SetFrameThemeVariant 只认「系统」
/// 颜色主题、不看应用 ThemeVariant，且在窗口显示流程中会把 attribute 20 重设回
/// 系统值（浅色系统 → 0），因此必须在 Opened 之后**延迟再覆盖一次**。另外当系统
/// 颜色主题变化时 Avalonia 也会重设，这里订阅 PlatformSettings.ColorValuesChanged
/// 再覆盖，防止运行期被改回浅色。
///
/// 非 Windows 平台为 no-op；任何失败都不抛异常（老系统标题栏保持浅色不影响功能）。
/// NativeAOT 友好：显式 DllImport，无反射。
/// </summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModePre20H1 = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>窗口 Opened 后句柄才可用，因此在 Opened 时机应用。</summary>
    public static void Attach(Window window)
    {
        window.Opened += OnOpened;
        // 系统颜色主题变化（如用户切 Windows 深浅色）时，Avalonia 会按系统色重设，
        // 这里在 UI 线程再覆盖回来。
        if (Application.Current?.PlatformSettings is { } settings)
            settings.ColorValuesChanged += (_, _) =>
                Dispatcher.UIThread.Post(() => SafeApply(window), DispatcherPriority.Background);
    }

    private static async void OnOpened(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        SafeApply(window);
        // Avalonia 在 Show 流程后段（Opened 事件之后）还会按系统色重设一次，延迟覆盖。
        var dispatcher = Dispatcher.UIThread;
        dispatcher.Post(() => SafeApply(window), DispatcherPriority.Background);
        try { await Task.Delay(150); } catch { /* 时序兜底，失败由下一次覆盖 */ }
        if (window.PlatformImpl is not null) SafeApply(window);
    }

    public static bool Apply(Window window)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return false;
        var hwnd = handle.Handle;

        int one = 1;
        int hr20 = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref one, sizeof(int));
        bool ok = hr20 == 0;
        if (!ok)
            ok = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModePre20H1, ref one, sizeof(int)) == 0;

        // Win11 增强：标题栏底色 = 窗口底色、文字 = 主文字色（COLORREF 为 0x00BBGGRR）。
        var bg = Ui.Bg;   // #141518
        var fg = Ui.Text; // #E8EAED
        TrySet(hwnd, DwmwaCaptionColor, ColorRef(bg.R, bg.G, bg.B));
        TrySet(hwnd, DwmwaTextColor, ColorRef(fg.R, fg.G, fg.B));
        return ok;
    }

    private static void SafeApply(Window window)
    {
        try { Apply(window); } catch { /* 视觉增强失败不影响功能 */ }
    }

    private static int ColorRef(byte r, byte g, byte b) => (b << 16) | (g << 8) | r;

    private static bool TrySet(IntPtr hwnd, int attr, int value)
        => DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)) == 0;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
