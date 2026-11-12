using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Core;
using Xunit;

namespace SimdCodecs.Tests;

/// <summary>上传闸门：所有拒绝都必须发生在解码之前，且状态码符合 HTTP 语义。</summary>
public class ImageGuardTests
{
    private const int W = 320, H = 240;
    private static readonly byte[] Png =
        CodecService.EncodeBytes(TestData.Gradient(W, H), W, H, ImageFormat.Png);

    [Fact]
    public void 空内容返回_400()
    {
        GuardVerdict v = ImageGuard.Validate(Array.Empty<byte>(), "empty.png");
        Assert.False(v.Accepted);
        Assert.Equal(400, v.StatusCode);
        Assert.Null(v.Entry);
    }

    [Fact]
    public void 超出字节上限返回_413_且原因含上限值()
    {
        var limits = new ImageLimits(MaxBytes: 16, MaxPixels: 80_000_000, MaxFrames: 200);
        GuardVerdict v = ImageGuard.Validate(Png, "big.png", limits);
        Assert.False(v.Accepted);
        Assert.Equal(413, v.StatusCode);
        Assert.Contains("MB", v.Reason!);
    }

    [Fact]
    public void 损坏文件返回_415_或_422()
    {
        byte[] broken = System.Text.Encoding.ASCII.GetBytes("not an image at all");
        GuardVerdict v = ImageGuard.Validate(broken, "broken.bin");
        Assert.False(v.Accepted);
        Assert.InRange(v.StatusCode, 415, 422);
        Assert.NotNull(v.Reason);
    }

    [Fact]
    public void CRC_正确的万倍像素图片炸弹被_413_拦下()
    {
        byte[] bomb = TestData.PngBomb(40_000, 40_000);
        Assert.True(bomb.Length < 100, "炸弹文件本身必须很小，才能证明校验只读头部");
        GuardVerdict v = ImageGuard.Validate(bomb, "bomb.png");
        Assert.False(v.Accepted);
        Assert.Equal(413, v.StatusCode);
        Assert.Contains("图片炸弹", v.Reason!);
        Assert.NotNull(v.Entry);
        Assert.Equal(40_000, v.Entry!.Width);
    }

    [Fact]
    public void 帧数超限时返回_413()
    {
        byte[] gif = TestData.AnimatedGif(3);
        var limits = new ImageLimits(MaxBytes: 32L * 1024 * 1024,
            MaxPixels: 80_000_000, MaxFrames: 1);
        GuardVerdict v = ImageGuard.Validate(gif, "anim.gif", limits);
        Assert.False(v.Accepted);
        Assert.Equal(413, v.StatusCode);
        Assert.Contains("帧", v.Reason!);
    }

    [Fact]
    public void 自定义像素上限生效()
    {
        var limits = new ImageLimits(MaxPixels: 1_000);
        GuardVerdict v = ImageGuard.Validate(Png, "g.png", limits);
        Assert.False(v.Accepted);
        Assert.Equal(413, v.StatusCode);
    }

    [Fact]
    public void 正常图片通过且条目信息完整()
    {
        GuardVerdict v = ImageGuard.Validate(Png, "gradient.png");
        Assert.True(v.Accepted, v.Reason);
        Assert.Equal(200, v.StatusCode);
        Assert.Null(v.Reason);
        Assert.NotNull(v.Entry);
        Assert.Equal(ImageFormat.Png, v.Entry!.Format);
        Assert.Equal(W, v.Entry.Width);
        Assert.Equal(H, v.Entry.Height);
        Assert.Equal(Png.LongLength, v.Entry.Bytes);
        Assert.True(v.Entry.Decodable);
    }

    [Fact]
    public void 判定工厂方法行为正确()
    {
        var entry = new ImageEntry("x", 1, ImageFormat.Png, 1, 1, 1, true);
        GuardVerdict ok = GuardVerdict.Ok(entry);
        Assert.True(ok.Accepted);
        Assert.Same(entry, ok.Entry);
        GuardVerdict reject = GuardVerdict.Reject(415, "bad");
        Assert.False(reject.Accepted);
        Assert.Equal(415, reject.StatusCode);
        Assert.Equal("bad", reject.Reason);
    }
}
