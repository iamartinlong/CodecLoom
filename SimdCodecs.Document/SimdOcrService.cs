using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;

namespace SimdCodecs.Document;

/// <summary>
/// SimdPaddleOCR 2.0.0（PP-OCRv6）封装：DET 检测 → CLS 方向分类 → REC 识别。
/// 模型来自 ChineseV6Tiny 包的嵌入资源，进程内 Lazy 单例（实测加载约 200 ms）。
/// 注意：returnCtcAlignment 在 2.0.0 是第二个 Run 重载的第 4 个位置参数，
/// 写成命名参数会触发 CS1744 —— 这是上游 API 与文章示例的已知偏差。
/// </summary>
public sealed class SimdOcrService : IOcrService, IDisposable
{
    private readonly Lazy<PaddleOcrAll> _lazy;
    private readonly DocumentOptions _options;
    private readonly object _swap = new();
    private PaddleOcrAll? _cpuFallback;
    private volatile bool _needFallback;
    private bool _degraded;

    public SimdOcrService(DocumentOptions options)
    {
        _options = options;
        _lazy = new Lazy<PaddleOcrAll>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Model => "tiny";
    public string Backend => _degraded ? "cpu" : _options.UseAutoBackend ? "auto" : "cpu";
    public bool Degraded => _degraded;
    public bool Loaded => _lazy.IsValueCreated;

    private PaddleOcrAll Build()
    {
        OcrBackend backend = _options.UseAutoBackend ? OcrBackend.Auto : OcrBackend.Cpu;
        try
        {
            return PaddleOcrAll.Load(ChineseV6TinyModels.Default, Options(backend));
        }
        catch when (backend == OcrBackend.Auto)
        {
            _degraded = true;
            return PaddleOcrAll.Load(ChineseV6TinyModels.Default, Options(OcrBackend.Cpu));
        }
    }

    private PaddleOcrOptions Options(OcrBackend backend) => new()
    {
        Detector = new PaddleOcrDetectorOptions { Backend = backend, MaxImagePixels = _options.MaxPixels },
        Classifier = new PaddleOcrClassifierOptions { Backend = backend, MaxImagePixels = _options.MaxPixels },
        Recognizer = new PaddleOcrRecognizerOptions { Backend = backend, MaxImagePixels = _options.MaxPixels },
        // 上游自带的二次内存保护，与 DocumentLimits 同口径。
        MaxCropPixels = _options.MaxPixels
    };

    private PaddleOcrAll Current()
    {
        PaddleOcrAll primary = _lazy.Value;
        if (!_needFallback) return primary;
        lock (_swap)
        {
            return _cpuFallback ??= PaddleOcrAll.Load(
                ChineseV6TinyModels.Default, Options(OcrBackend.Cpu));
        }
    }

    public OcrOutcome Recognize(byte[] bgra, int width, int height, int stride,
        bool returnCharacterBoxes = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (bgra.LongLength < (long)stride * height)
            throw new ArgumentException("Bgra32 缓冲区长度小于 stride × height。", nameof(bgra));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        PaddleOcrResult result;
        try
        {
            result = Invoke(Current(), bgra, width, height, stride, returnCharacterBoxes);
        }
        catch when (_options.UseAutoBackend && !_needFallback)
        {
            _degraded = true;
            _needFallback = true;
            result = Invoke(Current(), bgra, width, height, stride, returnCharacterBoxes);
        }
        sw.Stop();

        string text = result.Text ?? "";
        bool truncated = text.Length > _options.MaxTextChars;
        if (truncated) text = text[.._options.MaxTextChars];

        OcrLine[] lines = result.Lines.Select(l => new OcrLine(
            l.Text ?? "",
            l.RecognitionScore,
            new[] { l.Box.X1, l.Box.Y1, l.Box.X2, l.Box.Y2,
                    l.Box.X3, l.Box.Y3, l.Box.X4, l.Box.Y4 },
            returnCharacterBoxes
                ? l.EstimateCharacterBoxes().Select(c => new OcrChar(
                    c.Text ?? "", c.Score,
                    new[] { c.X1, c.Y1, c.X2, c.Y2, c.X3, c.Y3, c.X4, c.Y4 })).ToArray()
                : null)).ToArray();

        return new OcrOutcome(
            text,
            result.PackedTextHash,
            result.DetectedCount,
            lines.Length,
            text.Length,
            lines,
            Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            Backend,
            _degraded,
            truncated);
    }

    private static PaddleOcrResult Invoke(PaddleOcrAll ocr, byte[] px, int w, int h,
        int stride, bool ctc) => ctc
        // 第 4 个位置参数才是 returnCtcAlignment
        ? ocr.Run(px, w, h, true, stride, ImagePixelFormat.Bgra32)
        : ocr.Run(px, w, h, stride, ImagePixelFormat.Bgra32);

    public void Dispose()
    {
        if (_lazy.IsValueCreated) _lazy.Value.Dispose();
        _cpuFallback?.Dispose();
    }
}
