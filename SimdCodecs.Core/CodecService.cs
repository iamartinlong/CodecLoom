using System.Diagnostics;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Webp;

namespace SimdCodecs.Core;

/// <summary>解码结果：统一为 Bgra32，可直接喂给 Avalonia 的 WriteableBitmap。</summary>
public sealed class LoadedImage
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required ImageInfo Info { get; init; }
    public required ImageMetadata Metadata { get; init; }
    public required double DecodeMs { get; init; }
    public required long FileBytes { get; init; }
    public required string Path { get; init; }
    public required int Scale { get; init; }
    public required bool Oriented { get; init; }

    public PixelRect? Region { get; init; }
    public int FrameIndex { get; init; }
    public int FrameCount { get; init; } = 1;
    public TimeSpan? FrameDuration { get; init; }
}

/// <summary>导出预设：单张导出与批量转换共用同一套编码参数。</summary>
public sealed record ExportTarget(string Label, ImageFormat Format, string Extension,
    bool QuantizePng = false, bool LosslessWebp = false);

/// <summary>文件队列里的轻量条目：只读文件头，不解码整张图。</summary>
public sealed record ImageEntry(string Path, long Bytes, ImageFormat Format,
    int Width, int Height, int FrameCount, bool Decodable, string? Error = null)
{
    public string Summary => Error is not null
        ? "无法识别"
        : $"{Width}×{Height} · {Format} · {Bytes / 1024.0:F0} KB";
}

/// <summary>
/// SimdCodecs 只负责"文件字节 &lt;-&gt; 像素"；UI / Web 层负责显示与交互，两者在这里解耦。
/// 本文件不引用任何 Avalonia 或 ASP.NET Core 类型。
/// 每个能力都提供「文件路径」与「内存字节」两个入口：桌面端走文件，服务端全程不落盘。
/// </summary>
public static class CodecService
{
    private static readonly object Gate = new();
    private static bool _heifRegistered;

    public static readonly IReadOnlyList<ExportTarget> Targets = new[]
    {
        new ExportTarget("JPEG", ImageFormat.Jpeg, ".jpg"),
        new ExportTarget("PNG", ImageFormat.Png, ".png"),
        new ExportTarget("PNG 量化", ImageFormat.Png, ".png", QuantizePng: true),
        new ExportTarget("WebP", ImageFormat.Webp, ".webp"),
        new ExportTarget("WebP 无损", ImageFormat.Webp, ".webp", LosslessWebp: true)
    };

    public static ExportTarget Target(string label) =>
        Targets.FirstOrDefault(t => t.Label == label) ?? Targets[0];

    public static ExportTarget? FindTarget(string? label) =>
        Targets.FirstOrDefault(t => string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase))
        ?? Targets.FirstOrDefault(t => string.Equals(t.Extension.TrimStart('.'), label,
            StringComparison.OrdinalIgnoreCase));

    static CodecService() => EnsureHeif();

    /// <summary>HEVC 有专利负担，HEIF 必须由应用显式注册，门面包不会自动引用。</summary>
    public static void EnsureHeif()
    {
        lock (Gate)
        {
            if (_heifRegistered) return;
            ImageCodecs.Register(HeifImageDecoder.Registration);
            _heifRegistered = true;
        }
    }

    public static bool HeifEnabled => ImageCodecs.IsDecodable(ImageFormat.Heif);

    // ---------------- 头部预检 ----------------

    /// <summary>只读文件头，用于批量列表 / 上传前置校验，能挡住伪装扩展名和图片炸弹。</summary>
    public static ImageEntry Inspect(string path)
    {
        try { return InspectCore(File.ReadAllBytes(path), path); }
        catch (Exception ex)
        {
            return new ImageEntry(path, 0, ImageFormat.Unknown, 0, 0, 0, false,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static ImageEntry Inspect(byte[] data, string displayName = "<memory>")
    {
        try { return InspectCore(data, displayName); }
        catch (Exception ex)
        {
            return new ImageEntry(displayName, data.LongLength, ImageFormat.Unknown, 0, 0, 0, false,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ImageEntry InspectCore(byte[] data, string path)
    {
        EnsureHeif();
        if (!ImageCodecs.TryReadInfo(data, out ImageInfo info))
        {
            ImageFormat f = ImageCodecs.DetectFormat(data);
            return new ImageEntry(path, data.LongLength, f, 0, 0, 0, false,
                $"识别为 {f}，读不出尺寸（文件头损坏？）");
        }
        bool ok = ImageCodecs.IsDecodable(info.Format);
        return new ImageEntry(path, data.LongLength, info.Format, info.Width, info.Height,
            Math.Max(1, info.FrameCount), ok,
            ok ? null : $"识别为 {info.Format}，但当前版本不支持解码");
    }

    // ---------------- 解码 ----------------

    /// <summary>
    /// 解码成 Bgra32。region 用「缩放之后、显示坐标」的像素矩形；frameIndex &gt; 0 走 ImageDecoder 会话。
    /// 单帧与带 region 的解码共用门面的快路径（Region 是 DecodeOptions 的一部分）。
    /// </summary>
    public static LoadedImage Load(string path, int scale = 1, bool applyOrientation = true,
        PixelRect? region = null, int frameIndex = 0, CancellationToken ct = default)
        => LoadCore(File.ReadAllBytes(path), path, scale, applyOrientation, region, frameIndex, ct);

    /// <summary>内存态解码：HTTP 上传的字节直接进来，全过程不写临时文件。</summary>
    public static LoadedImage Load(byte[] data, string displayName = "<memory>",
        int scale = 1, bool applyOrientation = true, PixelRect? region = null,
        int frameIndex = 0, CancellationToken ct = default)
        => LoadCore(data, displayName, scale, applyOrientation, region, frameIndex, ct);

    private static LoadedImage LoadCore(byte[] data, string path, int scale, bool applyOrientation,
        PixelRect? region, int frameIndex, CancellationToken ct)
    {
        EnsureHeif();
        ct.ThrowIfCancellationRequested();

        if (!ImageCodecs.TryReadInfo(data, out ImageInfo info))
            throw new InvalidDataException("无法识别图片格式，或文件头损坏。");

        if (!ImageCodecs.IsDecodable(info.Format))
            throw new NotSupportedException(
                $"识别为 {info.Format}，但当前版本不支持解码（AVIF / JPEG XL 只能识别）。");

        var options = new DecodeOptions
        {
            ScaleDenominator = scale,
            ApplyOrientation = applyOrientation,
            Region = region
        };

        ImageCodecs.TryReadMetadata(data, out ImageMetadata? metadata);
        var sw = Stopwatch.StartNew();

        if (frameIndex == 0)
        {
            using PixelBuffer buffer = ImageCodecs.Decode(data, PixelFormat.Bgra32, options, ct);
            sw.Stop();
            return Make(data, path, info, buffer.Pixels.ToArray(), buffer.Width, buffer.Height,
                buffer.Stride, metadata, sw.Elapsed.TotalMilliseconds, scale, applyOrientation,
                region, 0, Math.Max(1, info.FrameCount), null);
        }

        // 多帧：ImageDecoder 保存动画画布状态，逐帧 Decode 得到的是合成后的画面。
        using ImageDecoder decoder = ImageDecoder.Open(new ReadOnlyMemory<byte>(data));
        if (frameIndex >= decoder.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex),
                $"文件只有 {decoder.FrameCount} 帧，取不到第 {frameIndex} 帧。");

        ImageFrame frame = decoder.GetFrame(frameIndex);
        IDecodedImage decoded = frame.Decode(options, ct);
        try
        {
            int w = decoded.Width, h = decoded.Height, stride = w * 4;
            byte[] pixels = new byte[stride * h];
            if (!decoded.TryCopyTo(pixels, stride, PixelFormat.Bgra32))
                throw new InvalidDataException($"第 {frameIndex} 帧无法转成 Bgra32。");
            sw.Stop();
            return Make(data, path, info, pixels, w, h, stride, metadata,
                sw.Elapsed.TotalMilliseconds, scale, applyOrientation, region,
                frameIndex, decoder.FrameCount, frame.Duration);
        }
        finally
        {
            (decoded as IDisposable)?.Dispose();
        }
    }

    private static LoadedImage Make(byte[] data, string path, ImageInfo info, byte[] pixels,
        int w, int h, int stride, ImageMetadata? metadata, double ms, int scale, bool oriented,
        PixelRect? region, int frameIndex, int frameCount, TimeSpan? duration) => new()
        {
            Pixels = pixels,
            Width = w,
            Height = h,
            Stride = stride,
            Info = info,
            Metadata = metadata ?? ImageMetadata.Empty,
            DecodeMs = ms,
            FileBytes = data.LongLength,
            Path = path,
            Scale = scale,
            Oriented = oriented,
            Region = region,
            FrameIndex = frameIndex,
            FrameCount = frameCount,
            FrameDuration = duration
        };

    // ---------------- 缩略图 ----------------

    /// <summary>生成文件队列的缩略图像素：优先用内嵌缩略图，否则 1/8 解码，再最近邻降到方框内。</summary>
    public static (byte[] Pixels, int Width, int Height, int Stride) LoadThumbnail(
        string path, int box = 96, CancellationToken ct = default)
        => LoadThumbnailCore(File.ReadAllBytes(path), box, ct);

    public static (byte[] Pixels, int Width, int Height, int Stride) LoadThumbnail(
        byte[] data, int box = 96, CancellationToken ct = default)
        => LoadThumbnailCore(data, box, ct);

    private static (byte[] Pixels, int Width, int Height, int Stride) LoadThumbnailCore(
        byte[] data, int box, CancellationToken ct)
    {
        EnsureHeif();
        ct.ThrowIfCancellationRequested();

        byte[]? src = null;
        int w = 0, h = 0;

        if (ImageCodecs.TryDecodeThumbnail(data, out IDecodedImage? thumb) && thumb is not null)
        {
            try
            {
                w = thumb.Width;
                h = thumb.Height;
                int stride = w * 4;
                src = new byte[stride * h];
                if (!thumb.TryCopyTo(src, stride, PixelFormat.Bgra32)) src = null;
            }
            finally
            {
                (thumb as IDisposable)?.Dispose();
            }
        }

        if (src is null)
        {
            using PixelBuffer buffer = ImageCodecs.Decode(data, PixelFormat.Bgra32,
                new DecodeOptions { ScaleDenominator = 8, ApplyOrientation = true }, ct);
            w = buffer.Width;
            h = buffer.Height;
            int stride = w * 4;
            src = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                int available = Math.Min(stride, buffer.Pixels.Length - y * buffer.Stride);
                if (available <= 0) break;
                buffer.Pixels.Slice(y * buffer.Stride, available).CopyTo(src.AsSpan(y * stride, available));
            }
        }

        double ratio = Math.Min(1.0, Math.Min(box / (double)w, box / (double)h));
        int tw = Math.Max(1, (int)Math.Round(w * ratio));
        int th = Math.Max(1, (int)Math.Round(h * ratio));
        byte[] result = Pixels.Downsample(src, w, h, tw, th);
        return (result, tw, th, tw * 4);
    }

    // ---------------- 编码 ----------------

    /// <summary>从原始像素写出文件，用于自检与"生成测试图"。</summary>
    public static void Write(byte[] pixels, int width, int height, string path,
        ImageFormat format, int quality = 90, bool quantizePng = false, bool losslessWebp = false)
    {
        EnsureHeif();
        using FileStream fs = File.Create(path);
        ImageCodecs.Encode(pixels, width, height, PixelFormat.Bgra32, format, fs,
            width * 4, BuildOptions(format, quality, quantizePng, losslessWebp), ImageMetadata.Empty);
    }

    /// <summary>从原始像素直接得到编码后的字节，供 API / 自检生成内存测试图。</summary>
    public static byte[] EncodeBytes(byte[] pixels, int width, int height, ImageFormat format,
        int quality = 90, bool quantizePng = false, bool losslessWebp = false)
    {
        EnsureHeif();
        using var ms = new MemoryStream();
        ImageCodecs.Encode(pixels, width, height, PixelFormat.Bgra32, format, ms,
            width * 4, BuildOptions(format, quality, quantizePng, losslessWebp), ImageMetadata.Empty);
        return ms.ToArray();
    }

    /// <summary>把当前显示的像素另存为目标格式，并透传原图的 EXIF / XMP / ICC。</summary>
    public static void Save(LoadedImage image, string path, ExportTarget target, int quality = 90,
        CancellationToken ct = default)
    {
        using FileStream fs = File.Create(path);
        ImageCodecs.Encode(image.Pixels, image.Width, image.Height, PixelFormat.Bgra32,
            target.Format, fs, image.Stride,
            BuildOptions(target.Format, quality, target.QuantizePng, target.LosslessWebp),
            image.Metadata, ct);
    }

    /// <summary>内存态编码：API 直接把结果写进 HTTP 响应体，不产生临时文件。</summary>
    public static byte[] SaveToBytes(LoadedImage image, ExportTarget target, int quality = 90,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        ImageCodecs.Encode(image.Pixels, image.Width, image.Height, PixelFormat.Bgra32,
            target.Format, ms, image.Stride,
            BuildOptions(target.Format, quality, target.QuantizePng, target.LosslessWebp),
            image.Metadata, ct);
        return ms.ToArray();
    }

    /// <summary>兼容旧调用：按格式枚举直接保存。</summary>
    public static void Save(LoadedImage image, string path, ImageFormat format,
        int quality = 90, bool quantizePng = false, bool losslessWebp = false)
    {
        var target = Targets.FirstOrDefault(t => t.Format == format
            && t.QuantizePng == quantizePng && t.LosslessWebp == losslessWebp)
            ?? new ExportTarget(format.ToString(), format, "." + format.ToString().ToLowerInvariant(),
                quantizePng, losslessWebp);
        Save(image, path, target, quality);
    }

    public static EncodeOptions? BuildOptions(ExportTarget target, int quality) =>
        BuildOptions(target.Format, quality, target.QuantizePng, target.LosslessWebp);

    private static EncodeOptions? BuildOptions(ImageFormat format, int quality,
        bool quantizePng, bool losslessWebp) => format switch
        {
            ImageFormat.Jpeg => new JpegEncodeOptions { Quality = quality, Progressive = true },
            ImageFormat.Png => new PngWriteOptions
            {
                Quantize = quantizePng ? new PngQuantizeOptions() : null
            },
            ImageFormat.Webp => losslessWebp
                ? new WebpLosslessEncodeOptions()
                : new WebpLossyEncodeOptions { Quality = quality },
            _ => null
        };
}

/// <summary>极简像素工具：只做最近邻缩小，用于列表缩略图（不做插值，避免引入图像处理依赖）。</summary>
public static class Pixels
{
    public static byte[] Downsample(byte[] src, int w, int h, int tw, int th)
    {
        byte[] dst = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
        {
            int sy = Math.Min(h - 1, y * h / th);
            for (int x = 0; x < tw; x++)
            {
                int sx = Math.Min(w - 1, x * w / tw);
                Buffer.BlockCopy(src, (sy * w + sx) * 4, dst, (y * tw + x) * 4, 4);
            }
        }
        return dst;
    }
}
