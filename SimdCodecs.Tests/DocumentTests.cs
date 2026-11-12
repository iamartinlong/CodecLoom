using System.Text.Json;
using SimdCodecs.Document;
using Xunit;

namespace SimdCodecs.Tests;

/// <summary>文档闸门与并发：纯逻辑，不加载模型，任何平台都能跑。</summary>
public class DocumentLimitsTests
{
    [Fact]
    public void 默认闸门放行小图并拒绝大图与多帧()
    {
        var limits = new DocumentLimits();
        Assert.True(limits.Validate(320, 240, 1).Accepted);
        Assert.True(limits.Validate(2000, 2000, 1).Accepted); // 恰好 4 MP
        Assert.Equal(413, limits.Validate(2001, 2000, 1).Status);
        Assert.Equal(413, limits.Validate(2401, 100, 1).Status);
        Assert.Equal(422, limits.Validate(31, 240, 1).Status);
        Assert.Equal(422, limits.Validate(320, 240, 2).Status);
    }

    [Fact]
    public void 超限原因明确说明不静默缩放()
    {
        DocumentLimits.Verdict v = new DocumentLimits().Validate(3000, 2000, 1);
        Assert.False(v.Accepted);
        Assert.Equal(413, v.Status);
        Assert.Contains("不自动缩放", v.Reason);
        Assert.Contains("OCR 坐标", v.Reason);
    }

    [Fact]
    public void 配置归一收敛越界值并拒绝未分发模型档位()
    {
        var o = new DocumentOptions
        {
            MaxPixels = 999_999_999, MaxEdge = 1, TimeoutSeconds = 0,
            MaxConcurrency = 99, MaxTextChars = 1, Backend = "vulkan"
        };
        o.Normalize();
        Assert.Equal(8_000_000, o.MaxPixels);
        Assert.Equal(64, o.MaxEdge);
        Assert.Equal(1, o.TimeoutSeconds);
        Assert.Equal(4, o.MaxConcurrency);
        Assert.Equal(1024, o.MaxTextChars);
        Assert.Equal("cpu", o.Backend);
        Assert.Throws<InvalidOperationException>(() =>
            new DocumentOptions { Model = "medium" }.Normalize());
    }

    [Fact]
    public void 从配置生成的闸门与配置一致()
    {
        var o = new DocumentOptions { MaxPixels = 1_000_000, MaxEdge = 1200 };
        o.Normalize();
        DocumentLimits l = DocumentLimits.From(o);
        Assert.Equal(1_000_000, l.MaxPixels);
        Assert.Equal(1200, l.MaxEdge);
        Assert.Equal(1, l.MaxFrames);
    }
}

public class DocumentGateTests
{
    [Fact]
    public async Task 并发闸门按配置串行且超时返回忙()
    {
        var gate = new DocumentGate(1);
        using DocumentGate.Lease lease = await gate.AcquireAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, gate.Available);
        await Assert.ThrowsAsync<DocumentBusyException>(async () =>
            await gate.AcquireAsync(TimeSpan.FromMilliseconds(20)));
        Assert.Equal(1, gate.TimedOut);
    }

    [Fact]
    public async Task 执行完成后归还令牌()
    {
        var gate = new DocumentGate(2);
        int value = await gate.RunAsync(() => 42, TimeSpan.FromSeconds(5));
        Assert.Equal(42, value);
        Assert.Equal(2, gate.Available);
        Assert.Equal(1, gate.Acquired);
    }
}

/// <summary>
/// 真实模型小图冒烟。只断言结构不变量（尺寸 / alpha / 确定性 / 哈希 / 坐标形状），
/// 不对识别文字做金标准断言 —— CPU / GPU fp16 与上游模型版本都可能让逐字结果抖动。
/// </summary>
public sealed class DocumentRuntimeFixture : IDisposable
{
    public DocumentRuntime Runtime { get; } = new(DocumentOptions.ForLocal());
    public byte[] Pixels { get; } = TestData.Document(320, 240);
    public DocumentFrame Frame => new(Pixels, 320, 240, 320 * 4, 1);

    public void Dispose() => Runtime.Dispose();
}

public class DocumentModuleTests(DocumentRuntimeFixture fixture) : IClassFixture<DocumentRuntimeFixture>
{
    private DocumentRuntime Runtime => fixture.Runtime;

    [Fact]
    public async Task 增强保持尺寸与_alpha_且确实改变像素()
    {
        DocumentResult r = await Runtime.RunAsync(fixture.Frame,
            new DocumentRequest(Enhance: true, Ocr: false));
        Assert.NotNull(r.Enhance);
        Assert.NotNull(r.EnhancedPixels);
        Assert.Equal(fixture.Pixels.Length, r.EnhancedPixels.Length);
        Assert.Equal(320 * 4, r.Stride);
        Assert.Equal("cpu", r.Enhance.Backend);
        Assert.False(r.Enhance.Degraded);
        for (int i = 3; i < r.EnhancedPixels.Length; i += 4)
            Assert.Equal((byte)255, r.EnhancedPixels[i]);
        Assert.NotEqual(fixture.Pixels, r.EnhancedPixels);
    }

    [Fact]
    public async Task 相同输入连续两次增强结果一致()
    {
        var req = new DocumentRequest(Enhance: true, Ocr: false);
        DocumentResult a = await Runtime.RunAsync(fixture.Frame, req);
        DocumentResult b = await Runtime.RunAsync(fixture.Frame, req);
        Assert.Equal(a.EnhancedPixels, b.EnhancedPixels);
    }

    [Fact]
    public async Task OCR_返回结构化结果与稳定文本哈希()
    {
        var req = new DocumentRequest(Enhance: false, Ocr: true, Ctc: true);
        DocumentResult a = await Runtime.RunAsync(fixture.Frame, req);
        DocumentResult b = await Runtime.RunAsync(fixture.Frame, req);
        Assert.NotNull(a.Ocr);
        Assert.Equal(a.Ocr.PackedTextHash, b.Ocr!.PackedTextHash);
        Assert.Equal(a.Ocr.Lines.Length, a.Ocr.LineCount);
        Assert.True(a.Ocr.ElapsedMs >= 0);
        Assert.All(a.Ocr.Lines, l =>
        {
            Assert.Equal(8, l.Box.Length);
            Assert.NotNull(l.Characters);
            Assert.All(l.Characters!, c => Assert.Equal(8, c.Box.Length));
        });
        Assert.False(a.Ocr.Truncated);
    }

    [Fact]
    public async Task 一步式管线给出增强图与原图对比()
    {
        DocumentResult r = await Runtime.RunAsync(fixture.Frame,
            new DocumentRequest(Enhance: true, Ocr: true, Compare: true));
        Assert.NotNull(r.EnhancedPixels);
        Assert.NotNull(r.Ocr);
        Assert.NotNull(r.OriginalOcr);
        Assert.True(r.TotalMs > 0);
        // Stats() 是日志/响应头用的脱敏摘要，绝不能包含正文。
        string json = JsonSerializer.Serialize(r.Stats());
        Assert.DoesNotContain("\"text\"", json);
        Assert.Contains("lines", json);
    }

    [Fact]
    public async Task Runtime_拒绝超限与多帧并统计()
    {
        long before = Runtime.Requests;
        var ex413 = await Assert.ThrowsAsync<DocumentRejectedException>(() =>
            Runtime.RunAsync(new DocumentFrame(fixture.Pixels, 3000, 2000, 3000 * 4, 1),
                new DocumentRequest(Enhance: false, Ocr: true)));
        Assert.Equal(413, ex413.Status);
        var ex422 = await Assert.ThrowsAsync<DocumentRejectedException>(() =>
            Runtime.RunAsync(new DocumentFrame(fixture.Pixels, 320, 240, 320 * 4, 3),
                new DocumentRequest(Enhance: false, Ocr: true)));
        Assert.Equal(422, ex422.Status);
        // 被闸门拒绝的请求在进入统计前就被拦下，不污染成功计数。
        Assert.Equal(before, Runtime.Requests);
    }

    [Fact]
    public void Snapshot_只含统计不含正文()
    {
        string json = JsonSerializer.Serialize(Runtime.Snapshot());
        Assert.Contains("model", json);
        Assert.Contains("maxPixels", json);
        Assert.DoesNotContain("\"text\"", json);
        Assert.DoesNotContain("fileName", json);
    }
}
