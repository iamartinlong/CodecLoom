using Avalonia.Media;

namespace SimdCodecsViewer;

/// <summary>
/// 工作台统一色板（专业深色）与少量可复用画刷。
/// 全部集中在这里，调整主题只改这一个文件。
/// </summary>
public static class Ui
{
    // ---- 基底 ----
    public static readonly Color Bg          = Color.FromRgb(0x14, 0x15, 0x18); // 窗口底
    public static readonly Color Panel       = Color.FromRgb(0x1D, 0x1F, 0x24); // 卡片面板
    public static readonly Color PanelAlt    = Color.FromRgb(0x24, 0x27, 0x2E); // 面板内嵌块
    public static readonly Color Border      = Color.FromRgb(0x2C, 0x30, 0x38); // 卡片描边
    public static readonly Color Preview     = Color.FromRgb(0x0F, 0x10, 0x13); // 预览区（更深）
    public static readonly Color Hover       = Color.FromRgb(0x32, 0x36, 0x3F); // 控件悬停

    // ---- 强调与语义 ----
    public static readonly Color Accent      = Color.FromRgb(0x4F, 0x8C, 0xFF); // 主色蓝
    public static readonly Color AccentDim   = Color.FromRgb(0x33, 0x64, 0xB8); // 主色按压
    public static readonly Color Ok          = Color.FromRgb(0x46, 0xB8, 0x6A); // 成功
    public static readonly Color Warn        = Color.FromRgb(0xE8, 0xB3, 0x45); // 跳过/警告
    public static readonly Color Error       = Color.FromRgb(0xE5, 0x5B, 0x4F); // 失败

    // ---- 文字 ----
    public static readonly Color Text        = Color.FromRgb(0xE8, 0xEA, 0xED); // 主文字
    public static readonly Color TextSub     = Color.FromRgb(0x9D, 0xA3, 0xAC); // 次级文字
    public static readonly Color TextFaint   = Color.FromRgb(0x6B, 0x71, 0x7B); // 弱化文字

    public static IBrush B(Color c) => new SolidColorBrush(c);
    public static IBrush B(Color c, byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

    public static Pen PenBorder(double thickness = 1) =>
        new(B(Border), thickness);
}
