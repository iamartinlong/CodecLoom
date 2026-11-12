using System.Diagnostics;

namespace SimdCodecs.Document;

/// <summary>
/// 文档推理的独立并发闸门。与 API 的解码闸门分开：
/// 增强 / OCR 是 CPU 密集、不可被 CancellationToken 打断的长任务，
/// 若共用解码信号量，一个慢请求会同时占住两条关键路径。
/// </summary>
public sealed class DocumentGate(int maxConcurrency)
{
    private readonly SemaphoreSlim _semaphore = new(Math.Max(1, maxConcurrency));
    private long _acquired, _timedOut;

    public long Acquired => Interlocked.Read(ref _acquired);
    public long TimedOut => Interlocked.Read(ref _timedOut);
    public int MaxConcurrency { get; } = Math.Max(1, maxConcurrency);
    public int Available => _semaphore.CurrentCount;

    /// <summary>排队拿令牌；超时抛 DocumentBusyException（HTTP 503）。</summary>
    public async ValueTask<Lease> AcquireAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        bool ok;
        try
        {
            ok = await _semaphore.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        if (!ok)
        {
            Interlocked.Increment(ref _timedOut);
            throw new DocumentBusyException(
                $"文档推理队列已满（并发 {MaxConcurrency}），等待 {timeout.TotalSeconds:F0}s 超时；请稍后重试。");
        }
        Interlocked.Increment(ref _acquired);
        return new Lease(this);
    }

    public readonly struct Lease(DocumentGate owner) : IDisposable
    {
        public void Dispose() => owner._semaphore.Release();
    }

    /// <summary>把一段同步 CPU 计算包进「排队 + 计时」，供 API / 桌面 / CLI 共用。</summary>
    public async Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken ct = default)
    {
        using Lease lease = await AcquireAsync(timeout, ct).ConfigureAwait(false);
        return await Task.Run(work, ct).ConfigureAwait(false);
    }
}

/// <summary>轻量计时器，统一各层的 elapsedMs 口径。</summary>
internal static class DocumentClock
{
    public static Stopwatch Start() => Stopwatch.StartNew();
    public static double Ms(this Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 2);
}
