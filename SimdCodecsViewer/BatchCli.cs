using System.Text;

namespace SimdCodecsViewer;

/// <summary>
/// 无 GUI 的批量转码入口。与窗口 / API 共用 BatchService，证明核心层可以原样搬去服务端 / CI。
/// 用法：--batch &lt;输入目录&gt; &lt;输出目录&gt; [格式或预设 ID] [逐帧]
/// 预设：heic-jpeg / heic-png / scan-split / web-optimize（会自动过滤源格式、拆页并写 _manifest.csv）
/// </summary>
public static class BatchCli
{
    // 文档智能 CLI 复用同一份扩展名白名单，避免两处漂移。
    internal static readonly string[] Extensions =
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".ico", ".heic", ".heif",
        ".tif", ".tiff", ".jp2", ".j2k", ".jbig2", ".qoi", ".ppm", ".pgm", ".pbm", ".apng"
    };

    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string input = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string output = args.Length > 1 ? args[1] : Path.Combine(input, "converted");
        string formatOrPreset = args.Length > 2 ? args[2] : "WebP";
        bool allFrames = args.Length > 3 && args[3].Equals("逐帧", StringComparison.OrdinalIgnoreCase);
        BatchPreset? preset = BatchPresets.Find(formatOrPreset);

        if (!Directory.Exists(input))
        {
            Console.WriteLine($"输入目录不存在：{input}");
            return 2;
        }

        string[] files = Directory.GetFiles(input)
            .Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine($"目录里没有可处理的图片：{input}");
            return 2;
        }

        var settings = new BatchSettings(output, CodecService.Target(formatOrPreset),
            Quality: 88, AllFrames: allFrames);
        if (preset is not null)
            settings = BatchPresets.Apply(preset, settings) with
            {
                AllFrames = preset.AllFrames || allFrames
            };
        Console.WriteLine($"批量转换：{files.Length} 个文件 · " +
                          (preset is not null ? $"预设「{preset.Label}」" : formatOrPreset) +
                          $" -> {output}" + (settings.AllFrames ? " · 多帧逐帧导出" : "") +
                          (settings.SourceFilter is { } f ? $" · 只处理 {string.Join("/", f)}" : ""));

        int last = -1;
        var progress = new Progress<BatchProgress>(p =>
        {
            if (p.Done == last) return;
            last = p.Done;
            string state = p.Last is null ? ""
                : p.Last.Ok ? "OK"
                : p.Last.Skipped ? "SKIP " + p.Last.Error
                : "FAIL " + p.Last.Error;
            Console.WriteLine($"  [{p.Done}/{p.Total}] {p.Current} {state}");
        });

        IReadOnlyList<BatchItemResult> results = BatchService.RunAsync(files, settings, progress)
            .GetAwaiter().GetResult();
        Console.WriteLine(BatchService.Summarize(results));
        foreach (BatchItemResult r in results.Where(r => !r.Ok && !r.Skipped))
            Console.WriteLine($"  失败 {Path.GetFileName(r.Input)}: {r.Error}");
        // 预设按源格式跳过是预期行为，不能算失败退出码。
        return results.Any(r => !r.Ok && !r.Skipped) ? 1 : 0;
    }
}
