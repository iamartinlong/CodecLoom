namespace SimdCodecs.Processing;

/// <summary>
/// 处理管线编排：缩放 → 色调 → 水印 → 布局转换。
/// 输入是 SimdCodecs 解出的 Bgra32，输出可以是任意下游布局；
/// 全程纯托管，不需要任何 UI 框架或原生库。
/// </summary>
public static class ImageProcessor
{
    public static ProcessResult Apply(BgraSurface surface, ProcessOptions? options = null) =>
        Apply(surface.Pixels, surface.Width, surface.Height, options);

    public static ProcessResult Apply(byte[] bgra, int width, int height,
        ProcessOptions? options = null)
    {
        ProcessOptions o = (options ?? new ProcessOptions()).Normalize();
        var s = new BgraSurface(bgra, width, height);
        var applied = new List<string>();

        byte[] working = (byte[])bgra.Clone();
        int w = width, h = height;

        if (o.ResizeWidth > 0 || o.ResizeHeight > 0)
        {
            int tw = o.ResizeWidth > 0 ? o.ResizeWidth
                : Math.Max(1, (int)Math.Round(w * o.ResizeHeight / (double)h));
            int th = o.ResizeHeight > 0 ? o.ResizeHeight
                : Math.Max(1, (int)Math.Round(h * o.ResizeWidth / (double)w));
            working = ImageOps.ResizeBilinear(working, w, h, tw, th);
            applied.Add($"双线性缩放 {w}×{h} → {tw}×{th}");
            w = tw; h = th;
        }

        if (o.Contrast != 1.0) applied.Add($"对比度 ×{o.Contrast:F2}");
        if (o.Brightness != 0) applied.Add($"亮度 {o.Brightness:+0;-0;0}");
        if (o.Grayscale) applied.Add("灰度化");
        if (o.Invert) applied.Add("反色");
        ImageOps.Adjust(working, o.Brightness, o.Contrast, o.Grayscale, o.Invert);

        if (o.Watermark is { Length: > 0 } text)
        {
            ImageOps.DrawWatermark(working, w, h, text);
            applied.Add($"几何水印「{text}」");
        }

        byte[] output;
        int stride;
        switch (o.Layout)
        {
            case PixelLayout.Gray8:
                output = ImageOps.ToGray8(working, w, h);
                stride = w;
                applied.Add("Gray8 连续布局");
                break;
            case PixelLayout.Bgr24:
                output = ImageOps.ToBgr24(working, w, h);
                stride = w * 3;
                applied.Add("Bgr24 连续布局");
                break;
            case PixelLayout.Rgb24:
                output = ImageOps.ToRgb24(working, w, h);
                stride = w * 3;
                applied.Add("Rgb24 连续布局");
                break;
            default:
                output = working;
                stride = w * 4;
                break;
        }

        return new ProcessResult(output, w, h, stride, o.Layout, applied);
    }

    /// <summary>把处理结果编码回 Bgra32；Gray8 会自然变成灰度图，BGR24 会还原彩色。</summary>
    public static byte[] ToDisplayBgra(ProcessResult result) =>
        ImageOps.ToBgra32(result.Pixels, result.Layout, result.Width, result.Height);

    /// <summary>下游 CV 算子：Gray8 → Otsu → 行投影，用于验证布局真的可消费。</summary>
    public static CvAnalysis AnalyzeForOcr(ProcessResult result)
    {
        byte[] gray = result.Layout == PixelLayout.Gray8
            ? result.Pixels
            : ImageOps.ToGray8(ImageOps.ToBgra32(result.Pixels, result.Layout,
                result.Width, result.Height), result.Width, result.Height);
        return CvInput.Analyze(gray, result.Width, result.Height);
    }
}
