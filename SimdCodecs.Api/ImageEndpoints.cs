using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Core;
using SimdCodecs.Document;
using SimdCodecs.Processing;

namespace SimdCodecs.Api;

internal sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// 图片处理 API。全部走 Core 的内存态入口：上传字节 → 头部闸门 → 解码 → 编码 → 直接写响应体，
/// 服务端不产生任何临时文件，也不依赖 SkiaSharp / ImageSharp / 原生 DLL。
/// </summary>
public static class ImageEndpoints
{
    public const long MaxBodyBytes = 64L * 1024 * 1024;
    private const int MaxBatchFiles = 20;
    private const int MaxZipFrames = 24;

    internal static readonly ImageLimits Limits = new();
    internal static readonly SemaphoreSlim DecoderGate =
        new(Math.Clamp(Environment.ProcessorCount, 2, 8));

    /// <summary>
    /// 端点层只认 ICodecService。Map() 时从 DI 容器解析并缓存，
    /// 因此这个静态类无需把实现类型写死，替换实现不必改任何端点代码。
    /// </summary>
    internal static ICodecService Codec { get; private set; } = CodecServices.Simd;

    // 文档端点复用同一套上传解析 / 闸门 / 解码约定，因此这几个基础设施是程序集内可见的。
    internal sealed record UploadFile(string Name, byte[] Data);
    internal sealed record Upload(IReadOnlyList<UploadFile> Files, IFormCollection Form);
    internal sealed record Conversion(
        ExportTarget Target, int Quality, int Scale, bool Orient, int Frame,
        PixelRect? Region, BatchPreset? Preset, bool AllFrames);

    public static void Map(WebApplication app)
    {
        Codec = app.Services.GetRequiredService<ICodecService>();
        RouteGroupBuilder api = app.MapGroup("/api");
        // 限流只在配置打开时挂到 /api 路由组；关闭时连中间件都不进管线。
        if (app.Services.GetRequiredService<ApiRuntimeOptions>().RateLimit.Enabled)
            api.RequireRateLimiting(ApiRuntime.RatePolicy);

        api.MapGet("/health", Health);
        api.MapGet("/formats", Formats);
        api.MapGet("/presets", Presets);
        api.MapPost("/inspect", InspectOne);
        api.MapPost("/convert", ConvertOne);
        api.MapPost("/thumbnail", ThumbnailOne);
        api.MapPost("/batch", Batch);
        api.MapPost("/frames", Frames);
        api.MapPost("/process", ProcessOne);
        api.MapPost("/pixels", PixelsOne);
    }

    // ---------------- 元信息 ----------------

    private static IResult Health(DocumentRuntime document) => Results.Ok(new
    {
        service = "CodecLoom Image API",
        version = "1.1.0",
        runtime = Environment.Version.ToString(),
        heifRegistered = Codec.HeifEnabled,
        pureManaged = true,
        processing = "SimdCodecs.Processing (纯托管, 零原生依赖)",
        presets = BatchPresets.All.Select(p => p.Id).ToArray(),
        limits = new
        {
            maxBodyBytes = MaxBodyBytes,
            maxFileBytes = Limits.MaxBytes,
            maxPixels = Limits.MaxPixels,
            maxFramesPerFile = Limits.MaxFrames,
            maxFilesPerBatch = MaxBatchFiles,
            maxFramesPerZip = MaxZipFrames
        },
        document = document.Snapshot(),
        endpoints = new[]
        {
            "GET  /api/health", "GET  /api/formats", "GET  /api/presets",
            "POST /api/inspect", "POST /api/convert", "POST /api/thumbnail",
            "POST /api/batch", "POST /api/frames",
            "POST /api/process", "POST /api/pixels",
            "POST /api/enhance", "POST /api/ocr", "POST /api/document", "GET  /"
        }
    });

    private static IResult Formats() => Results.Ok(new
    {
        inputs = Enum.GetNames<ImageFormat>()
            .Where(n => n != nameof(ImageFormat.Unknown))
            .Select(n =>
            {
                ImageFormat f = Enum.Parse<ImageFormat>(n);
                return new { name = n, decodable = ImageCodecs.IsDecodable(f) };
            })
            .ToArray(),
        outputs = Codec.Targets.Select(t => new
        {
            label = t.Label,
            extension = t.Extension,
            mime = Mime(t),
            quantizePng = t.QuantizePng,
            losslessWebp = t.LosslessWebp
        }).ToArray(),
        notes = new[]
        {
            "AVIF / JPEG XL 可以识别，但当前版本不能解码",
            "HEIF/HEIC 需显式注册；HEVC 专利许可不在 MIT 授权范围内",
            "编解码层只支持 1/2^n 解码期降采样；任意尺寸缩放由 /api/process 的纯托管处理层提供",
            "滤镜 / 水印 / Gray8·BGR 裸像素见 /api/process 与 /api/pixels（不需要 SkiaSharp / ImageSharp）",
            "一键业务动作见 /api/presets：HEIC 批量转码、扫描件拆页等"
        }
    });

    private static IResult Presets() => Results.Ok(new
    {
        presets = BatchPresets.All.Select(p => new
        {
            id = p.Id,
            label = p.Label,
            description = p.Description,
            target = p.Target.Label,
            extension = p.Target.Extension,
            quality = p.Quality,
            sourceFormats = p.SourceFormats?.Select(f => f.ToString()).ToArray(),
            sourceHint = p.SourceHint,
            allFrames = p.AllFrames,
            pageNumbering = p.PageNumbering,
            multiFrameOnly = p.MultiFrameOnly
        }).ToArray(),
        usage = "POST /api/batch 时附加 preset=<id>，未显式给出的 target/quality/frames 由预设填充"
    });

    // ---------------- 单文件端点 ----------------

    private static Task<IResult> InspectOne(HttpContext ctx, CancellationToken ct) => Safe(async () =>
    {
        Upload up = await ReadUploadAsync(ctx, ct);
        UploadFile f = up.Files[0];
        GuardVerdict v = ImageGuard.Validate(f.Data, f.Name, Limits);
        if (!v.Accepted) throw new ApiException(v.StatusCode, v.Reason!);

        ImageEntry e = v.Entry!;
        ImageCodecs.TryReadMetadata(f.Data, out ImageMetadata? m);
        ImageMetadata meta = m ?? ImageMetadata.Empty;
        ImageCodecs.TryReadInfo(f.Data, out ImageInfo info);

        return Results.Ok(new
        {
            name = f.Name,
            bytes = e.Bytes,
            format = e.Format.ToString(),
            width = e.Width,
            height = e.Height,
            megapixels = Math.Round(e.Width * (double)e.Height / 1_000_000, 2),
            frameCount = e.FrameCount,
            decodable = e.Decodable,
            channels = info.Channels,
            bitDepth = info.BitDepth,
            sampleFormat = info.SampleFormat.ToString(),
            orientation = info.Orientation.ToString(),
            colorEncoding = info.ColorEncoding.ToString(),
            hasThumbnail = info.HasThumbnail,
            metadata = new { exifBytes = meta.Exif.Length, xmpBytes = meta.Xmp.Length, iccBytes = meta.Icc.Length }
        });
    });

    private static Task<IResult> ConvertOne(HttpContext ctx, CancellationToken ct) => Safe(async () =>
    {
        Upload up = await ReadUploadAsync(ctx, ct);
        if (up.Files.Count != 1)
            throw new ApiException(400, "/api/convert 一次只处理 1 个文件，多个文件请用 /api/batch。");
        UploadFile f = up.Files[0];
        GuardVerdict v = ImageGuard.Validate(f.Data, f.Name, Limits);
        if (!v.Accepted) throw new ApiException(v.StatusCode, v.Reason!);

        Conversion spec = ReadConversion(up.Form);
        if (spec.Preset is { } p && !p.Accepts(v.Entry!.Format))
            throw new ApiException(400,
                $"预设「{p.Label}」只处理 {p.SourceHint}，收到的是 {v.Entry.Format}。");

        LoadedImage image;
        byte[] output;
        await DecoderGate.WaitAsync(ct);
        try
        {
            image = await Task.Run(() => Codec.Load(f.Data, f.Name, spec.Scale, spec.Orient,
                spec.Region, spec.Frame, ct), ct);
            output = await Task.Run(() => Codec.SaveToBytes(image, spec.Target, spec.Quality, ct), ct);
        }
        finally { DecoderGate.Release(); }

        SetStats(ctx.Response, image, output, v.Entry!, spec.Target, spec.Frame);
        if (spec.Preset is { } usedPreset)
            ctx.Response.Headers["X-Preset-Id"] = usedPreset.Id;
        string download = Path.GetFileNameWithoutExtension(f.Name) +
                          (spec.Frame > 0 ? $"-f{spec.Frame + 1:000}" : "") + spec.Target.Extension;
        return Results.File(output, Mime(spec.Target), download);
    });

    private static Task<IResult> ThumbnailOne(HttpContext ctx, CancellationToken ct) => Safe(async () =>
    {
        Upload up = await ReadUploadAsync(ctx, ct);
        UploadFile f = up.Files[0];
        GuardVerdict v = ImageGuard.Validate(f.Data, f.Name, Limits);
        if (!v.Accepted) throw new ApiException(v.StatusCode, v.Reason!);
        int box = ReadInt(up.Form, "box", 160, 16, 512);

        await DecoderGate.WaitAsync(ct);
        try
        {
            var (pixels, tw, th, _) =
                await Task.Run(() => Codec.LoadThumbnail(f.Data, box, ct), ct);
            byte[] png = await Task.Run(
                () => Codec.EncodeBytes(pixels, tw, th, ImageFormat.Png), ct);
            ctx.Response.Headers["X-Thumbnail-Width"] = tw.ToString(CultureInfo.InvariantCulture);
            ctx.Response.Headers["X-Thumbnail-Height"] = th.ToString(CultureInfo.InvariantCulture);
            ctx.Response.Headers["X-Source-Format"] = v.Entry!.Format.ToString();
            return Results.File(png, "image/png",
                Path.GetFileNameWithoutExtension(f.Name) + "-thumb.png");
        }
        finally { DecoderGate.Release(); }
    });

    // ---------------- 批量 / 拆帧（zip） ----------------

    private static Task<IResult> Batch(HttpContext ctx, CancellationToken ct) =>
        ConvertToZip(ctx, ct, forceFrames: false);

    private static Task<IResult> Frames(HttpContext ctx, CancellationToken ct) =>
        ConvertToZip(ctx, ct, forceFrames: true);

    private static Task<IResult> ConvertToZip(HttpContext ctx, CancellationToken ct, bool forceFrames) =>
        Safe(async () =>
        {
            Upload up = await ReadUploadAsync(ctx, ct);
            if (forceFrames && up.Files.Count != 1)
                throw new ApiException(400, "/api/frames 一次只拆 1 个文件。");

            Conversion spec = ReadConversion(up.Form);
            bool allFrames = forceFrames || spec.AllFrames;
            bool pageNumbering = spec.Preset?.PageNumbering == true && allFrames;

            using var buffer = new MemoryStream();
            var report = new StringBuilder();
            var manifest = new StringBuilder();
            manifest.AppendLine("input,output,sourceFormat,page,width,height,bytes,status");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int ok = 0, failed = 0, skipped = 0;
            long before = 0, after = 0;

            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (UploadFile f in up.Files)
                {
                    GuardVerdict v = ImageGuard.Validate(f.Data, f.Name, Limits);
                    if (!v.Accepted)
                    {
                        failed++;
                        report.AppendLine($"REJECT {f.Name}: HTTP {v.StatusCode} {v.Reason}");
                        manifest.AppendLine($"{f.Name},,,0,0,0,0,REJECT");
                        continue;
                    }
                    if (spec.Preset is { } preset && !preset.Accepts(v.Entry!.Format))
                    {
                        skipped++;
                        report.AppendLine($"SKIP   {f.Name}: 预设只处理 {preset.SourceHint}，收到 {v.Entry.Format}");
                        manifest.AppendLine($"{f.Name},,{v.Entry.Format},0,0,0,0,SKIP");
                        continue;
                    }
                    if (spec.Preset is { MultiFrameOnly: true } && v.Entry!.FrameCount < 2)
                    {
                        skipped++;
                        report.AppendLine($"SKIP   {f.Name}: 预设只拆多页文档，该文件为单帧");
                        manifest.AppendLine($"{f.Name},,{v.Entry.Format},0,0,0,0,SKIP");
                        continue;
                    }

                    int frames = allFrames
                        ? Math.Min(Math.Max(1, v.Entry!.FrameCount), MaxZipFrames)
                        : 1;
                    if (allFrames && v.Entry!.FrameCount > MaxZipFrames)
                        report.AppendLine($"NOTE  {f.Name}: 共 {v.Entry.FrameCount} 帧，zip 只取前 {MaxZipFrames} 帧");

                    for (int i = 0; i < frames; i++)
                    {
                        string stem = Path.GetFileNameWithoutExtension(f.Name);
                        string suffix = allFrames
                            ? (pageNumbering ? $"-p{i + 1:000}" : $"-f{i + 1:000}") : "";
                        string wanted = stem + suffix + spec.Target.Extension;
                        string name = UniqueName(wanted, used);
                        try
                        {
                            LoadedImage image;
                            byte[] bytes;
                            await DecoderGate.WaitAsync(ct);
                            try
                            {
                                image = await Task.Run(() => Codec.Load(f.Data, f.Name,
                                    spec.Scale, spec.Orient, null, allFrames ? i : 0, ct), ct);
                                bytes = await Task.Run(
                                    () => Codec.SaveToBytes(image, spec.Target, spec.Quality, ct), ct);
                            }
                            finally { DecoderGate.Release(); }

                            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                            await using (var stream = entry.Open())
                                await stream.WriteAsync(bytes, ct);

                            ok++;
                            before += f.Data.LongLength;
                            after += bytes.LongLength;
                            report.AppendLine($"OK    {name}  {f.Data.Length} -> {bytes.Length} B" +
                                              $"  decode={image.DecodeMs:F2}ms");
                            manifest.AppendLine($"{f.Name},{name},{v.Entry!.Format},{i + 1}," +
                                                $"{image.Width},{image.Height},{bytes.Length},OK");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            report.AppendLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
                            manifest.AppendLine($"{f.Name},{name},{v.Entry!.Format},{i + 1},0,0,0,FAIL");
                        }
                    }
                }

                report.AppendLine();
                report.AppendLine($"summary: ok={ok} failed={failed} skipped={skipped} " +
                                  $"input={before} B output={after} B " +
                                  $"saved={(before > 0 ? 100.0 * (before - after) / before : 0):F1}% " +
                                  $"target={spec.Target.Label} preset={spec.Preset?.Id ?? "none"} " +
                                  $"pageNumbering={pageNumbering}");
                ZipArchiveEntry reportEntry = zip.CreateEntry("_report.txt", CompressionLevel.Fastest);
                using (var writer = new StreamWriter(reportEntry.Open(), new UTF8Encoding(false)))
                    writer.Write(report.ToString());
                ZipArchiveEntry manifestEntry = zip.CreateEntry(
                    BatchService.ManifestFileName, CompressionLevel.Fastest);
                using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                    writer.Write(manifest.ToString());
            }

            byte[] zipBytes = buffer.ToArray();
            ctx.Response.Headers["X-Batch-Ok"] = ok.ToString(CultureInfo.InvariantCulture);
            ctx.Response.Headers["X-Batch-Failed"] = failed.ToString(CultureInfo.InvariantCulture);
            ctx.Response.Headers["X-Batch-Skipped"] = skipped.ToString(CultureInfo.InvariantCulture);
            if (spec.Preset is { } appliedPreset)
                ctx.Response.Headers["X-Preset-Id"] = appliedPreset.Id;
            ctx.Response.Headers["X-Batch-Input-Bytes"] = before.ToString(CultureInfo.InvariantCulture);
            ctx.Response.Headers["X-Batch-Output-Bytes"] = after.ToString(CultureInfo.InvariantCulture);
            string zipName = "simdcodecs-" + spec.Target.Extension.TrimStart('.') +
                             (spec.Target.QuantizePng ? "-quant" : "") +
                             (spec.Target.LosslessWebp ? "-lossless" : "") + ".zip";
            return Results.File(zipBytes, "application/zip", zipName);
        });

    // ---------------- 处理层 / CV 输入 ----------------

    /// <summary>
    /// 任意尺寸缩放 + 滤镜 + 水印，返回编码后的图片。
    /// 处理层结果是裸像素布局时先转回 Bgra32 再编码，保证浏览器 / 桌面都能直接打开。
    /// </summary>
    private static Task<IResult> ProcessOne(HttpContext ctx, CancellationToken ct) => Safe(async () =>
    {
        (UploadFile f, GuardVerdict v, Conversion spec, ProcessOptions options) =
            await ReadProcessRequestAsync(ctx, ct);
        _ = v;

        LoadedImage image;
        await DecoderGate.WaitAsync(ct);
        try
        {
            image = await Task.Run(() => Codec.Load(f.Data, f.Name, spec.Scale, spec.Orient,
                spec.Region, spec.Frame, ct), ct);
        }
        finally { DecoderGate.Release(); }

        EnsureProcessLimits(image, options);
        ProcessResult result = ImageProcessor.Apply(image.Pixels, image.Width, image.Height, options);
        byte[] display = ImageProcessor.ToDisplayBgra(result);
        byte[] output = Codec.EncodeBytes(display, result.Width, result.Height,
            spec.Target.Format, spec.Quality, spec.Target.QuantizePng, spec.Target.LosslessWebp);

        CvAnalysis cv = ImageProcessor.AnalyzeForOcr(result);
        ctx.Response.Headers["X-Process-Steps"] = Uri.EscapeDataString(result.Summary);
        ctx.Response.Headers["X-Pixel-Layout"] = result.Layout.ToString();
        ctx.Response.Headers["X-Output-Width"] = result.Width.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Output-Height"] = result.Height.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Input-Bytes"] = image.FileBytes.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Output-Bytes"] = output.LongLength.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Cv-Otsu-Threshold"] = cv.Threshold.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Cv-Text-Rows"] = cv.TextRows.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Source-Format"] = v.Entry!.Format.ToString();
        return Results.File(output, Mime(spec.Target),
            Path.GetFileNameWithoutExtension(f.Name) + "-processed" + spec.Target.Extension);
    });

    /// <summary>
    /// 返回下游可直接消费的连续像素字节（Gray8 / Bgr24 / Rgb24 / Bgra32）。
    /// 附带 Otsu 二值化 + 行投影的分析结果，证明这些字节确实能被 CV 算子读取，而不是一堆占位数据。
    /// </summary>
    private static Task<IResult> PixelsOne(HttpContext ctx, CancellationToken ct) => Safe(async () =>
    {
        (UploadFile f, GuardVerdict v, Conversion spec, ProcessOptions options) =
            await ReadProcessRequestAsync(ctx, ct);
        _ = v;

        LoadedImage image;
        await DecoderGate.WaitAsync(ct);
        try
        {
            image = await Task.Run(() => Codec.Load(f.Data, f.Name, spec.Scale, spec.Orient,
                spec.Region, spec.Frame, ct), ct);
        }
        finally { DecoderGate.Release(); }

        EnsureProcessLimits(image, options);
        ProcessResult result = ImageProcessor.Apply(image.Pixels, image.Width, image.Height, options);
        CvAnalysis cv = ImageProcessor.AnalyzeForOcr(result);

        ctx.Response.Headers["X-Pixel-Layout"] = result.Layout.ToString();
        ctx.Response.Headers["X-Width"] = result.Width.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Height"] = result.Height.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Stride"] = result.Stride.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Bytes-Per-Pixel"] = result.BytesPerPixel.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Cv-Otsu-Threshold"] = cv.Threshold.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Cv-Ink-Ratio"] = cv.InkRatio.ToString("F4", CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Cv-Text-Rows"] = cv.TextRows.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-Source-Format"] = v.Entry!.Format.ToString();
        _ = spec;
        return Results.File(result.Pixels, PixelLayoutInfo.Mime(result.Layout),
            Path.GetFileNameWithoutExtension(f.Name) + "." + result.Layout.ToString().ToLowerInvariant() + ".raw");
    });

    private static async Task<(UploadFile File, GuardVerdict Verdict, Conversion Spec,
        ProcessOptions Options)> ReadProcessRequestAsync(HttpContext ctx, CancellationToken ct)
    {
        Upload up = await ReadUploadAsync(ctx, ct);
        if (up.Files.Count != 1)
            throw new ApiException(400, "处理端点一次只接收 1 个文件，批量请用 /api/batch。");
        UploadFile f = up.Files[0];
        GuardVerdict v = ImageGuard.Validate(f.Data, f.Name, Limits);
        if (!v.Accepted) throw new ApiException(v.StatusCode, v.Reason!);
        return (f, v, ReadConversion(up.Form), ReadProcessOptions(up.Form));
    }

    private static void EnsureProcessLimits(LoadedImage image, ProcessOptions options)
    {
        int tw = options.ResizeWidth > 0 ? options.ResizeWidth
            : options.ResizeHeight > 0
                ? Math.Max(1, (int)Math.Round(image.Width * options.ResizeHeight / (double)image.Height))
                : image.Width;
        int th = options.ResizeHeight > 0 ? options.ResizeHeight
            : options.ResizeWidth > 0
                ? Math.Max(1, (int)Math.Round(image.Height * options.ResizeWidth / (double)image.Width))
                : image.Height;
        if ((long)tw * th > Limits.MaxPixels)
            throw new ApiException(413,
                $"处理目标 {tw}×{th} 超过像素上限 {Limits.MaxPixels}。");
    }

    private static ProcessOptions ReadProcessOptions(IFormCollection form) => new(
        ResizeWidth: ReadInt(form, "resizeW", 0, 0, 16_384),
        ResizeHeight: ReadInt(form, "resizeH", 0, 0, 16_384),
        Brightness: ReadDouble(form, "brightness", 0, -100, 100),
        Contrast: ReadDouble(form, "contrast", 1.0, 0.1, 3.0),
        Grayscale: ReadBool(form, "gray", false),
        Invert: ReadBool(form, "invert", false),
        Watermark: ReadString(form, "watermark"),
        Layout: ReadLayout(form));

    private static PixelLayout ReadLayout(IFormCollection form) =>
        ReadString(form, "layout")?.ToLowerInvariant() switch
        {
            "gray8" or "gray" => PixelLayout.Gray8,
            "bgr24" or "bgr" => PixelLayout.Bgr24,
            "rgb24" or "rgb" => PixelLayout.Rgb24,
            "bgra32" or "bgra" or null or "" => PixelLayout.Bgra32,
            string unknown => throw new ApiException(400,
                $"未知像素布局 '{unknown}'，可用 gray8 / bgr24 / rgb24 / bgra32。")
        };

    private static double ReadDouble(IFormCollection form, string key,
        double fallback, double min, double max) =>
        double.TryParse(ReadString(form, key), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double n) ? Math.Clamp(n, min, max) : fallback;

    // ---------------- 基础设施 ----------------

    /// <summary>
    /// 文档端点与 /api/process 共用的单帧解码路径：同一份 scale / orient / region / frame 语义，
    /// 同一个 DecoderGate，避免为三个新端点复制一套上传与解码逻辑。
    /// </summary>
    internal static async Task<LoadedImage> DecodeSingleAsync(
        UploadFile file, IFormCollection form, CancellationToken ct)
    {
        Conversion spec = ReadConversion(form);
        await DecoderGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => Codec.Load(file.Data, file.Name, spec.Scale, spec.Orient,
                spec.Region, spec.Frame, ct), ct);
        }
        finally { DecoderGate.Release(); }
    }

    internal static async Task<IResult> Safe(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ApiException ex)
        {
            return Results.Problem(title: "Request rejected", detail: ex.Message, statusCode: ex.Status);
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(title: "Request cancelled", detail: "Client disconnected or timed out.",
                statusCode: 499);
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Processing failed",
                detail: $"{ex.GetType().Name}: {ex.Message}", statusCode: 500);
        }
    }

    internal static async Task<Upload> ReadUploadAsync(HttpContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.HasFormContentType)
            throw new ApiException(415, "需要 multipart/form-data 上传，文件字段名 file 或 files。");

        IFormCollection form = await ctx.Request.ReadFormAsync(ct);
        if (form.Files.Count == 0) throw new ApiException(400, "没有收到任何文件。");
        if (form.Files.Count > MaxBatchFiles)
            throw new ApiException(413, $"一次最多上传 {MaxBatchFiles} 个文件。");

        var files = new List<UploadFile>(form.Files.Count);
        foreach (IFormFile file in form.Files)
        {
            if (file.Length > Limits.MaxBytes)
                throw new ApiException(413,
                    $"{SafeName(file.FileName)} 有 {file.Length / 1024.0 / 1024:F1} MB，超过单文件上限。");
            using var ms = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
            await file.CopyToAsync(ms, ct);
            files.Add(new UploadFile(SafeName(file.FileName), ms.ToArray()));
        }
        return new Upload(files, form);
    }

    internal static string SafeName(string raw)
    {
        string name = Path.GetFileName(raw.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) name = "upload";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 120 ? name[..120] : name;
    }

    private static string UniqueName(string wanted, HashSet<string> used)
    {
        string stem = Path.GetFileNameWithoutExtension(wanted);
        string ext = Path.GetExtension(wanted);
        string candidate = wanted;
        for (int i = 1; !used.Add(candidate); i++)
            candidate = $"{stem}-{i}{ext}";
        return candidate;
    }

    /// <summary>预设只填充「调用方没有显式指定」的字段，显式参数永远优先。</summary>
    internal static Conversion ReadConversion(IFormCollection form)
    {
        BatchPreset? preset = BatchPresets.Find(ReadString(form, "preset"));
        ExportTarget target = preset is not null && ReadString(form, "target") is null
            ? preset.Target : ReadTarget(form);
        return new Conversion(
            target,
            ReadInt(form, "quality", preset?.Quality ?? 88, 1, 100),
            ReadScale(form),
            ReadBool(form, "orient", true),
            ReadInt(form, "frame", 0, 0, Limits.MaxFrames - 1),
            ReadRegion(form),
            preset,
            ReadBool(form, "frames", preset?.AllFrames ?? false));
    }

    private static ExportTarget ReadTarget(IFormCollection form)
    {
        string? label = ReadString(form, "target");
        if (label is null) return Codec.Target("WebP");
        return Codec.FindTarget(label) ?? throw new ApiException(400,
            $"未知导出格式 '{label}'，可用：{string.Join(" / ", Codec.Targets.Select(t => t.Label))}");
    }

    private static int ReadScale(IFormCollection form)
    {
        int s = ReadInt(form, "scale", 1, 1, 8);
        return s is 1 or 2 or 4 or 8 ? s : 1;
    }

    private static PixelRect? ReadRegion(IFormCollection form)
    {
        string? xs = ReadString(form, "x"), ys = ReadString(form, "y");
        string? ws = ReadString(form, "w"), hs = ReadString(form, "h");
        if (xs is null && ys is null && ws is null && hs is null) return null;
        if (!int.TryParse(xs, out int x) || !int.TryParse(ys, out int y) ||
            !int.TryParse(ws, out int w) || !int.TryParse(hs, out int h))
            throw new ApiException(400, "裁剪参数 x / y / w / h 必须同时给出整数。");
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || w > 20_000 || h > 20_000)
            throw new ApiException(400, "裁剪区域不合法（x,y >= 0，0 < w,h <= 20000）。");
        return new PixelRect(x, y, w, h);
    }

    internal static string? ReadString(IFormCollection form, string key) =>
        form.TryGetValue(key, out var value) ? value.ToString() : null;

    internal static int ReadInt(IFormCollection form, string key, int fallback, int min, int max) =>
        int.TryParse(ReadString(form, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? Math.Clamp(n, min, max) : fallback;

    internal static bool ReadBool(IFormCollection form, string key, bool fallback) =>
        ReadString(form, key)?.ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => fallback
        };

    private static string Mime(ExportTarget target) => target.Format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Webp => "image/webp",
        _ => "image/png"
    };

    private static void SetStats(HttpResponse response, LoadedImage image, byte[] output,
        ImageEntry entry, ExportTarget target, int frame)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        response.Headers["X-Source-Format"] = entry.Format.ToString();
        response.Headers["X-Target-Format"] = target.Format.ToString();
        response.Headers["X-Decoded-Width"] = image.Width.ToString(inv);
        response.Headers["X-Decoded-Height"] = image.Height.ToString(inv);
        response.Headers["X-Decode-Ms"] = image.DecodeMs.ToString("F2", inv);
        response.Headers["X-Frame-Index"] = frame.ToString(inv);
        response.Headers["X-Frame-Count"] = image.FrameCount.ToString(inv);
        response.Headers["X-Input-Bytes"] = image.FileBytes.ToString(inv);
        response.Headers["X-Output-Bytes"] = output.LongLength.ToString(inv);
        response.Headers["X-Saved-Percent"] =
            (image.FileBytes > 0 ? 100.0 * (image.FileBytes - output.LongLength) / image.FileBytes : 0)
            .ToString("F1", inv);
    }
}
