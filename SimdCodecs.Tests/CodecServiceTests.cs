using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Core;
using Xunit;

namespace SimdCodecs.Tests;

/// <summary>Core 端到端：编码 → 头部预检 → 解码 → 缩放/裁剪/多帧 → 再编码回读。</summary>
public class CodecServiceTests
{
    private const int W = 320, H = 240;

    private static readonly byte[] Gradient = TestData.Gradient(W, H);
    private static readonly byte[] Png = CodecService.EncodeBytes(Gradient, W, H, ImageFormat.Png);

    [Fact]
    public void Heif_注册成功且_AVIF_只识别不解码()
    {
        CodecService.EnsureHeif();
        Assert.True(CodecService.HeifEnabled);
        Assert.True(ImageCodecs.IsDecodable(ImageFormat.Heif));
        Assert.False(ImageCodecs.IsDecodable(ImageFormat.Avif));
        Assert.True(CodecServices.Current.HeifEnabled);
    }

    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Webp)]
    public void 三种格式编码后都能预检并解码回_Bgra32(ImageFormat format)
    {
        byte[] bytes = CodecService.EncodeBytes(Gradient, W, H, format);
        Assert.True(bytes.Length > 0);

        ImageEntry entry = CodecService.Inspect(bytes, $"gradient.{format}");
        Assert.Equal(format, entry.Format);
        Assert.True(entry.Decodable, entry.Error);
        Assert.Equal(W, entry.Width);
        Assert.Equal(H, entry.Height);
        Assert.Equal(bytes.LongLength, entry.Bytes);

        LoadedImage image = CodecService.Load(bytes, $"gradient.{format}");
        Assert.Equal(W, image.Width);
        Assert.Equal(H, image.Height);
        Assert.Equal(W * 4, image.Stride);
        Assert.Equal(W * H * 4, image.Pixels.Length);
        Assert.True(image.Pixels.Any(v => v != 0), "解码结果不应全黑");
        Assert.True(image.DecodeMs >= 0);
        Assert.Equal(1, image.Scale);
    }

    [Fact]
    public void 编码字节带正确文件头()
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, Png[..4]);
        byte[] jpeg = CodecService.EncodeBytes(Gradient, W, H, ImageFormat.Jpeg);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        byte[] webp = CodecService.EncodeBytes(Gradient, W, H, ImageFormat.Webp);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(webp, 8, 4));
    }

    [Fact]
    public void 解码期四分之一降采样生效()
    {
        LoadedImage image = CodecService.Load(Png, "g.png", scale: 4);
        Assert.Equal(W / 4, image.Width);
        Assert.Equal(H / 4, image.Height);
        Assert.Equal(4, image.Scale);
        Assert.Equal(image.Width * image.Height * 4, image.Pixels.Length);
    }

    [Fact]
    public void 区域裁剪按显示坐标生效()
    {
        var region = new PixelRect(10, 10, 80, 60);
        LoadedImage image = CodecService.Load(Png, "g.png", region: region);
        Assert.Equal(80, image.Width);
        Assert.Equal(60, image.Height);
        Assert.NotNull(image.Region);
        Assert.Equal(region, image.Region!.Value);
        Assert.Equal(80 * 60 * 4, image.Pixels.Length);
    }

    [Theory]
    [InlineData("JPEG")]
    [InlineData("PNG")]
    [InlineData("WebP")]
    public void SaveToBytes_输出可再次解码(string label)
    {
        LoadedImage source = CodecService.Load(Png, "g.png");
        ExportTarget target = CodecService.Target(label);
        byte[] output = CodecService.SaveToBytes(source, target, 88);
        Assert.True(output.Length > 0);
        LoadedImage back = CodecService.Load(output, "out" + target.Extension);
        Assert.Equal(W, back.Width);
        Assert.Equal(H, back.Height);
        Assert.Equal(target.Format, back.Info.Format);
    }

    [Fact]
    public void 照片类量化_PNG_写出调色板且更小()
    {
        const int pw = 256, ph = 256;
        byte[] photo = TestData.Photo(pw, ph);
        byte[] lossless = CodecService.EncodeBytes(photo, pw, ph, ImageFormat.Png);
        LoadedImage image = CodecService.Load(lossless, "photo.png");
        var quantized = new ExportTarget("PNG 量化", ImageFormat.Png, ".png", QuantizePng: true);
        byte[] output = CodecService.SaveToBytes(image, quantized);
        Assert.Contains("PLTE", System.Text.Encoding.ASCII.GetString(output));
        Assert.True(output.Length < lossless.Length,
            $"量化 {output.Length} B 应小于无损 {lossless.Length} B");
        Assert.Equal(pw, CodecService.Load(output, "q.png").Width);
    }

    [Fact]
    public void 缩略图落在_96_方框内且布局紧凑()
    {
        (byte[] pixels, int w, int h, int stride) = CodecService.LoadThumbnail(Png, 96);
        Assert.InRange(w, 1, 96);
        Assert.InRange(h, 1, 96);
        Assert.Equal(w * 4, stride);
        Assert.Equal(stride * h, pixels.Length);
        Assert.True(w <= h * 2 && h <= w * 2, "宽高比不应被明显扭曲");
    }

    [Fact]
    public void 损坏文件预检不抛异常而是返回错误()
    {
        byte[] broken = System.Text.Encoding.ASCII.GetBytes("this is definitely not an image");
        ImageEntry entry = CodecService.Inspect(broken, "broken.bin");
        Assert.False(entry.Decodable);
        Assert.NotNull(entry.Error);
        Assert.Contains("无法识别", entry.Summary);
        Assert.Throws<InvalidDataException>(() => CodecService.Load(broken, "broken.bin"));
    }

    [Fact]
    public void 多帧_GIF_可逐帧解码()
    {
        byte[] gif = TestData.AnimatedGif(3);
        ImageEntry entry = CodecService.Inspect(gif, "anim.gif");
        Assert.Equal(ImageFormat.Gif, entry.Format);
        Assert.Equal(3, entry.FrameCount);

        LoadedImage last = CodecService.Load(gif, "anim.gif", frameIndex: 2);
        Assert.Equal(2, last.FrameIndex);
        Assert.Equal(3, last.FrameCount);
        Assert.NotNull(last.FrameDuration);
        Assert.True(last.FrameDuration > TimeSpan.Zero);
    }

    [Fact]
    public void Pixels_Downsample_按最近邻取点()
    {
        byte[] src = TestData.Solid(4, 4, 10, 20, 30);
        src[0] = 99;                       // 目标 (0,0) 采样源 (0,0)
        src[(2 * 4 + 2) * 4] = 77;        // 目标 (1,1) 采样源 (2,2)
        byte[] dst = Pixels.Downsample(src, 4, 4, 2, 2);
        Assert.Equal(2 * 2 * 4, dst.Length);
        Assert.Equal(99, dst[0]);
        Assert.Equal(77, dst[(1 * 2 + 1) * 4]);
        Assert.Equal(255, dst[3]);         // alpha 一并复制
    }

    [Fact]
    public void Target_与_FindTarget_支持标签和扩展名()
    {
        Assert.Equal(ImageFormat.Jpeg, CodecService.Target("JPEG").Format);
        Assert.Equal(ImageFormat.Jpeg, CodecService.Target("不存在时回退").Format);
        Assert.Equal(ImageFormat.Webp, CodecService.FindTarget("webp")?.Format);
        Assert.Equal(ImageFormat.Png, CodecService.FindTarget("PNG")?.Format);
        Assert.Null(CodecService.FindTarget("avif"));
        Assert.Equal(5, CodecService.Targets.Count);
    }

    [Fact]
    public void CodecServices_注册点拒绝空实现并可复位()
    {
        Assert.Throws<ArgumentNullException>(() => CodecServices.Use(null!));
        CodecServices.Reset();
        Assert.Same(CodecServices.Simd, CodecServices.Current);
    }

    [Fact]
    public void 真实_HEIC_样本端到端()
    {
        string? path = TestData.Find("example.heic");
        if (path is null)
        {
            Console.WriteLine("  [SKIP] testdata/example.heic 不存在（脱离源码树时属正常）");
            return;
        }
        byte[] data = File.ReadAllBytes(path);
        ImageEntry entry = CodecService.Inspect(data, "example.heic");
        Assert.Equal(ImageFormat.Heif, entry.Format);
        Assert.True(entry.Decodable, entry.Error);
        Assert.Equal(1280, entry.Width);

        LoadedImage image = CodecService.Load(data, "example.heic");
        Assert.Equal(entry.Width, image.Width);
        Assert.Equal(entry.Height, image.Height);
        Assert.Equal(image.Width * 4, image.Stride);

        foreach (ExportTarget target in new[] { CodecService.Target("JPEG"), CodecService.Target("PNG") })
        {
            byte[] output = CodecService.SaveToBytes(image, target);
            LoadedImage back = CodecService.Load(output, "out" + target.Extension);
            Assert.Equal(image.Width, back.Width);
            Assert.Equal(image.Height, back.Height);
        }
    }

    [Fact]
    public void 真实三页_TIFF_逐页解码()
    {
        string? path = TestData.Find("multipage-pillow.tiff");
        if (path is null)
        {
            Console.WriteLine("  [SKIP] testdata/multipage-pillow.tiff 不存在");
            return;
        }
        byte[] data = File.ReadAllBytes(path);
        ImageEntry entry = CodecService.Inspect(data, "scan.tiff");
        Assert.Equal(ImageFormat.Tiff, entry.Format);
        Assert.Equal(3, entry.FrameCount);
        for (int i = 0; i < 3; i++)
        {
            LoadedImage page = CodecService.Load(data, "scan.tiff", frameIndex: i);
            Assert.True(page.Width > 0 && page.Height > 0);
            Assert.Equal(page.Width * 4, page.Stride);
            Assert.Equal(3, page.FrameCount);
        }
    }
}
