using System.Diagnostics;

namespace SimdCodecs.Document;

/// <summary>一次文档请求的输入帧：连续 Bgra32 + 源帧数（闸门要求单帧）。</summary>
public sealed record DocumentFrame(
    byte[] Pixels, int Width, int Height, int Stride, int FrameCount = 1);

/// <summary>
/// 动作组合：enhance（去阴影 / 透印）、ocr（文字识别）、compare（增强前后 OCR 对比）、
/// ctc（逐字符估算框）。compare=true 会多跑一次原图 OCR，用于展示增强的实际收益。
/// </summary>
public sealed record DocumentRequest(
    bool Enhance = true,
    bool Ocr = true,
    bool Compare = false,
    bool Ctc = false);

public sealed record DocumentResult(
    int Width,
    int Height,
    int Stride,
    byte[]? EnhancedPixels,
    EnhanceResult? Enhance,
    OcrOutcome? Ocr,
    OcrOutcome? OriginalOcr,
    double TotalMs)
{
    /// <summary>不含正文的统计摘要，供响应头 / 日志 / 指标使用。</summary>
    public object Stats() => new
    {
        width = Width,
        height = Height,
        elapsedMs = TotalMs,
        enhanced = Enhance is not null,
        enhanceMs = Enhance?.ElapsedMs ?? 0,
        backend = Enhance?.Backend ?? Ocr?.Backend ?? "cpu",
        degraded = Enhance?.Degraded == true || Ocr?.Degraded == true,
        ocr = Ocr is null ? null : OcrSummary.Stats(Ocr),
        original = OriginalOcr is null ? null : OcrSummary.Stats(OriginalOcr)
    };
}

/// <summary>
/// 把「解码结果 → 可选增强 → OCR」串成一条零格式转换的管线。
/// 本类不做闸门与并发控制（由 DocumentRuntime 统一负责），便于单测直接注入假实现。
/// </summary>
public sealed class DocumentPipeline(IDocumentEnhancer enhancer, IOcrService ocr)
{
    public DocumentResult Run(DocumentFrame frame, DocumentRequest request,
        CancellationToken ct = default)
    {
        var sw = DocumentClock.Start();
        OcrOutcome? original = null;
        EnhanceResult? enhanced = null;
        OcrOutcome? recognized = null;
        byte[] pixels = frame.Pixels;

        if (request.Compare && request.Ocr)
        {
            ct.ThrowIfCancellationRequested();
            original = ocr.Recognize(pixels, frame.Width, frame.Height, frame.Stride,
                request.Ctc, ct);
        }

        if (request.Enhance)
        {
            enhanced = enhancer.Enhance(pixels, frame.Width, frame.Height, frame.Stride, ct);
            pixels = enhanced.Pixels;
        }

        if (request.Ocr)
        {
            ct.ThrowIfCancellationRequested();
            recognized = ocr.Recognize(pixels, frame.Width, frame.Height, frame.Stride,
                request.Ctc, ct);
        }

        sw.Stop();
        return new DocumentResult(frame.Width, frame.Height, frame.Stride,
            enhanced?.Pixels, enhanced, recognized, original, sw.Ms());
    }
}
