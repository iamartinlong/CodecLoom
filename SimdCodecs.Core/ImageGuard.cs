namespace SimdCodecs.Core;

/// <summary>服务端上传闸门：进解码器之前先卡字节数、像素数与格式。</summary>
public sealed record ImageLimits(
    long MaxBytes = 32L * 1024 * 1024,
    long MaxPixels = 80_000_000,
    int MaxFrames = 200)
{
    public static ImageLimits Default { get; } = new();
}

/// <summary>校验结论。StatusCode 用 HTTP 语义：413 太大、415 不认识、422 头部损坏。</summary>
public sealed record GuardVerdict(bool Accepted, int StatusCode, string? Reason, ImageEntry? Entry)
{
    public static GuardVerdict Ok(ImageEntry entry) => new(true, 200, null, entry);
    public static GuardVerdict Reject(int status, string reason, ImageEntry? entry = null) =>
        new(false, status, reason, entry);
}

public static class ImageGuard
{
    /// <summary>
    /// 只读文件头做校验，不解码整张图。图片炸弹（声明几万×几万像素但文件很小）在这里就被挡住，
    /// 避免后面按 Width*Height*4 分配托管数组。
    /// </summary>
    public static GuardVerdict Validate(byte[] data, string displayName, ImageLimits? limits = null)
    {
        limits ??= ImageLimits.Default;
        if (data.LongLength == 0)
            return GuardVerdict.Reject(400, "上传内容为空。");

        if (data.LongLength > limits.MaxBytes)
            return GuardVerdict.Reject(413,
                $"文件 {data.LongLength / 1024.0 / 1024:F1} MB 超过上限 {limits.MaxBytes / 1024.0 / 1024:F0} MB。");

        ImageEntry entry = CodecServices.Current.Inspect(data, displayName);
        if (entry.Error is not null)
            return GuardVerdict.Reject(
                entry.Format == ImageFormat.Unknown ? 415 : 422, entry.Error, entry);

        long pixels = (long)entry.Width * entry.Height;
        if (pixels <= 0)
            return GuardVerdict.Reject(422, "文件头里的尺寸非法。", entry);

        if (pixels > limits.MaxPixels)
            return GuardVerdict.Reject(413,
                $"声明尺寸 {entry.Width}×{entry.Height}（{pixels / 1_000_000.0:F1} MP）超过像素上限 " +
                $"{limits.MaxPixels / 1_000_000.0:F0} MP，疑似图片炸弹。", entry);

        if (entry.FrameCount > limits.MaxFrames)
            return GuardVerdict.Reject(413,
                $"共 {entry.FrameCount} 帧，超过 {limits.MaxFrames} 帧上限。", entry);

        return GuardVerdict.Ok(entry);
    }
}
