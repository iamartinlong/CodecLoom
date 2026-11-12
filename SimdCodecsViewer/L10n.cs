using System.Collections.Frozen;

namespace SimdCodecsViewer;

/// <summary>
/// 极轻量双语文案：key 即中文原文，默认语言 zh 时直接返回原文，
/// 保证旧行为与冒烟断言（Title 含「工作台」）零变化。
/// 覆盖范围：工具栏、左右栏标题、占位提示与操作按钮等静态标签；
/// 运行时拼接的状态消息（含文件名/耗时插值）仍为中文，如实登记为覆盖边界。
/// </summary>
public static class L10n
{
    public static string Language { get; set; } = "zh";

    public static bool IsEnglish => Language == "en";

    private static readonly FrozenDictionary<string, string> En = new Dictionary<string, string>
    {
        ["CodecLoom · 本地图片工作台"] = "CodecLoom · Local Image Workbench",
        ["打开图片…"] = "Open…",
        ["添加文件…"] = "Add…",
        ["清空队列"] = "Clear",
        ["导出当前…"] = "Export…",
        ["解码缩放"] = "Decode scale",
        ["导出格式"] = "Export format",
        ["质量"] = "Quality",
        ["EXIF 转正"] = "EXIF orient",
        ["1/1 原始"] = "1/1 original",
        ["语言"] = "Language",
        ["文件队列（可直接拖入）"] = "Queue (drop files here)",
        ["把 HEIC / TIFF / JPEG 2000 / JBIG2 / QOI / Netpbm\n拖进窗口即可批量加入"] =
            "Drag HEIC / TIFF / JPEG 2000 / JBIG2 / QOI / Netpbm\nfiles into the window to enqueue",
        ["打开或拖入图片开始\n\nAvalonia 自带解码器打不开 HEIC、TIFF、JPEG 2000、JBIG2、QOI、Netpbm\n这里全部由 Sdcb.SimdCodecs 解码，像素直写 WriteableBitmap"] =
            "Open or drop an image to start\n\nAvalonia's built-in decoders cannot open HEIC, TIFF, JPEG 2000, JBIG2, QOI, Netpbm.\nEverything here is decoded by Sdcb.SimdCodecs and blitted into a WriteableBitmap",
        ["◀ 上一帧"] = "◀ Prev frame",
        ["下一帧 ▶"] = "Next frame ▶",
        ["播放"] = "Play",
        ["暂停"] = "Pause",
        ["单帧"] = "Single frame",
        ["框选裁剪"] = "Crop select",
        ["在图上拖出矩形即可解码该区域"] = "Drag a rectangle on the image to decode that region",
        ["清除裁剪"] = "Clear crop",
        ["批量转换"] = "Batch convert",
        ["一键预设（HEIC 转码 / 扫描件拆页）"] = "Presets (HEIC transcode / scan split)",
        ["不使用预设"] = "No preset",
        ["输出目录"] = "Output folder",
        ["浏览…"] = "Browse…",
        ["多帧文件逐帧导出（GIF / APNG / 多页 TIFF）"] = "Export all frames (GIF / APNG / multi-page TIFF)",
        ["批量转换队列"] = "Convert queue",
        ["就绪"] = "Ready",
        ["图像处理（纯托管 · 零原生依赖）"] = "Image processing (pure managed · no native deps)",
        ["任意尺寸缩放 / 亮度对比度 / 灰度 / 反色 / 几何水印，以及 Gray8·BGR 裸像素导出"] =
            "Resize / brightness & contrast / grayscale / invert / geometric watermark, plus Gray8·BGR raw pixel export",
        ["尺寸"] = "Size",
        ["宽"] = "W",
        ["高"] = "H",
        ["（0=不缩放）"] = "(0 = keep size)",
        ["亮度"] = "Brightness",
        ["对比"] = "Contrast",
        ["灰度"] = "Grayscale",
        ["反色"] = "Invert",
        ["水印文本（空=不加水印）"] = "Watermark text (empty = none)",
        ["裸像素布局"] = "Raw layout",
        ["应用到预览"] = "Apply to preview",
        ["导出裸像素…"] = "Export raw pixels…",
        ["重置"] = "Reset",
        ["未应用处理"] = "No processing applied",
        ["尚未打开文件。\n\n先读文件头（尺寸 / 格式 / 帧数），再按需解码 —— 大文件不会一进门就吃满内存。"] =
            "No file opened.\n\nHeaders (size / format / frame count) are read first, then pixels on demand — large files never load all at once.",
        ["打开图片（可多选）"] = "Open images (multi-select)",
        ["图片"] = "Images",
        ["所有文件"] = "All files",
        ["追加到队列"] = "Append to queue",
        ["语言切换将在重启后生效"] = "Language change takes effect after restart",
    }.ToFrozenDictionary();

    /// <summary>取文案：zh 原样返回（不查表），en 查表并回退原文。</summary>
    public static string Tr(string text) =>
        !IsEnglish ? text : En.TryGetValue(text, out string? v) ? v : text;
}
