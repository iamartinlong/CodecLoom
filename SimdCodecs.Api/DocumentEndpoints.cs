using System.Globalization;
using Microsoft.Extensions.Logging;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Core;
using SimdCodecs.Document;

namespace SimdCodecs.Api;

/// <summary>
/// 文档智能端点：文档照片增强、文字识别、以及「增强 → 识别」一步式管线。
/// 复用 ImageEndpoints 的上传解析 / ImageGuard / 单帧解码 / ProblemDetails 约定；
/// 额外的 DocumentLimits 与 DocumentGate 由 DocumentRuntime 统一提供。
///
/// 隐私：OCR 输出即文档正文。响应体只回给调用方，日志与 /api/metrics 只记行数、
/// 字符数、耗时与哈希，绝不写正文或文件名；启用模块却未开鉴权时启动时输出告警。
/// </summary>
public static class DocumentEndpoints
{
    /// <summary>
    /// 从当前请求的服务容器取模块实例，而不是缓存静态字段：
    /// xUnit 会并行启动多个不同配置的 Kestrel 实例，静态字段会被后启动的 app 覆盖，
    /// 导致「已启用」的 fixture 命中「未启用」实例的 501。
    /// </summary>
    private static DocumentRuntime GetRuntime(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<DocumentRuntime>();

    public static void Map(WebApplication app)
    {
        DocumentRuntime document = app.Services.GetRequiredService<DocumentRuntime>();
        ApiRuntimeOptions runtime = app.Services.GetRequiredService<ApiRuntimeOptions>();
        if (document.Options.Enabled && document.Options.WarnWhenUnauthenticated && !runtime.Auth.Enabled)
        {
            app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("SimdCodecs.Api.Document")
                .LogWarning(
                    "文档智能模块已启用，但 ApiRuntime:Auth 未开启。OCR 结果可能包含合同 / 证件正文，" +
                    "生产部署请同时设置 ApiRuntime__Auth__Enabled=true 与 ApiRuntime__Auth__Key；" +
                    "可用 Document__WarnWhenUnauthenticated=false 关闭本告警。");
        }

        RouteGroupBuilder api = app.MapGroup("/api");
        if (runtime.RateLimit.Enabled) api.RequireRateLimiting(ApiRuntime.RatePolicy);

        api.MapPost("/enhance", EnhanceOne);
        api.MapPost("/ocr", OcrOne);
        api.MapPost("/document", DocumentOne);
    }

    private sealed record Input(LoadedImage Image, ImageEndpoints.UploadFile File,
        GuardVerdict Verdict, IFormCollection Form);

    // ---------------- 端点 ----------------

    /// <summary>POST /api/enhance：返回增强后的 PNG（alpha 保持、尺寸不变）。</summary>
    private static Task<IResult> EnhanceOne(HttpContext ctx, CancellationToken ct) =>
        ImageEndpoints.Safe(async () =>
        {
            Input input = await ReadInputAsync(ctx, ct);
            DocumentResult r = await ExecuteAsync(ctx, input,
                new DocumentRequest(Enhance: true, Ocr: false), ct);

            byte[] png = ImageEndpoints.Codec.EncodeBytes(
                r.EnhancedPixels!, r.Width, r.Height, ImageFormat.Png);
            SetHeaders(ctx.Response, r);
            ctx.Response.Headers["X-Output-Bytes"] = png.LongLength.ToString(CultureInfo.InvariantCulture);
            return Results.File(png, "image/png",
                Path.GetFileNameWithoutExtension(input.File.Name) + "-enhanced.png");
        });

    /// <summary>
    /// POST /api/ocr：返回文本与坐标 JSON。
    /// 参数 enhance=true 可先增强再识别（不返回图片），ctc=true 附带逐字符估算框。
    /// </summary>
    private static Task<IResult> OcrOne(HttpContext ctx, CancellationToken ct) =>
        ImageEndpoints.Safe(async () =>
        {
            Input input = await ReadInputAsync(ctx, ct);
            bool enhance = ImageEndpoints.ReadBool(input.Form, "enhance", false);
            bool ctc = ImageEndpoints.ReadBool(input.Form, "ctc", false);
            DocumentResult r = await ExecuteAsync(ctx, input,
                new DocumentRequest(Enhance: enhance, Ocr: true, Ctc: ctc), ct);
            SetHeaders(ctx.Response, r);
            return Results.Ok(Payload(r));
        });

    /// <summary>
    /// POST /api/document：增强 → 识别一步式。compare=true 额外跑一次原图 OCR，
    /// 用行数 / 字符数对比量化增强收益（PoC 实测透印页 55 行 → 14 行）。
    /// </summary>
    private static Task<IResult> DocumentOne(HttpContext ctx, CancellationToken ct) =>
        ImageEndpoints.Safe(async () =>
        {
            Input input = await ReadInputAsync(ctx, ct);
            bool compare = ImageEndpoints.ReadBool(input.Form, "compare", false);
            bool ctc = ImageEndpoints.ReadBool(input.Form, "ctc", false);
            DocumentResult r = await ExecuteAsync(ctx, input,
                new DocumentRequest(Enhance: true, Ocr: true, Compare: compare, Ctc: ctc), ct);
            SetHeaders(ctx.Response, r);
            return Results.Ok(Payload(r));
        });

    // ---------------- 请求与执行 ----------------

    private static void EnsureEnabled(HttpContext ctx)
    {
        if (!GetRuntime(ctx).Options.Enabled)
            throw new ApiException(501,
                "文档智能模块未启用。请设置 Document:Enabled=true（环境变量 Document__Enabled=true）后重启服务；" +
                "默认 opt-in，避免未授权部署承担模型内存与隐私风险。");
    }

    private static async Task<Input> ReadInputAsync(HttpContext ctx, CancellationToken ct)
    {
        DocumentRuntime document = GetRuntime(ctx);
        EnsureEnabled(ctx);
        ImageEndpoints.Upload up = await ImageEndpoints.ReadUploadAsync(ctx, ct);
        if (up.Files.Count != 1)
            throw new ApiException(400, "文档端点一次只处理 1 个文件；多页请先用 /api/frames 拆页。");
        ImageEndpoints.UploadFile f = up.Files[0];
        GuardVerdict v = ImageGuard.Validate(f.Data, f.Name, ImageEndpoints.Limits);
        if (!v.Accepted) throw new ApiException(v.StatusCode, v.Reason!);

        // 先用文件头的尺寸做一次预检：超限图不必解码就能以 413 / 422 拒绝，
        // 避免 80 MP 的合法上传白白吃掉解码内存（Runtime 内还有同一套兜底校验）。
        DocumentLimits.Verdict pre = document.Limits.Validate(
            v.Entry!.Width, v.Entry.Height, v.Entry.FrameCount);
        if (!pre.Accepted) throw new ApiException(pre.Status, pre.Reason!);

        LoadedImage image = await ImageEndpoints.DecodeSingleAsync(f, up.Form, ct);
        return new Input(image, f, v, up.Form);
    }

    private static async Task<DocumentResult> ExecuteAsync(HttpContext ctx, Input input,
        DocumentRequest request, CancellationToken ct)
    {
        DocumentRuntime runtime = GetRuntime(ctx);
        var frame = new DocumentFrame(input.Image.Pixels, input.Image.Width,
            input.Image.Height, input.Image.Stride, input.Image.FrameCount);
        try
        {
            // 双重超时：Runtime 内部排队有超时，这里的 WaitAsync 让 HTTP 请求也不会无限等待。
            // CPU 推理内核不可中断，超时后后台任务仍会跑完并释放闸门；这是刻意的取舍。
            return await runtime.RunAsync(frame, request, ct).WaitAsync(runtime.Timeout, ct);
        }
        catch (DocumentRejectedException ex) { throw new ApiException(ex.Status, ex.Message); }
        catch (DocumentBusyException ex) { throw new ApiException(503, ex.Message); }
        catch (TimeoutException)
        {
            throw new ApiException(503,
                $"文档推理超过 {runtime.Timeout.TotalSeconds:F0}s 未完成，请换更小的图片或稍后重试。");
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            throw new ApiException(500, $"文档推理失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- 响应 ----------------

    private static object Payload(DocumentResult r)
    {
        OcrOutcome? ocr = r.Ocr;
        return new
        {
            model = "tiny",
            backend = r.Enhance?.Backend ?? ocr?.Backend ?? "cpu",
            width = r.Width,
            height = r.Height,
            enhanced = r.Enhance is not null,
            degraded = r.Enhance?.Degraded == true || ocr?.Degraded == true,
            text = ocr?.Text ?? "",
            packedTextHash = ocr?.PackedTextHash.ToString("x16"),
            lineCount = ocr?.LineCount ?? 0,
            charCount = ocr?.CharCount ?? 0,
            detectedCount = ocr?.DetectedCount ?? 0,
            truncated = ocr?.Truncated ?? false,
            lines = ocr?.Lines.Select(l => new
            {
                text = l.Text,
                score = l.Score,
                box = l.Box,
                characters = l.Characters?.Select(c => new
                {
                    text = c.Text, score = c.Score, box = c.Box
                }).ToArray()
            }).ToArray(),
            enhanceMs = r.Enhance?.ElapsedMs ?? 0,
            ocrMs = ocr?.ElapsedMs ?? 0,
            totalMs = r.TotalMs,
            // compare=true 时给出增强前的 OCR 统计（同样不含正文）
            original = r.OriginalOcr is null ? null : OcrSummary.Stats(r.OriginalOcr)
        };
    }

    /// <summary>响应头只写统计与诊断量，绝不写正文 / 文件名。</summary>
    private static void SetHeaders(HttpResponse response, DocumentResult r)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        response.Headers["X-Document-Model"] = "tiny";
        response.Headers["X-Document-Backend"] = r.Enhance?.Backend ?? r.Ocr?.Backend ?? "cpu";
        response.Headers["X-Document-Degraded"] =
            (r.Enhance?.Degraded == true || r.Ocr?.Degraded == true) ? "true" : "false";
        response.Headers["X-Document-Enhance-Ms"] =
            (r.Enhance?.ElapsedMs ?? 0).ToString("F2", inv);
        response.Headers["X-Document-Ocr-Ms"] =
            (r.Ocr?.ElapsedMs ?? 0).ToString("F2", inv);
        response.Headers["X-Document-Total-Ms"] = r.TotalMs.ToString("F2", inv);
        response.Headers["X-Document-Lines"] =
            (r.Ocr?.LineCount ?? 0).ToString(inv);
        response.Headers["X-Document-Chars"] =
            (r.Ocr?.CharCount ?? 0).ToString(inv);
        response.Headers["X-Output-Width"] = r.Width.ToString(inv);
        response.Headers["X-Output-Height"] = r.Height.ToString(inv);
        if (r.Ocr is not null)
            response.Headers["X-Document-Text-Hash"] = r.Ocr.PackedTextHash.ToString("x16");
    }
}
