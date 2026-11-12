using SimdCodecs.Processing;
using Xunit;

namespace SimdCodecs.Tests;

/// <summary>纯托管处理层：任意尺寸缩放、色调算子、几何水印、裸像素布局、OCR 前处理。</summary>
public class ProcessingTests
{
    private const int W = 320, H = 240;
    private static byte[] Document => TestData.Document(W, H);

    [Fact]
    public void 默认参数是恒等变换()
    {
        var o = new ProcessOptions();
        Assert.True(o.IsIdentity);
        Assert.Equal("恒等变换", ImageProcessor.Apply(TestData.Solid(2, 2, 1, 2, 3), 2, 2, o).Summary);
        Assert.False((o with { Layout = PixelLayout.Gray8 }).IsIdentity);
        Assert.False((o with { Brightness = 1 }).IsIdentity);
    }

    [Fact]
    public void Normalize_把所有参数钳到安全区间()
    {
        var wild = new ProcessOptions(ResizeWidth: 99_999, ResizeHeight: -5,
            Brightness: 500, Contrast: 99, Watermark: "  " + new string('x', 80) + "  ");
        ProcessOptions o = wild.Normalize();
        Assert.Equal(16_384, o.ResizeWidth);
        Assert.Equal(0, o.ResizeHeight);
        Assert.Equal(100, o.Brightness);
        Assert.Equal(3.0, o.Contrast);
        Assert.Equal(48, o.Watermark!.Length);
        Assert.Equal(0, new ProcessOptions(Watermark: "   ").Normalize().Watermark?.Length ?? 0);
    }

    [Fact]
    public void BgraSurface_校验尺寸和数组长度()
    {
        var s = new BgraSurface(new byte[4], 1, 1);
        Assert.Equal(4, s.Stride);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BgraSurface(new byte[4], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BgraSurface(new byte[4], -1, 1));
        Assert.Throws<ArgumentException>(() => new BgraSurface(new byte[3], 1, 1));
    }

    [Fact]
    public void 双线性缩放支持任意尺寸且常量图不失真()
    {
        byte[] src = TestData.Solid(W, H, 10, 20, 30);
        byte[] dst = ImageOps.ResizeBilinear(src, W, H, 137, 91);
        Assert.Equal(137 * 91 * 4, dst.Length);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, dst[..4]);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, dst[^4..]);
        Assert.Equal(2 * 2 * 4, ImageOps.ResizeBilinear(src, W, H, 2, 2).Length);
    }

    [Fact]
    public void 双线性缩放拒绝非法参数()
    {
        byte[] src = new byte[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageOps.ResizeBilinear(src, 1, 1, 0, 1));
        Assert.Throws<ArgumentException>(() => ImageOps.ResizeBilinear(new byte[3], 1, 1, 1, 1));
        Assert.Throws<ArgumentNullException>(() => ImageOps.ResizeBilinear(null!, 1, 1, 1, 1));
    }

    [Fact]
    public void 亮度提升按_2_55_倍率换算且不改_alpha()
    {
        byte[] px = TestData.Solid(2, 2, 128, 128, 128, a: 77);
        ImageOps.Adjust(px, 20, 1.0, false, false);
        Assert.Equal(179, px[0]);
        Assert.Equal(179, px[1]);
        Assert.Equal(179, px[2]);
        Assert.Equal(77, px[3]);
    }

    [Fact]
    public void 反色结果是_255_减原值()
    {
        byte[] px = TestData.Solid(2, 2, 128, 128, 128);
        ImageOps.Adjust(px, 0, 1.0, false, true);
        Assert.Equal(127, px[0]);
        Assert.Equal(127, px[1]);
        Assert.Equal(127, px[2]);
    }

    [Fact]
    public void 灰度化使用_BT601_且三通道相等()
    {
        byte[] px = TestData.Solid(2, 2, 10, 40, 200);
        ImageOps.Adjust(px, 0, 1.0, true, false);
        byte luma = ImageOps.Luma(200, 40, 10);
        Assert.Equal(84, luma);
        for (int i = 0; i < px.Length; i += 4)
            Assert.Equal(luma, px[i]);
        Assert.Equal(luma, px[1]);
        Assert.Equal(luma, px[2]);
    }

    [Fact]
    public void 恒等参数是可快速返回的_noop()
    {
        byte[] px = TestData.Solid(2, 2, 1, 2, 3);
        byte[] copy = (byte[])px.Clone();
        ImageOps.Adjust(px, 0, 1.0, false, false);
        Assert.Equal(copy, px);
    }

    [Fact]
    public void 几何水印对相同文本确定_对不同文本不同()
    {
        byte[] a = TestData.Solid(W, H, 100, 110, 120);
        byte[] b = (byte[])a.Clone();
        byte[] c = (byte[])a.Clone();
        ImageOps.DrawWatermark(a, W, H, "alpha");
        ImageOps.DrawWatermark(b, W, H, "alpha");
        ImageOps.DrawWatermark(c, W, H, "beta");
        Assert.Equal(a, b);
        Assert.False(a.SequenceEqual(c), "不同文本应产生不同图案");
        Assert.False(a.SequenceEqual(TestData.Solid(W, H, 100, 110, 120)), "水印必须真的改了像素");
    }

    [Fact]
    public void 三种裸像素布局长度与通道顺序正确()
    {
        byte[] px = TestData.Solid(4, 3, 10, 40, 200);
        byte[] gray = ImageOps.ToGray8(px, 4, 3);
        byte[] bgr = ImageOps.ToBgr24(px, 4, 3);
        byte[] rgb = ImageOps.ToRgb24(px, 4, 3);
        Assert.Equal(4 * 3, gray.Length);
        Assert.Equal(4 * 3 * 3, bgr.Length);
        Assert.Equal(new byte[] { 10, 40, 200 }, bgr[..3]);
        Assert.Equal(new byte[] { 200, 40, 10 }, rgb[..3]);
        Assert.Equal(ImageOps.Luma(200, 40, 10), gray[0]);

        Assert.Equal(px, ImageOps.ToBgra32(bgr, PixelLayout.Bgr24, 4, 3));
        Assert.Equal(px, ImageOps.ToBgra32(rgb, PixelLayout.Rgb24, 4, 3));
        byte[] fromGray = ImageOps.ToBgra32(gray, PixelLayout.Gray8, 4, 3);
        Assert.Equal(gray[0], fromGray[0]);
        Assert.Equal(255, fromGray[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageOps.ToBgra32(gray, (PixelLayout)99, 4, 3));
    }

    [Fact]
    public void 布局元信息一致()
    {
        Assert.Equal(1, PixelLayoutInfo.BytesPerPixel(PixelLayout.Gray8));
        Assert.Equal(3, PixelLayoutInfo.BytesPerPixel(PixelLayout.Bgr24));
        Assert.Equal(3, PixelLayoutInfo.BytesPerPixel(PixelLayout.Rgb24));
        Assert.Equal(4, PixelLayoutInfo.BytesPerPixel(PixelLayout.Bgra32));
        Assert.Equal("application/x-gray8", PixelLayoutInfo.Mime(PixelLayout.Gray8));
        Assert.Equal("application/x-bgr24", PixelLayoutInfo.Mime(PixelLayout.Bgr24));
    }

    [Fact]
    public void 处理管线恒等输入原样返回()
    {
        byte[] px = TestData.Solid(8, 6, 7, 8, 9);
        ProcessResult r = ImageProcessor.Apply(new BgraSurface(px, 8, 6));
        Assert.Empty(r.Applied);
        Assert.Equal(px, r.Pixels);
        Assert.Equal(PixelLayout.Bgra32, r.Layout);
        Assert.Equal(8 * 4, r.Stride);
        Assert.Equal("恒等变换", r.Summary);
    }

    [Fact]
    public void 缩放加灰度水印得到_Gray8_连续布局()
    {
        ProcessResult r = ImageProcessor.Apply(Document, W, H, new ProcessOptions(
            ResizeWidth: 137, ResizeHeight: 91, Grayscale: true,
            Watermark: "proof", Layout: PixelLayout.Gray8));
        Assert.Equal(137, r.Width);
        Assert.Equal(91, r.Height);
        Assert.Equal(137, r.Stride);
        Assert.Equal(137 * 91, r.Pixels.Length);
        Assert.Equal(1, r.BytesPerPixel);
        Assert.Equal(PixelLayout.Gray8, r.Layout);
        Assert.Contains(r.Applied, s => s.Contains("双线性缩放"));
        Assert.Contains(r.Applied, s => s.Contains("灰度化"));
        Assert.Contains(r.Applied, s => s.Contains("几何水印"));
        Assert.Contains(" → ", r.Summary);
        Assert.Equal(137 * 91 * 4, ImageProcessor.ToDisplayBgra(r).Length);
    }

    [Fact]
    public void Otsu_二值化结果与阈值自洽()
    {
        byte[] gray = ImageOps.ToGray8(Document, W, H);
        (byte[] binary, int threshold) = CvInput.Otsu(gray);
        Assert.Equal(gray.Length, binary.Length);
        Assert.InRange(threshold, 12, 249);
        Assert.Equal(gray.Count(v => v <= threshold), binary.Count(v => v == 255));
        Assert.True(binary.Any(v => v == 255) && binary.Any(v => v == 0));
    }

    [Fact]
    public void 行投影在合成文档上检出三行且_BGR_与_Gray8_一致()
    {
        byte[] doc = Document;
        CvAnalysis gray = ImageProcessor.AnalyzeForOcr(
            ImageProcessor.Apply(doc, W, H, new ProcessOptions(Layout: PixelLayout.Gray8)));
        CvAnalysis bgr = ImageProcessor.AnalyzeForOcr(
            ImageProcessor.Apply(doc, W, H, new ProcessOptions(Layout: PixelLayout.Bgr24)));
        Assert.Equal(3, gray.TextRows);
        Assert.Equal(3, bgr.TextRows);
        Assert.Equal(gray.Threshold, bgr.Threshold);
        Assert.Equal(H, gray.RowDensity.Length);
        Assert.True(gray.InkRatio > 0 && gray.InkRatio < 1);
        Assert.Contains("文本行", gray.Summary);
    }

    [Fact]
    public void CV_算子对非法输入抛异常()
    {
        Assert.Throws<ArgumentException>(() => CvInput.Otsu(Array.Empty<byte>()));
        Assert.Throws<ArgumentNullException>(() => CvInput.Otsu(null!));
        Assert.Throws<ArgumentException>(() =>
            CvInput.AnalyzeRows(new byte[2], 4, 4));
    }
}
