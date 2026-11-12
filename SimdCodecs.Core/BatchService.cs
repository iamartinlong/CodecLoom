using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;

namespace SimdCodecs.Core;

public sealed record BatchSettings(
    string OutputDirectory,
    ExportTarget Target,
    int Quality = 90,
    int Scale = 1,
    bool ApplyOrientation = true,
    int MaxConcurrency = 0,
    bool Overwrite = false,
    bool AllFrames = false,
    int MaxFramesPerFile = 200,
    // ---- 预设相关（全部可选，旧调用零改动） ----
    string? PresetId = null,
    IReadOnlyList<ImageFormat>? SourceFilter = null,
    bool PageNumbering = false,
    bool MultiFrameOnly = false,
    bool WriteManifest = true);

public sealed record BatchItemResult(
    string Input, string Output, long InputBytes, long OutputBytes, double Ms,
    string? Error = null, bool Skipped = false)
{
    public bool Ok => Error is null && !Skipped;
    public double SavedPercent => InputBytes <= 0 || !Ok
        ? 0 : 100.0 * (InputBytes - OutputBytes) / InputBytes;
}

public sealed record BatchProgress(int Done, int Total, string Current, BatchItemResult? Last);

/// <summary>
/// 批量转码：并行、可取消、单文件错误隔离、输出名自动避让、预设过滤、页码命名、CSV 转换清单。
/// 刻意不依赖 Avalonia，命令行 / 自检可独立调用；这也是产品化时最容易抽成服务端的一层。
/// </summary>
public static class BatchService
{
    public const string ManifestFileName = "_manifest.csv";

    public static Task<IReadOnlyList<BatchItemResult>> RunAsync(
        IReadOnlyList<string> files, BatchSettings settings,
        IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => RunCore(files, settings, progress, ct), CancellationToken.None);

    private static IReadOnlyList<BatchItemResult> RunCore(
        IReadOnlyList<string> files, BatchSettings settings,
        IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(settings.OutputDirectory);
        int workers = settings.MaxConcurrency > 0
            ? settings.MaxConcurrency
            : Math.Clamp(Environment.ProcessorCount, 1, 8);

        // 先探一次头，既能在列表里预校验，也能把总任务数（含多帧）算准。
        var jobs = new List<(string Path, int Frame, ImageEntry Entry)>();
        var results = new ConcurrentBag<BatchItemResult>();
        var manifest = new ConcurrentBag<string>();
        foreach (string file in files)
        {
            ImageEntry entry = CodecServices.Current.Inspect(file);
            if (!entry.Decodable)
            {
                results.Add(new BatchItemResult(file, "", entry.Bytes, 0, 0, entry.Error ?? "不支持"));
                manifest.Add(ManifestLine(file, "", entry, -1, 0, 0, 0, "REJECT", entry.Error));
                continue;
            }
            if (settings.SourceFilter is { Count: > 0 } filter && !filter.Contains(entry.Format))
            {
                results.Add(new BatchItemResult(file, "", entry.Bytes, 0, 0,
                    $"预设只处理 {string.Join("/", filter)}，已跳过 {entry.Format}", Skipped: true));
                manifest.Add(ManifestLine(file, "", entry, -1, 0, 0, 0, "SKIP",
                    $"source format {entry.Format}"));
                continue;
            }
            if (settings.MultiFrameOnly && entry.FrameCount < 2)
            {
                results.Add(new BatchItemResult(file, "", entry.Bytes, 0, 0,
                    "预设只拆多页/多帧文档，单帧文件已跳过", Skipped: true));
                manifest.Add(ManifestLine(file, "", entry, -1, 0, 0, 0, "SKIP", "single frame"));
                continue;
            }
            int count = settings.AllFrames
                ? Math.Clamp(entry.FrameCount, 1, settings.MaxFramesPerFile)
                : 1;
            for (int i = 0; i < count; i++) jobs.Add((file, i, entry));
        }

        // 多帧被截短时如实告知，避免用户以为整份文档都导出了。
        foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ImageEntry e = CodecServices.Current.Inspect(file);
            if (settings.AllFrames && e.Decodable && e.FrameCount > settings.MaxFramesPerFile)
                results.Add(new BatchItemResult(file, "", e.Bytes, 0, 0,
                    $"共 {e.FrameCount} 帧，按上限只导出前 {settings.MaxFramesPerFile} 帧"));
        }

        int total = jobs.Count + results.Count;
        int done = 0;
        var claimed = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        foreach (BatchItemResult pre in results)
            progress?.Report(new BatchProgress(++done, total, Path.GetFileName(pre.Input), pre));

        Parallel.ForEach(jobs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = workers,
                CancellationToken = CancellationToken.None // 取消由下面逐项检查，保留已完成结果
            },
            job =>
            {
                string name = Path.GetFileNameWithoutExtension(job.Path);
                string suffix = settings.AllFrames
                    ? (settings.PageNumbering ? $"-p{job.Frame + 1:000}" : $"-f{job.Frame + 1:000}")
                    : "";
                string output = Reserve(Path.Combine(settings.OutputDirectory,
                    name + suffix + settings.Target.Extension), claimed);

                BatchItemResult result;
                try
                {
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);

                    var sw = Stopwatch.StartNew();
                    LoadedImage image = CodecServices.Current.Load(job.Path,
                        scale: settings.Scale,
                        applyOrientation: settings.ApplyOrientation,
                        frameIndex: settings.AllFrames ? job.Frame : 0,
                        ct: ct);
                    CodecServices.Current.Save(image, output, settings.Target, settings.Quality, ct);
                    sw.Stop();
                    long outBytes = new FileInfo(output).Length;
                    result = new BatchItemResult(job.Path, output, image.FileBytes, outBytes,
                        sw.Elapsed.TotalMilliseconds);
                    manifest.Add(ManifestLine(job.Path, output, job.Entry, job.Frame,
                        image.Width, image.Height, outBytes, "OK", null, image.Metadata));
                }
                catch (Exception ex)
                {
                    // 写失败时别留下半截文件。
                    TryDelete(output);
                    result = new BatchItemResult(job.Path, output, 0, 0, 0,
                        $"{ex.GetType().Name}: {ex.Message}");
                    manifest.Add(ManifestLine(job.Path, output, job.Entry, job.Frame,
                        0, 0, 0, "FAIL", ex.Message));
                }
                results.Add(result);
                progress?.Report(new BatchProgress(
                    Interlocked.Increment(ref done), total,
                    Path.GetFileName(job.Path), result));
            });

        IReadOnlyList<BatchItemResult> ordered = results
            .OrderBy(r => r.Input, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Output, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 只在确实产出过结果且未被取消时写清单：取消场景不能留下非空文件。
        if (settings.WriteManifest && !ct.IsCancellationRequested && ordered.Any(r => r.Ok))
            TryWriteManifest(settings.OutputDirectory, ordered.Count, manifest);

        return ordered;
    }

    /// <summary>转换清单：页码、源格式、输出尺寸、元数据字节数都可追溯，方便档案/扫描件场景对账。</summary>
    private static void TryWriteManifest(string dir, int resultCount, ConcurrentBag<string> lines)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("input,output,sourceFormat,page,width,height,outputBytes,status,exifBytes,xmpBytes,iccBytes,note");
            foreach (string line in lines.OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(line);
            sb.AppendLine($"# rows={lines.Count} results={resultCount} presetManifest=v1");
            File.WriteAllText(Path.Combine(dir, ManifestFileName), sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* 清单是辅助产物，写失败不影响转换结果 */ }
    }

    private static string ManifestLine(string input, string output, ImageEntry entry, int frame,
        int width, int height, long outBytes, string status, string? note,
        ImageMetadata? metadata = null) => string.Join(',', new[]
        {
            Csv(Path.GetFileName(input)), Csv(Path.GetFileName(output)),
            entry.Format.ToString(), (frame < 0 ? "" : (frame + 1).ToString(CultureInfo.InvariantCulture)),
            width.ToString(CultureInfo.InvariantCulture), height.ToString(CultureInfo.InvariantCulture),
            outBytes.ToString(CultureInfo.InvariantCulture), status,
            (metadata?.Exif.Length ?? 0).ToString(CultureInfo.InvariantCulture),
            (metadata?.Xmp.Length ?? 0).ToString(CultureInfo.InvariantCulture),
            (metadata?.Icc.Length ?? 0).ToString(CultureInfo.InvariantCulture),
            Csv(note ?? "")
        });

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>同名一律避让：并行转换时若两个输入同名，绝不并发写同一个文件。</summary>
    private static string Reserve(string path, ConcurrentDictionary<string, byte> claimed)
    {
        string candidate = path;
        string dir = Path.GetDirectoryName(path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            if (claimed.TryAdd(candidate, 0)) return candidate;
            candidate = Path.Combine(dir, $"{stem}-{i}{ext}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 清理失败不影响主流程 */ }
    }

    public static string Summarize(IReadOnlyList<BatchItemResult> results)
    {
        int ok = results.Count(r => r.Ok);
        int skipped = results.Count(r => r.Skipped);
        int bad = results.Count - ok - skipped;
        long before = results.Where(r => r.Ok).Sum(r => r.InputBytes);
        long after = results.Where(r => r.Ok).Sum(r => r.OutputBytes);
        double saved = before > 0 ? 100.0 * (before - after) / before : 0;
        double ms = results.Where(r => r.Ok).Sum(r => r.Ms);
        string skipText = skipped > 0 ? $"，跳过 {skipped} 项" : "";
        return $"完成 {ok} 项，失败 {bad} 项{skipText} · {before / 1024.0:F0} KB → " +
               $"{after / 1024.0:F0} KB（{(saved >= 0 ? "省" : "增")} {Math.Abs(saved):F1}%）· " +
               $"累计解码+编码 {ms:F0} ms";
    }
}
