namespace SimdCodecs.Document;

/// <summary>
/// 文档智能模块的运行配置。默认 opt-in（Enabled=false）：模型仍随产物分发，
/// 但未启用时不会构造推理会话、不占内存，也不向 API 暴露能力。
/// 配置键：Document:Enabled / Backend / Model / MaxPixels / TimeoutSeconds …
/// </summary>
public sealed class DocumentOptions
{
    public const string Section = "Document";

    /// <summary>是否启用文档端点 / 桌面面板。默认关闭，由部署方显式开启。</summary>
    public bool Enabled { get; set; }

    /// <summary>cpu（默认，容器与 CI 固定）或 auto（仅桌面可选，失败回落 cpu）。</summary>
    public string Backend { get; set; } = "cpu";

    /// <summary>模型档位。一期只支持 tiny；其他值会在启动时抛明确错误。</summary>
    public string Model { get; set; } = "tiny";

    /// <summary>单帧像素上限。CPU arena 约 96 B/px，4 MP 峰值约 400 MB。</summary>
    public int MaxPixels { get; set; } = 4_000_000;

    /// <summary>长边上限，避免极端长条图绕过像素闸门。</summary>
    public int MaxEdge { get; set; } = 2400;

    /// <summary>排队 + 执行的请求级超时（秒）。CPU 内核不可中断，超时只让调用方提前返回。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>推理并发度。默认 1：本机 4 核，双模型同进程峰值约 419 MB。</summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>单次识别返回的最大字符数，超出截断并标记 truncated（防止响应体膨胀）。</summary>
    public int MaxTextChars { get; set; } = 200_000;

    /// <summary>
    /// 启用模块但 ApiRuntime:Auth 未开启时，启动时输出显著告警。
    /// 隐私要求：OCR 输出即文档正文，生产部署必须同时开启鉴权。
    /// </summary>
    public bool WarnWhenUnauthenticated { get; set; } = true;

    public bool UseAutoBackend => string.Equals(Backend, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>桌面 / 自检用：启用全部能力但不改变默认部署策略。</summary>
    public static DocumentOptions ForLocal(bool enabled = true) => new()
    {
        Enabled = enabled,
        Backend = "cpu",
        Model = "tiny"
    };

    /// <summary>配置边界收敛，避免错误配置把进程拖死。</summary>
    public void Normalize()
    {
        if (!string.Equals(Model, "tiny", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Document:Model='{Model}' 暂不支持；一期只分发 tiny 模型包。" +
                "需要 small / medium 请改用外置模型文件方案，见 docs/07 第 6.2 节。");
        MaxPixels = Math.Clamp(MaxPixels, 1024, 8_000_000);
        MaxEdge = Math.Clamp(MaxEdge, 64, 16_384);
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 600);
        MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 4);
        MaxTextChars = Math.Clamp(MaxTextChars, 1024, 2_000_000);
        if (!UseAutoBackend) Backend = "cpu";
    }
}

/// <summary>
/// 独立于 Core.ImageLimits 的推理闸门。Core 允许 80 MP（解码安全），
/// 但模型按像素分配 arena，直接放通会 OOM —— 两套闸门语义不同，不能合并。
/// </summary>
public sealed record DocumentLimits(
    int MaxPixels = 4_000_000,
    int MaxEdge = 2400,
    int MinEdge = 32,
    int MaxFrames = 1,
    int MaxTextChars = 200_000)
{
    public static DocumentLimits From(DocumentOptions o) =>
        new(o.MaxPixels, o.MaxEdge, 32, 1, o.MaxTextChars);

    /// <summary>校验结论：413 尺寸超限（不静默缩放）、422 多帧或非法尺寸。</summary>
    public readonly record struct Verdict(bool Accepted, int Status, string? Reason)
    {
        public static Verdict Ok() => new(true, 200, null);
        public static Verdict Reject(int status, string reason) => new(false, status, reason);
    }

    public Verdict Validate(int width, int height, int frameCount)
    {
        if (width < MinEdge || height < MinEdge)
            return Verdict.Reject(422,
                $"文档推理要求宽高各 ≥ {MinEdge}px，收到 {width}×{height}。请用更大尺寸的图片。");
        if (width > MaxEdge || height > MaxEdge)
            return Verdict.Reject(413,
                $"长边 {Math.Max(width, height)}px 超过上限 {MaxEdge}px；不自动缩放（会破坏 OCR 坐标）。");
        long pixels = (long)width * height;
        if (pixels > MaxPixels)
            return Verdict.Reject(413,
                $"{width}×{height}（{pixels / 1_000_000.0:F2} MP）超过文档推理上限 " +
                $"{MaxPixels / 1_000_000.0:F1} MP；请先缩放或拆页，不自动降采样。");
        if (frameCount > MaxFrames)
            return Verdict.Reject(422,
                $"文档推理一次只处理单帧（收到 {frameCount} 帧）；请先用 /api/frames 拆页。");
        return Verdict.Ok();
    }
}

/// <summary>闸门拒绝（413 / 422）。API 层转换为 ProblemDetails，桌面层显示为状态栏消息。</summary>
public sealed class DocumentRejectedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>并发排队超时或后端失败（503）。</summary>
public sealed class DocumentBusyException(string message, Exception? inner = null)
    : Exception(message, inner);
