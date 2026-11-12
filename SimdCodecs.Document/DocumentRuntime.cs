using System.Diagnostics;

namespace SimdCodecs.Document;

/// <summary>
/// 模块统一入口：配置 / 闸门 / 两个 Lazy 单例 / 管线 / 统计。
/// API 用 DI 注册为单例，桌面与 CLI 直接 new（不依赖 ASP.NET Core）。
/// 刻意不引用 Core：Core 保持「文件字节 ↔ 像素」单一职责，调用方把 LoadedImage.Pixels 喂进来即可。
/// </summary>
public sealed class DocumentRuntime : IDisposable
{
    private readonly GcdrDocumentEnhancer _enhancer;
    private readonly SimdOcrService _ocr;
    private long _requests, _failures, _enhanceCalls, _ocrCalls;
    private double _totalMs;
    private readonly object _statsGate = new();

    public DocumentRuntime(DocumentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Normalize();
        Options = options;
        Limits = DocumentLimits.From(options);
        Gate = new DocumentGate(options.MaxConcurrency);
        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        _enhancer = new GcdrDocumentEnhancer(options);
        _ocr = new SimdOcrService(options);
        Pipeline = new DocumentPipeline(_enhancer, _ocr);
    }

    public DocumentOptions Options { get; }
    public DocumentLimits Limits { get; }
    public DocumentGate Gate { get; }
    public DocumentPipeline Pipeline { get; }
    public TimeSpan Timeout { get; }
    public IDocumentEnhancer Enhancer => _enhancer;
    public IOcrService Ocr => _ocr;

    public long Requests => Interlocked.Read(ref _requests);
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>
    /// 闸门 → 排队 → 同步推理 → 统计。CPU 内核不响应取消，因此取消只在排队与实际调用前检查；
    /// 超时的语义由调用方用 Task.WaitAsync(Timeout) 实现（提前返回 HTTP，不中断内核）。
    /// </summary>
    public async Task<DocumentResult> RunAsync(DocumentFrame frame, DocumentRequest request,
        CancellationToken ct = default)
    {
        DocumentLimits.Verdict verdict = Limits.Validate(frame.Width, frame.Height, frame.FrameCount);
        if (!verdict.Accepted)
            throw new DocumentRejectedException(verdict.Status, verdict.Reason!);

        Interlocked.Increment(ref _requests);
        var sw = DocumentClock.Start();
        try
        {
            DocumentResult result = await Gate
                .RunAsync(() => Pipeline.Run(frame, request, ct), Timeout, ct)
                .ConfigureAwait(false);
            if (result.Enhance is not null) Interlocked.Increment(ref _enhanceCalls);
            if (result.Ocr is not null) Interlocked.Increment(ref _ocrCalls);
            if (result.OriginalOcr is not null) Interlocked.Increment(ref _ocrCalls);
            return result;
        }
        catch (DocumentRejectedException) { throw; }
        catch (DocumentBusyException) { Interlocked.Increment(ref _failures); throw; }
        catch (OperationCanceledException) { throw; }
        catch
        {
            Interlocked.Increment(ref _failures);
            throw;
        }
        finally
        {
            sw.Stop();
            lock (_statsGate) _totalMs += sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>供 /api/health、/api/ready 与桌面状态栏读取；不含任何正文内容。</summary>
    public object Snapshot()
    {
        lock (_statsGate)
        {
            return new
            {
                enabled = Options.Enabled,
                model = _ocr.Model,
                backend = Options.Backend,
                maxPixels = Limits.MaxPixels,
                maxEdge = Limits.MaxEdge,
                maxConcurrency = Gate.MaxConcurrency,
                timeoutSeconds = Options.TimeoutSeconds,
                requests = Interlocked.Read(ref _requests),
                failures = Interlocked.Read(ref _failures),
                enhanceCalls = Interlocked.Read(ref _enhanceCalls),
                ocrCalls = Interlocked.Read(ref _ocrCalls),
                totalMs = Math.Round(_totalMs, 2),
                enhancerLoaded = _enhancer.Loaded,
                ocrLoaded = _ocr.Loaded,
                degraded = _enhancer.Degraded || _ocr.Degraded,
                gateAvailable = Gate.Available,
                gateTimedOut = Gate.TimedOut,
                warnWhenUnauthenticated = Options.WarnWhenUnauthenticated
            };
        }
    }

    public void Dispose()
    {
        _enhancer.Dispose();
        _ocr.Dispose();
    }
}
