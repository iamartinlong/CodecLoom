using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Core;
using Xunit;

namespace SimdCodecs.Tests;

public class BatchTests
{
    private const int W = 160, H = 120;

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "simdcodecs-tests-" + Guid.NewGuid().ToString("n")[..8]);

        public TempDir() => Directory.CreateDirectory(Root);

        public string Dir(string name)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 临时目录清理失败不影响测试结论 */ }
        }
    }

    private static string PngFile(string dir, string name, byte b = 40)
    {
        byte[] px = TestData.Solid(W, H, b, 80, 120);
        byte[] data = CodecService.EncodeBytes(px, W, H, ImageFormat.Png);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void 预设注册表有四个且_ID_唯一()
    {
        Assert.Equal(4, BatchPresets.All.Count);
        Assert.Equal(4, BatchPresets.All.Select(p => p.Id).Distinct().Count());
        Assert.Contains(BatchPresets.All, p => p.Id == "heic-jpeg");
        Assert.Contains(BatchPresets.All, p => p.Id == "scan-split");
    }

    [Fact]
    public void Find_支持大小写_ID_与标签()
    {
        Assert.Same(BatchPresets.HeicToJpeg, BatchPresets.Find("HEIC-JPEG"));
        Assert.Same(BatchPresets.HeicToJpeg, BatchPresets.Find(BatchPresets.HeicToJpeg.Label));
        Assert.Null(BatchPresets.Find("nope"));
        Assert.Null(BatchPresets.Find(null));
    }

    [Fact]
    public void Accepts_按源格式过滤()
    {
        Assert.True(BatchPresets.HeicToJpeg.Accepts(ImageFormat.Heif));
        Assert.False(BatchPresets.HeicToJpeg.Accepts(ImageFormat.Png));
        Assert.True(BatchPresets.WebOptimize.Accepts(ImageFormat.Png));
        Assert.Equal("Heif", BatchPresets.HeicToJpeg.SourceHint);
        Assert.Equal("全部格式", BatchPresets.WebOptimize.SourceHint);
        Assert.True(BatchPresets.ScanSplit.AllFrames);
        Assert.True(BatchPresets.ScanSplit.PageNumbering);
        Assert.True(BatchPresets.ScanSplit.MultiFrameOnly);
    }

    [Fact]
    public void Apply_填充预设字段但不改变输出目录()
    {
        var settings = new BatchSettings("out", CodecService.Target("PNG"), Quality: 30);
        BatchSettings applied = BatchPresets.Apply(BatchPresets.HeicToJpeg, settings);
        Assert.Equal("out", applied.OutputDirectory);
        Assert.Equal("heic-jpeg", applied.PresetId);
        Assert.Equal(ImageFormat.Jpeg, applied.Target.Format);
        Assert.Equal(88, applied.Quality);
        Assert.NotNull(applied.SourceFilter);
        Assert.Contains(ImageFormat.Heif, applied.SourceFilter!);
        Assert.Equal(30, settings.Quality); // 原 record 不可变
    }

    [Fact]
    public async Task 批量转码产出可回读文件与清单()
    {
        using var tmp = new TempDir();
        string input = tmp.Dir("in"), output = tmp.Dir("out");
        string[] files = { PngFile(input, "a.png"), PngFile(input, "b.png", 90), PngFile(input, "c.png", 160) };

        IReadOnlyList<BatchItemResult> results = await BatchService.RunAsync(files,
            new BatchSettings(output, CodecService.Target("WebP"), Quality: 88, MaxConcurrency: 2));

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        foreach (BatchItemResult r in results)
        {
            Assert.True(File.Exists(r.Output));
            Assert.Equal(".webp", Path.GetExtension(r.Output));
            Assert.Equal(ImageFormat.Webp, CodecService.Inspect(r.Output).Format);
            Assert.Equal(W, CodecService.Load(r.Output).Width);
        }
        string manifest = Path.Combine(output, BatchService.ManifestFileName);
        Assert.True(File.Exists(manifest));
        string csv = await File.ReadAllTextAsync(manifest);
        Assert.Contains("sourceFormat,page", csv);
        Assert.Contains(",OK,", csv);
        Assert.Contains("完成 3 项", BatchService.Summarize(results));
    }

    [Fact]
    public async Task 预设过滤产生_Skipped_而不是失败()
    {
        using var tmp = new TempDir();
        string input = tmp.Dir("in"), output = tmp.Dir("out");
        string[] files = { PngFile(input, "phone.png"), PngFile(input, "camera.png", 70) };
        var settings = BatchPresets.Apply(BatchPresets.HeicToJpeg,
            new BatchSettings(output, CodecService.Target("PNG")));

        IReadOnlyList<BatchItemResult> results = await BatchService.RunAsync(files, settings);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Skipped));
        Assert.DoesNotContain(results, r => r.Ok);
        Assert.Contains("跳过 2 项", BatchService.Summarize(results));
        Assert.False(File.Exists(Path.Combine(output, BatchService.ManifestFileName)));
    }

    [Fact]
    public async Task 单个损坏文件不影响其他文件()
    {
        using var tmp = new TempDir();
        string input = tmp.Dir("in"), output = tmp.Dir("out");
        string broken = Path.Combine(input, "broken.png");
        await File.WriteAllTextAsync(broken, "definitely broken");
        string[] files = { PngFile(input, "good.png"), broken };

        IReadOnlyList<BatchItemResult> results = await BatchService.RunAsync(files,
            new BatchSettings(output, CodecService.Target("PNG"), MaxConcurrency: 1));

        Assert.Equal(2, results.Count);
        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => r.Error is not null && !r.Skipped);
    }

    [Fact]
    public async Task 取消后不写清单也不产出成功项()
    {
        using var tmp = new TempDir();
        string input = tmp.Dir("in"), output = tmp.Dir("out");
        string[] files = { PngFile(input, "a.png"), PngFile(input, "b.png", 70) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        IReadOnlyList<BatchItemResult> results = await BatchService.RunAsync(files,
            new BatchSettings(output, CodecService.Target("PNG")), ct: cts.Token);

        Assert.DoesNotContain(results, r => r.Ok);
        Assert.False(File.Exists(Path.Combine(output, BatchService.ManifestFileName)));
    }

    [Fact]
    public async Task 同名输入自动避让输出文件()
    {
        using var tmp = new TempDir();
        string a = tmp.Dir("a"), b = tmp.Dir("b"), output = tmp.Dir("out");
        string[] files = { PngFile(a, "same.png"), PngFile(b, "same.png", 150) };

        IReadOnlyList<BatchItemResult> results = await BatchService.RunAsync(files,
            new BatchSettings(output, CodecService.Target("PNG"), MaxConcurrency: 2));

        Assert.Equal(2, results.Count(r => r.Ok));
        Assert.Equal(2, results.Select(r => r.Output).Distinct().Count());
        Assert.All(results, r => Assert.True(File.Exists(r.Output)));
    }

    [Fact]
    public async Task 真实三页_TIFF_按页码拆页并生成清单()
    {
        string? source = TestData.Find("multipage-pillow.tiff");
        if (source is null)
        {
            Console.WriteLine("  [SKIP] testdata/multipage-pillow.tiff 不存在");
            return;
        }
        using var tmp = new TempDir();
        string output = tmp.Dir("out");
        var settings = BatchPresets.Apply(BatchPresets.ScanSplit,
            new BatchSettings(output, CodecService.Target("PNG")));

        IReadOnlyList<BatchItemResult> results =
            await BatchService.RunAsync(new[] { source }, settings);

        Assert.Equal(3, results.Count(r => r.Ok));
        foreach (int page in new[] { 1, 2, 3 })
        {
            string file = Path.Combine(output, $"multipage-pillow-p{page:000}.png");
            Assert.True(File.Exists(file), $"缺少 {file}");
            Assert.Equal(ImageFormat.Png, CodecService.Inspect(file).Format);
        }
        string csv = await File.ReadAllTextAsync(Path.Combine(output, BatchService.ManifestFileName));
        Assert.Contains("Tiff", csv);
        Assert.Contains("presetManifest=v1", csv);
    }

    [Fact]
    public void 结果统计字段计算正确()
    {
        var ok = new BatchItemResult("in", "out", 1000, 500, 12);
        Assert.True(ok.Ok);
        Assert.Equal(50, ok.SavedPercent);
        var skipped = new BatchItemResult("in", "", 100, 0, 0, "skip", Skipped: true);
        Assert.False(skipped.Ok);
        Assert.Equal(0, skipped.SavedPercent);
        var failed = new BatchItemResult("in", "", 100, 0, 0, "boom");
        Assert.False(failed.Ok);
    }
}
