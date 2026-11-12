namespace SimdCodecs.Document;

/// <summary>
/// 文档照片增强契约。像素约定：连续 Bgra32（stride = width*4），
/// 与 Core.CodecService.Load 的输出、OCR 的 ImagePixelFormat.Bgra32 完全一致，
/// 因此 decode → enhance → ocr 全程零格式转换。
/// </summary>
public interface IDocumentEnhancer
{
    EnhanceResult Enhance(byte[] bgra, int width, int height, int stride,
        CancellationToken ct = default);
}

/// <param name="Pixels">增强后的 Bgra32；alpha 逐字节保持。</param>
/// <param name="Backend">实际使用的后端（auto 降级后为 cpu）。</param>
/// <param name="Degraded">是否发生了 GPU → CPU 回落。</param>
public sealed record EnhanceResult(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    double ElapsedMs,
    string Backend,
    bool Degraded);
