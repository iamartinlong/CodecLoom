namespace SimdCodecs.Document;

/// <summary>文字识别契约。输入同样是连续 Bgra32，不依赖任何图像库。</summary>
public interface IOcrService
{
    OcrOutcome Recognize(byte[] bgra, int width, int height, int stride,
        bool returnCharacterBoxes = false, CancellationToken ct = default);
}

/// <summary>四边形坐标 [X1,Y1,X2,Y2,X3,Y3,X4,Y4]，与上游检测框一致。</summary>
public sealed record OcrLine(
    string Text,
    float Score,
    float[] Box,
    OcrChar[]? Characters);

/// <summary>CTC 估算的逐字符框（适合打码，不适合精确排版还原）。</summary>
public sealed record OcrChar(string Text, float Score, float[] Box);

/// <param name="PackedTextHash">上游提供的文本哈希，可用于同图两次结果一致性断言。</param>
/// <param name="Truncated">文本是否因 Document:MaxTextChars 被截断。</param>
public sealed record OcrOutcome(
    string Text,
    ulong PackedTextHash,
    int DetectedCount,
    int LineCount,
    int CharCount,
    OcrLine[] Lines,
    double ElapsedMs,
    string Backend,
    bool Degraded,
    bool Truncated);

/// <summary>给 JSON / 状态栏用的稳定摘要（不含文本正文，可安全写日志）。</summary>
public static class OcrSummary
{
    public static object Stats(OcrOutcome o) => new
    {
        lines = o.LineCount,
        chars = o.CharCount,
        detected = o.DetectedCount,
        textHash = o.PackedTextHash.ToString("x16"),
        elapsedMs = o.ElapsedMs,
        backend = o.Backend,
        degraded = o.Degraded,
        truncated = o.Truncated
    };
}
