using System.Diagnostics;
using Sdcb.SimdGcdrNet;
using Sdcb.SimdGcdrNet.Models;

namespace SimdCodecs.Document;

/// <summary>
/// SimdGcdrNet 1.0.0 封装。GcdrEnhancer 线程安全、内置按尺寸 plan 池与 session 池，
/// 所以进程内只保留一个 Lazy 单例：首次真正调用才付约 50 ms 模型构造 + plan 编译。
/// 模型是程序集嵌入资源，FromBytes 走 ReadOnlySpan 零拷贝，不解压、不落临时文件。
/// </summary>
public sealed class GcdrDocumentEnhancer : IDocumentEnhancer, IDisposable
{
    private readonly Lazy<GcdrEnhancer> _lazy;
    private readonly object _swap = new();
    private readonly bool _auto;
    private GcdrEnhancer? _cpuFallback;
    private volatile bool _needFallback;
    private bool _degraded;

    public GcdrDocumentEnhancer(DocumentOptions options)
    {
        _auto = options.UseAutoBackend;
        _lazy = new Lazy<GcdrEnhancer>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>实际后端；auto 回落后变为 cpu。</summary>
    public string Backend => _degraded ? "cpu" : _auto ? "auto" : "cpu";
    public bool Degraded => _degraded;
    public bool Loaded => _lazy.IsValueCreated;

    private static GcdrEnhancer Build(GcdrBackend backend) =>
        GcdrEnhancer.FromBytes(GcdrModels.GcNet, GcdrModels.DrNet, backend);

    private GcdrEnhancer Build()
    {
        if (!_auto) return Build(GcdrBackend.Cpu);
        try
        {
            return Build(GcdrBackend.Auto);
        }
        catch
        {
            _degraded = true;
            return Build(GcdrBackend.Cpu);
        }
    }

    private GcdrEnhancer Current()
    {
        GcdrEnhancer primary = _lazy.Value;
        if (!_needFallback) return primary;
        lock (_swap)
        {
            return _cpuFallback ??= Build(GcdrBackend.Cpu);
        }
    }

    public EnhanceResult Enhance(byte[] bgra, int width, int height, int stride,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (bgra.LongLength < (long)stride * height)
            throw new ArgumentException("Bgra32 缓冲区长度小于 stride × height。", nameof(bgra));

        byte[] dst = new byte[bgra.Length];
        var sw = DocumentClock.Start();
        try
        {
            Current().Enhance(bgra, width, height, stride, dst, stride, GcdrPixelFormat.Bgra32);
        }
        catch when (_auto && !_needFallback)
        {
            // GPU 后端在首次推理时仍可能失败（无 libvulkan / 设备不兼容）：
            // 明确降级为 CPU 后重试一次，并在响应里标记 degraded。
            _degraded = true;
            _needFallback = true;
            Current().Enhance(bgra, width, height, stride, dst, stride, GcdrPixelFormat.Bgra32);
        }
        sw.Stop();
        return new EnhanceResult(dst, width, height, stride, sw.Ms(), Backend, _degraded);
    }

    public void Dispose()
    {
        if (_lazy.IsValueCreated) _lazy.Value.Dispose();
        _cpuFallback?.Dispose();
    }
}
