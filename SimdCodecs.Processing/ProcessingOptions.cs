namespace SimdCodecs.Processing;

/// <summary>下游算子期望的像素布局。Bgra32 与 Avalonia / Skia 对齐；Gray8 / Bgr24 与 OCR / OpenCV 对齐。</summary>
public enum PixelLayout
{
    Bgra32,
    Gray8,
    Bgr24,
    Rgb24
}

public static class PixelLayoutInfo
{
    public static int BytesPerPixel(PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => 1,
        PixelLayout.Bgr24 or PixelLayout.Rgb24 => 3,
        _ => 4
    };

    public static string Mime(PixelLayout layout) => layout switch
    {
        PixelLayout.Gray8 => "application/x-gray8",
        PixelLayout.Bgr24 => "application/x-bgr24",
        PixelLayout.Rgb24 => "application/x-rgb24",
        _ => "application/x-bgra32"
    };
}

/// <summary>
/// 处理参数。全部可选，默认恒等变换。
/// Resize 是真正的任意尺寸双线性缩放（SimdCodecs 只提供 1/2^n 解码期降采样）。
/// </summary>
public sealed record ProcessOptions(
    int ResizeWidth = 0,
    int ResizeHeight = 0,
    double Brightness = 0,
    double Contrast = 1.0,
    bool Grayscale = false,
    bool Invert = false,
    string? Watermark = null,
    PixelLayout Layout = PixelLayout.Bgra32)
{
    public bool IsIdentity =>
        ResizeWidth == 0 && ResizeHeight == 0 && Brightness == 0 && Contrast == 1.0 &&
        !Grayscale && !Invert && Watermark is null && Layout == PixelLayout.Bgra32;

    public ProcessOptions Normalize() => this with
    {
        ResizeWidth = Math.Clamp(ResizeWidth, 0, 16_384),
        ResizeHeight = Math.Clamp(ResizeHeight, 0, 16_384),
        Brightness = Math.Clamp(Brightness, -100, 100),
        Contrast = Math.Clamp(Contrast, 0.1, 3.0),
        Watermark = string.IsNullOrWhiteSpace(Watermark)
            ? null : Watermark.Trim()[..Math.Min(48, Watermark.Trim().Length)]
    };
}

/// <summary>处理结果：Pixels 始终是 Layout 指定的紧凑布局，Stride 已按行对齐到 bpp 的整数倍（无 padding）。</summary>
public sealed record ProcessResult(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    PixelLayout Layout,
    IReadOnlyList<string> Applied)
{
    public int BytesPerPixel => PixelLayoutInfo.BytesPerPixel(Layout);
    public string Summary => Applied.Count == 0 ? "恒等变换" : string.Join(" → ", Applied);
}

/// <summary>Bgra32 像素面，构造时校验长度，避免后续算子越界。</summary>
public sealed class BgraSurface
{
    public BgraSurface(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "宽高必须为正数。");
        if (pixels.Length < width * 4 * height)
            throw new ArgumentException(
                $"像素数组长度 {pixels.Length} 小于 {width}×{height}×4 = {width * 4 * height}。",
                nameof(pixels));
        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
}
