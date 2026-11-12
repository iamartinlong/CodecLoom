using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Document;

namespace SimdCodecsViewer;

/// <summary>
/// 无 GUI 的文档智能批处理：逐张串行（上游不支持 N>1 批量推理）。
/// 用法：--document &lt;输入目录&gt; &lt;输出目录&gt; [ocr|enhance|both] [compare]
///   ocr      → 每图写出 &lt;stem&gt;.txt（识别正文）
///   enhance  → 每图写出 &lt;stem&gt;-enhanced.png
///   both     → 两者都写（默认），compare 时额外在控制台打印增强前后行数 / 字符数对比
/// 所有产物落在一个 _document_manifest.csv 里，与 --batch 的清单风格一致。
/// </summary>
public static class DocumentCli
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string input = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string output = args.Length > 1 ? args[1] : Path.Combine(input, "document-out");
        string mode = (args.Length > 2 ? args[2] : "both").ToLowerInvariant();
        bool compare = args.Length > 3 && args[3].Equals("compare", StringComparison.OrdinalIgnoreCase);

        if (mode is not ("ocr" or "enhance" or "both"))
        {
            Console.WriteLine($"未知模式 '{mode}'，可用 ocr / enhance / both。");
            return 2;
        }
        if (!Directory.Exists(input))
        {
            Console.WriteLine($"输入目录不存在：{input}");
            return 2;
        }
        string[] files = Directory.GetFiles(input)
            .Where(p => BatchCli.Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine($"目录里没有可处理的图片：{input}");
            return 2;
        }
        Directory.CreateDirectory(output);
        // 与 --batch / --selftest 一致：HEIC 解码器需要显式注册后才能进这条链路。
        CodecService.EnsureHeif();

        bool wantEnhance = mode is "enhance" or "both";
        bool wantOcr = mode is "ocr" or "both";
        using var runtime = new DocumentRuntime(DocumentOptions.ForLocal());
        var manifest = new StringBuilder();
        manifest.AppendLine("input,mode,width,height,enhanced,lines,chars,elapsedMs,status");
        int ok = 0, failed = 0, skipped = 0;

        Console.WriteLine($"文档智能：{files.Length} 个文件 · 模式 {mode} · CPU / tiny → {output}");
        foreach (string path in files)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            try
            {
                LoadedImage image = CodecService.Load(path);
                DocumentLimits.Verdict verdict = runtime.Limits.Validate(
                    image.Width, image.Height, image.FrameCount);
                if (!verdict.Accepted)
                {
                    skipped++;
                    Console.WriteLine($"  [SKIP] {stem}: HTTP {verdict.Status} {verdict.Reason}");
                    manifest.AppendLine($"{Csv(stem)},{mode},{image.Width},{image.Height},0,0,0,0,REJECT");
                    continue;
                }

                var frame = new DocumentFrame(image.Pixels, image.Width, image.Height,
                    image.Stride, image.FrameCount);
                var request = new DocumentRequest(
                    Enhance: wantEnhance, Ocr: wantOcr,
                    Compare: compare && wantOcr && wantEnhance, Ctc: false);
                DocumentResult r = runtime.RunAsync(frame, request).GetAwaiter().GetResult();

                if (r.EnhancedPixels is not null)
                {
                    string target = Path.Combine(output, stem + "-enhanced.png");
                    CodecService.Write(r.EnhancedPixels, r.Width, r.Height, target, ImageFormat.Png);
                }
                if (r.Ocr is not null)
                    File.WriteAllText(Path.Combine(output, stem + ".txt"), r.Ocr.Text, Encoding.UTF8);

                int lines = r.Ocr?.LineCount ?? 0;
                int chars = r.Ocr?.CharCount ?? 0;
                string extra = r.OriginalOcr is { } before
                    ? $" · 对比 {before.LineCount}行/{before.CharCount}字符 → {lines}行/{chars}字符" : "";
                Console.WriteLine($"  [OK] {stem}: {image.Width}×{image.Height} · " +
                                  $"增强 {r.Enhance?.ElapsedMs ?? 0:F0} ms + OCR {r.Ocr?.ElapsedMs ?? 0:F0} ms" +
                                  $" · {lines} 行 / {chars} 字符{extra}");
                manifest.AppendLine(string.Join(",",
                    Csv(stem), mode,
                    image.Width.ToString(CultureInfo.InvariantCulture),
                    image.Height.ToString(CultureInfo.InvariantCulture),
                    r.EnhancedPixels is null ? "0" : "1",
                    lines.ToString(CultureInfo.InvariantCulture),
                    chars.ToString(CultureInfo.InvariantCulture),
                    r.TotalMs.ToString("F2", CultureInfo.InvariantCulture), "OK"));
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  [FAIL] {stem}: {ex.GetType().Name}: {ex.Message}");
                manifest.AppendLine($"{Csv(stem)},{mode},0,0,0,0,0,0,FAIL");
            }
        }

        File.WriteAllText(Path.Combine(output, "_document_manifest.csv"),
            manifest.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"完成：ok={ok} failed={failed} skipped={skipped} · " +
                          $"清单 {Path.Combine(output, "_document_manifest.csv")}");
        return failed > 0 ? 1 : 0;
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
