using Sdcb.SimdCodecs.Abstractions;

namespace SimdCodecs.Core;

/// <summary>
/// 一键预设：把「源格式过滤 + 目标格式 + 质量 + 是否拆页」打包成一个可复用的业务动作。
/// 这是把通用编解码能力变成产品能力的关键一层 —— 用户不必理解 ImageFormat / 帧 / 量化，
/// 只要说「把 iPhone 照片转成能发的 JPEG」「把扫描件拆成一页一张」。
/// </summary>
public sealed record BatchPreset(
    string Id,
    string Label,
    string Description,
    ExportTarget Target,
    int Quality = 88,
    IReadOnlyList<ImageFormat>? SourceFormats = null,
    bool AllFrames = false,
    bool PageNumbering = false,
    bool MultiFrameOnly = false)
{
    /// <summary>预设只处理这些源格式；null 表示不限制。</summary>
    public bool Accepts(ImageFormat format) =>
        SourceFormats is null || SourceFormats.Contains(format);

    public string SourceHint => SourceFormats is null
        ? "全部格式"
        : string.Join(" / ", SourceFormats.Select(f => f.ToString()));
}

/// <summary>预设注册表。新增场景只需在这里加一行，三端 UI 均从 /api/presets 动态生成。</summary>
public static class BatchPresets
{
    public static readonly BatchPreset HeicToJpeg = new(
        "heic-jpeg", "HEIC → JPEG · 照片分享",
        "iPhone / 相机 HEIC 原图批量转 JPEG，浏览器与聊天软件都能直接打开",
        CodecService.Target("JPEG"), 88,
        new[] { ImageFormat.Heif });

    public static readonly BatchPreset HeicToPng = new(
        "heic-png", "HEIC → PNG · 无损归档",
        "HEIC 转无损 PNG，保留全部像素，适合长期存档与后续编辑",
        CodecService.Target("PNG"), 90,
        new[] { ImageFormat.Heif });

    public static readonly BatchPreset ScanSplit = new(
        "scan-split", "扫描件拆页 · TIFF / JP2 / JBIG2 → PNG",
        "多页 TIFF、JPEG 2000、JBIG2 传真件按页拆成单张 PNG，页码命名并附转换清单",
        CodecService.Target("PNG"), 90,
        new[] { ImageFormat.Tiff, ImageFormat.Jpeg2000, ImageFormat.Jbig2 },
        AllFrames: true, PageNumbering: true, MultiFrameOnly: true);

    public static readonly BatchPreset WebOptimize = new(
        "web-optimize", "网页瘦身 · WebP",
        "所有输入格式统一转有损 WebP，压缩体积优先，适合站点图片批量优化",
        CodecService.Target("WebP"), 82);

    public static IReadOnlyList<BatchPreset> All { get; } = new[]
    {
        HeicToJpeg, HeicToPng, ScanSplit, WebOptimize
    };

    public static BatchPreset? Find(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
          ?? All.FirstOrDefault(p => string.Equals(p.Label, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>用预设覆盖批量设置中未被显式指定的字段。</summary>
    public static BatchSettings Apply(BatchPreset preset, BatchSettings settings) => settings with
    {
        Target = preset.Target,
        Quality = preset.Quality,
        AllFrames = preset.AllFrames,
        SourceFilter = preset.SourceFormats,
        PageNumbering = preset.PageNumbering,
        MultiFrameOnly = preset.MultiFrameOnly,
        PresetId = preset.Id
    };
}
