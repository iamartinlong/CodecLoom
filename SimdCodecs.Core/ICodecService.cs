using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;

namespace SimdCodecs.Core;

/// <summary>
/// 编解码能力契约。上层只依赖这个接口，不直接依赖 Sdcb.SimdCodecs 的具体实现。
/// 目的：上游库 2.0 发布仅两天、单人维护且已有破坏性变更记录，一旦需要回退到
/// Magick.NET / ImageSharp / 内部实现，只需替换注册的实现，不必改动 UI 与端点层。
/// 方法签名与静态门面 <see cref="CodecService"/> 保持一致，便于渐进迁移。
/// </summary>
public interface ICodecService
{
    IReadOnlyList<ExportTarget> Targets { get; }
    bool HeifEnabled { get; }

    ExportTarget Target(string label);
    ExportTarget? FindTarget(string? label);
    void EnsureHeif();

    ImageEntry Inspect(byte[] data, string displayName = "<memory>");
    ImageEntry Inspect(string path);

    LoadedImage Load(byte[] data, string displayName = "<memory>", int scale = 1,
        bool applyOrientation = true, PixelRect? region = null, int frameIndex = 0,
        CancellationToken ct = default);
    LoadedImage Load(string path, int scale = 1, bool applyOrientation = true,
        PixelRect? region = null, int frameIndex = 0, CancellationToken ct = default);

    (byte[] Pixels, int Width, int Height, int Stride) LoadThumbnail(
        byte[] data, int box = 96, CancellationToken ct = default);
    (byte[] Pixels, int Width, int Height, int Stride) LoadThumbnail(
        string path, int box = 96, CancellationToken ct = default);

    void Write(byte[] pixels, int width, int height, string path, ImageFormat format,
        int quality = 90, bool quantizePng = false, bool losslessWebp = false);
    byte[] EncodeBytes(byte[] pixels, int width, int height, ImageFormat format,
        int quality = 90, bool quantizePng = false, bool losslessWebp = false);

    void Save(LoadedImage image, string path, ExportTarget target, int quality = 90,
        CancellationToken ct = default);
    void Save(LoadedImage image, string path, ImageFormat format,
        int quality = 90, bool quantizePng = false, bool losslessWebp = false);
    byte[] SaveToBytes(LoadedImage image, ExportTarget target, int quality = 90,
        CancellationToken ct = default);
}

/// <summary>
/// 默认实现：把接口调用委托给静态门面 <see cref="CodecService"/>，不改变既有行为。
/// 这是一个适配器，不是第二份实现。
/// </summary>
public sealed class SimdCodecService : ICodecService
{
    public IReadOnlyList<ExportTarget> Targets => CodecService.Targets;
    public bool HeifEnabled => CodecService.HeifEnabled;

    public ExportTarget Target(string label) => CodecService.Target(label);
    public ExportTarget? FindTarget(string? label) => CodecService.FindTarget(label);
    public void EnsureHeif() => CodecService.EnsureHeif();

    public ImageEntry Inspect(byte[] data, string displayName = "<memory>") =>
        CodecService.Inspect(data, displayName);
    public ImageEntry Inspect(string path) => CodecService.Inspect(path);

    public LoadedImage Load(byte[] data, string displayName = "<memory>", int scale = 1,
        bool applyOrientation = true, PixelRect? region = null, int frameIndex = 0,
        CancellationToken ct = default) =>
        CodecService.Load(data, displayName, scale, applyOrientation, region, frameIndex, ct);
    public LoadedImage Load(string path, int scale = 1, bool applyOrientation = true,
        PixelRect? region = null, int frameIndex = 0, CancellationToken ct = default) =>
        CodecService.Load(path, scale, applyOrientation, region, frameIndex, ct);

    public (byte[] Pixels, int Width, int Height, int Stride) LoadThumbnail(
        byte[] data, int box = 96, CancellationToken ct = default) =>
        CodecService.LoadThumbnail(data, box, ct);
    public (byte[] Pixels, int Width, int Height, int Stride) LoadThumbnail(
        string path, int box = 96, CancellationToken ct = default) =>
        CodecService.LoadThumbnail(path, box, ct);

    public void Write(byte[] pixels, int width, int height, string path, ImageFormat format,
        int quality = 90, bool quantizePng = false, bool losslessWebp = false) =>
        CodecService.Write(pixels, width, height, path, format, quality, quantizePng, losslessWebp);
    public byte[] EncodeBytes(byte[] pixels, int width, int height, ImageFormat format,
        int quality = 90, bool quantizePng = false, bool losslessWebp = false) =>
        CodecService.EncodeBytes(pixels, width, height, format, quality, quantizePng, losslessWebp);

    public void Save(LoadedImage image, string path, ExportTarget target, int quality = 90,
        CancellationToken ct = default) => CodecService.Save(image, path, target, quality, ct);
    public void Save(LoadedImage image, string path, ImageFormat format,
        int quality = 90, bool quantizePng = false, bool losslessWebp = false) =>
        CodecService.Save(image, path, format, quality, quantizePng, losslessWebp);
    public byte[] SaveToBytes(LoadedImage image, ExportTarget target, int quality = 90,
        CancellationToken ct = default) => CodecService.SaveToBytes(image, target, quality, ct);
}

/// <summary>
/// 进程级注册点。服务端用 DI 注入 <see cref="ICodecService"/>；
/// 不便于用容器的场景（桌面启动早期、自检、命令行）通过 <see cref="Current"/> 取当前实现。
/// 替换实现示例：CodecServices.Use(new MagickCodecService())。
/// </summary>
public static class CodecServices
{
    public static ICodecService Simd { get; } = new SimdCodecService();

    private static ICodecService _current = Simd;

    public static ICodecService Current => Volatile.Read(ref _current);

    public static void Use(ICodecService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        Volatile.Write(ref _current, service);
    }

    public static void Reset() => Use(Simd);
}
