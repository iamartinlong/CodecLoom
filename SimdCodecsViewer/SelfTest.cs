using System.Buffers;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Processing;

namespace SimdCodecsViewer;

/// <summary>不启动 GUI 也能跑的端到端自检：编码 -> 解码 -> 缩放 -> 裁剪 -> 多帧 -> 导出 -> 批量 -> 回读。</summary>
public static class SelfTest
{
    public static int Run(string? outputDir)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string dir = outputDir ?? Path.Combine(AppContext.BaseDirectory, "selftest-output");
        Directory.CreateDirectory(dir);
        int failures = 0;

        void Check(bool ok, string label)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok) failures++;
        }

        try
        {
            Console.WriteLine($"SimdCodecs 自检，输出目录：{dir}");
            CodecService.EnsureHeif();
            Console.WriteLine("HEIF 解码器显式注册完成（Sdcb.SimdCodecs.Heif）");
            Check(ImageCodecs.IsDecodable(ImageFormat.Heif), "HEIF/HEIC 解码器注册成功");
            Check(!ImageCodecs.IsDecodable(ImageFormat.Avif), "AVIF 按预期只识别、不解码");

            const int w = 320, h = 240;
            byte[] pixels = MakeGradient(w, h);

            Console.WriteLine("1) 编码三种格式");
            string png = Path.Combine(dir, "gradient.png");
            string jpg = Path.Combine(dir, "gradient.jpg");
            string webp = Path.Combine(dir, "gradient.webp");
            CodecService.Write(pixels, w, h, png, ImageFormat.Png);
            CodecService.Write(pixels, w, h, jpg, ImageFormat.Jpeg, quality: 92);
            CodecService.Write(pixels, w, h, webp, ImageFormat.Webp, quality: 90);
            foreach (string p in new[] { png, jpg, webp })
                Check(new FileInfo(p).Length > 0, $"{Path.GetFileName(p)} 写出 ({new FileInfo(p).Length} B)");

            Console.WriteLine("2) 解码回 Bgra32");
            foreach (string p in new[] { png, jpg, webp })
            {
                LoadedImage image = CodecService.Load(p);
                bool nonBlank = false;
                for (int i = 0; i < image.Pixels.Length; i += 997)
                    if (image.Pixels[i] != 0) { nonBlank = true; break; }
                Check(image.Width == w && image.Height == h && image.Stride == w * 4 && nonBlank,
                    $"{Path.GetFileName(p)} -> {image.Width}x{image.Height}, stride={image.Stride}, " +
                    $"{image.DecodeMs:F2} ms, format={image.Info.Format}");
            }

            Console.WriteLine("3) 解码期 1/4 缩放");
            LoadedImage quarter = CodecService.Load(png, scale: 4);
            Check(quarter.Width == w / 4 && quarter.Height == h / 4,
                $"320x240 -> {quarter.Width}x{quarter.Height}，{quarter.DecodeMs:F2} ms");

            Console.WriteLine("4) 另存为（含量化 PNG 与元数据透传）");
            LoadedImage src = CodecService.Load(png);
            string qPng = Path.Combine(dir, "quantized.png");
            string outJpg = Path.Combine(dir, "converted.jpg");
            string outWebp = Path.Combine(dir, "converted-lossless.webp");
            CodecService.Save(src, qPng, ImageFormat.Png, quantizePng: true);
            CodecService.Save(src, outJpg, ImageFormat.Jpeg, quality: 85);
            CodecService.Save(src, outWebp, ImageFormat.Webp, losslessWebp: true);
            Check(HasPaletteChunk(qPng), "量化 PNG 写出 PLTE 调色板块（pngquant 模式生效）");
            Console.WriteLine($"       注：平滑渐变 无损 {new FileInfo(png).Length} B / 量化 {new FileInfo(qPng).Length} B，" +
                              "这类图量化反而变大，属正常行为");
            foreach (string p in new[] { qPng, outJpg, outWebp })
            {
                LoadedImage back = CodecService.Load(p);
                Check(back.Width == w && back.Height == h,
                    $"{Path.GetFileName(p)} 可回读 ({back.Width}x{back.Height}, {new FileInfo(p).Length} B)");
            }

            Console.WriteLine("5) 照片类图片验证量化收益");
            const int pw = 256, ph = 256;
            byte[] photo = MakePhoto(pw, ph);
            string photoPng = Path.Combine(dir, "photo.png");
            string photoQ = Path.Combine(dir, "photo-quantized.png");
            CodecService.Write(photo, pw, ph, photoPng, ImageFormat.Png);
            CodecService.Save(CodecService.Load(photoPng), photoQ, ImageFormat.Png, quantizePng: true);
            long losslessSize = new FileInfo(photoPng).Length;
            long quantSize = new FileInfo(photoQ).Length;
            Check(quantSize < losslessSize,
                $"照片类量化 {quantSize} B < 无损 {losslessSize} B（省 {100.0 * (losslessSize - quantSize) / losslessSize:F1}%）");

            Console.WriteLine("6) 文件头预检（上传前置校验）");
            ImageEntry good = CodecService.Inspect(png);
            Check(good.Decodable && good.Width == w && good.Height == h,
                $"正常 PNG 预检：{good.Summary}");
            string broken = Path.Combine(dir, "broken.png");
            File.WriteAllText(broken, "this is definitely not a png");
            ImageEntry bad = CodecService.Inspect(broken);
            Check(!bad.Decodable && bad.Error is not null, $"损坏文件被拦下：{bad.Error}");

            Console.WriteLine("7) 解码期区域裁剪（只取这块像素）");
            var region = new PixelRect(40, 30, 120, 90);
            LoadedImage crop = CodecService.Load(png, region: region);
            Check(crop.Width == 120 && crop.Height == 90,
                $"裁剪输出 {crop.Width}x{crop.Height}，{crop.DecodeMs:F2} ms");
            Check(RegionMatches(src, crop, 40, 30), "裁剪像素与整图对应区域逐字节一致");
            LoadedImage scaledCrop = CodecService.Load(png, scale: 2,
                region: new PixelRect(10, 10, 50, 40));
            Check(scaledCrop.Width == 50 && scaledCrop.Height == 40,
                $"1/2 缩放后裁剪 {scaledCrop.Width}x{scaledCrop.Height}（region 用的是缩放后坐标）");

            Console.WriteLine("8) 多帧动画：生成 3 帧 GIF 再逐帧解码");
            string gif = Path.Combine(dir, "anim.gif");
            var writer = new ArrayBufferWriter<byte>();
            IImageEncoderSession session = ImageCodecs.CreateEncoder(ImageFormat.Gif, writer);
            const int fw = 64, fh = 48;
            for (int f = 0; f < 3; f++)
            {
                byte[] fp = new byte[fw * fh * 4];
                for (int i = 0; i < fw * fh; i++)
                {
                    fp[i * 4] = (byte)(30 + f * 90);
                    fp[i * 4 + 1] = (byte)(60 + f * 60);
                    fp[i * 4 + 2] = (byte)(200 - f * 80);
                    fp[i * 4 + 3] = 255;
                }
                IImageFrameEncoder frame = session.AddFrame(fw, fh, PixelFormat.Bgra32,
                    duration: TimeSpan.FromMilliseconds(120));
                frame.WriteRows(fp, fh);
            }
            session.Commit();
            File.WriteAllBytes(gif, writer.WrittenSpan.ToArray());
            Check(new FileInfo(gif).Length > 0, $"3 帧 GIF 写出 ({new FileInfo(gif).Length} B)");

            using (ImageDecoder decoder = ImageDecoder.Open(new ReadOnlyMemory<byte>(File.ReadAllBytes(gif))))
                Check(decoder.FrameCount == 3, $"ImageDecoder 会话报告 FrameCount={decoder.FrameCount}");

            LoadedImage f0 = CodecService.Load(gif);
            LoadedImage f1 = CodecService.Load(gif, frameIndex: 1);
            LoadedImage f2 = CodecService.Load(gif, frameIndex: 2);
            Check(f1.FrameIndex == 1 && f1.FrameCount == 3 && f1.Width == fw,
                $"第 2 帧解码成功：{f1.Width}x{f1.Height}，frame={f1.FrameIndex + 1}/{f1.FrameCount}");
            Check(f1.FrameDuration is { } d && Math.Abs(d.TotalMilliseconds - 120) < 1,
                $"帧时长可读：{f1.FrameDuration}");
            Check(!f0.Pixels.SequenceEqual(f1.Pixels) && !f1.Pixels.SequenceEqual(f2.Pixels),
                "三帧像素互不相同（动画画布状态正确）");

            Console.WriteLine("9) 缩略图（内嵌优先 / 1-8 解码 + 最近邻缩小）");
            (byte[] thumb, int tw, int th, int ts) = CodecService.LoadThumbnail(png, 64);
            Check(tw <= 64 && th <= 64 && thumb.Length == ts * th && ts == tw * 4,
                $"缩略图 {tw}x{th}（{thumb.Length} B，目标框 64）");

            Console.WriteLine("10) 批量转换（并行 / 多帧拆帧 / 错误隔离 / 取消）");
            string batchDir = Path.Combine(dir, "batch");
            var settings = new BatchSettings(batchDir, CodecService.Target("WebP"),
                Quality: 80, MaxConcurrency: 2, AllFrames: true);
            string[] inputs = { png, jpg, webp, gif, broken };
            IReadOnlyList<BatchItemResult> results = BatchService.RunAsync(inputs, settings)
                .GetAwaiter().GetResult();
            int ok = results.Count(r => r.Ok);
            Check(ok == 6, $"3 张静态图 + 3 帧动画 = 6 个输出（实际 {ok}）");
            Check(results.Any(r => !r.Ok), "损坏文件单独记为失败，不影响其他文件");
            Check(Directory.GetFiles(batchDir, "*.webp").Length == ok,
                $"输出名自动避让，无覆盖：{Directory.GetFiles(batchDir, "*.webp").Length} 个文件");
            Check(BatchService.Summarize(results).Contains("完成"),
                "批量汇总可生成：" + BatchService.Summarize(results));

            string cancelDir = Path.Combine(dir, "batch-cancel");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            IReadOnlyList<BatchItemResult> cancelled = BatchService.RunAsync(
                new[] { png }, new BatchSettings(cancelDir, CodecService.Target("JPEG")),
                null, cts.Token).GetAwaiter().GetResult();
            Check(cancelled.Count == 1 && cancelled.All(r => !r.Ok) &&
                  !Directory.GetFiles(cancelDir).Any(p => new FileInfo(p).Length > 0),
                "取消令牌生效，不留下半截输出文件");

            Console.WriteLine("11) 真实 HEIC 实图端到端（libheif / Nokia 一致性样本）");
            string? heic = FindTestData("example.heic");
            string? heic2 = FindTestData("C002.heic");
            if (heic is null || heic2 is null)
            {
                Console.WriteLine("  [SKIP] 未找到 testdata/*.heic（脱离源码仓库运行时属正常）");
            }
            else
            {
                foreach (string p in new[] { heic, heic2 })
                {
                    string name = Path.GetFileName(p);
                    byte[] bytes = File.ReadAllBytes(p);
                    ImageEntry head = CodecService.Inspect(bytes, name);
                    Check(head.Format == ImageFormat.Heif && head.Decodable && head.Width > 0,
                        $"{name} 头部预检：{head.Format} {head.Width}x{head.Height}，{bytes.Length} B");

                    LoadedImage full = CodecService.Load(bytes, name);
                    Check(full.Width == head.Width && full.Height == head.Height &&
                          full.Pixels.Length == full.Stride * full.Height && HasVariance(full.Pixels),
                        $"{name} 解码为 Bgra32：{full.Width}x{full.Height}，{full.DecodeMs:F1} ms，像素有内容");

                    byte[] jpeg = CodecService.SaveToBytes(full, CodecService.Target("JPEG"), 85);
                    byte[] pngOut = CodecService.SaveToBytes(full, CodecService.Target("PNG"));
                    LoadedImage backJ = CodecService.Load(jpeg, name + ".jpg");
                    LoadedImage backP = CodecService.Load(pngOut, name + ".png");
                    Check(jpeg.Length > 0 && pngOut.Length > 0 &&
                          backJ.Width == full.Width && backJ.Height == full.Height &&
                          backP.Width == full.Width && backP.Height == full.Height,
                        $"{name} 转 JPEG {jpeg.Length} B / PNG {pngOut.Length} B，两者均可回读");

                    (byte[] tp, int tW, int tH, int tS) = CodecService.LoadThumbnail(bytes, 96);
                    Check(tW <= 96 && tH <= 96 && tp.Length == tS * tH,
                        $"{name} 缩略图 {tW}x{tH}（内嵌缩略图优先）");
                }

                string preview = Path.Combine(dir, "heic-preview.jpg");
                CodecService.Save(CodecService.Load(heic), preview,
                    CodecService.Target("JPEG"), 85);
                Check(new FileInfo(preview).Length > 0,
                    "HEIC 预览图 heic-preview.jpg 已写出，可人工核对画面");
            }

            Console.WriteLine("12) ICodecService 抽象层");
            ICodecService svc = CodecServices.Current;
            Check(svc is SimdCodecService && svc.HeifEnabled,
                "当前注册实现为 SimdCodecService，HEIF 已就绪");
            ImageEntry viaInterface = svc.Inspect(File.ReadAllBytes(png), "via-interface.png");
            LoadedImage viaLoad = svc.Load(File.ReadAllBytes(png), "via-interface.png", scale: 2);
            Check(viaInterface.Decodable && viaInterface.Width == w && viaInterface.Height == h &&
                  viaLoad.Width == w / 2 && viaLoad.Height == h / 2 &&
                  svc.Targets.Count == CodecService.Targets.Count,
                $"经接口 Inspect / Load / Targets 与静态门面一致：{viaInterface.Summary} → 1/2 缩放 {viaLoad.Width}x{viaLoad.Height}");

            Console.WriteLine("13) 一键预设：HEIC 批量转 JPEG（源格式过滤 + 转换清单）");
            string? heicOne = FindTestData("example.heic");
            string? multipage = FindTestData("multipage-pillow.tiff");
            if (heicOne is null || multipage is null)
            {
                Console.WriteLine("  [SKIP] 未找到 testdata/example.heic 或 multipage-pillow.tiff");
            }
            else
            {
                string presetDir = Path.Combine(dir, "preset-heic");
                if (Directory.Exists(presetDir)) Directory.Delete(presetDir, true);
                BatchSettings heicSettings = BatchPresets.Apply(BatchPresets.HeicToJpeg,
                    new BatchSettings(presetDir, CodecService.Target("PNG")));
                Check(heicSettings.Target.Label == "JPEG" && heicSettings.PresetId == "heic-jpeg" &&
                      heicSettings.SourceFilter!.Contains(ImageFormat.Heif),
                    "预设覆盖了目标格式 / 质量 / 源格式过滤，并记录 PresetId");

                IReadOnlyList<BatchItemResult> heicBatch = BatchService.RunAsync(
                    new[] { heicOne, png, multipage }, heicSettings).GetAwaiter().GetResult();
                int heicOk = heicBatch.Count(r => r.Ok);
                int heicSkipped = heicBatch.Count(r => r.Skipped);
                string[] jpgOut = Directory.GetFiles(presetDir, "*.jpg");
                Check(heicOk == 1 && heicSkipped == 2 && jpgOut.Length == 1,
                    $"混合队列 3 个文件：1 个 HEIC 转出、2 个非 HEIC 跳过（实际 ok={heicOk} skip={heicSkipped}）");
                Check(jpgOut.Length == 1 && CodecService.Load(jpgOut[0]).Width ==
                      CodecService.Inspect(heicOne).Width,
                    "HEIC → JPEG 输出可回读，宽度与原图一致");
                string manifestPath = Path.Combine(presetDir, BatchService.ManifestFileName);
                string manifest = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : "";
                Check(manifest.Contains("example.heic") && manifest.Contains("SKIP") &&
                      manifest.Contains("status"),
                    "_manifest.csv 记录输入 / 源格式 / 状态，含被跳过项");

                Console.WriteLine("14) 扫描件拆页：真实 3 页 TIFF → 页码命名 PNG");
                string splitDir = Path.Combine(dir, "preset-split");
                if (Directory.Exists(splitDir)) Directory.Delete(splitDir, true);
                BatchSettings splitSettings = BatchPresets.Apply(BatchPresets.ScanSplit,
                    new BatchSettings(splitDir, CodecService.Target("JPEG")));
                IReadOnlyList<BatchItemResult> split = BatchService.RunAsync(
                    new[] { multipage, png }, splitSettings).GetAwaiter().GetResult();
                string[] pages = Directory.GetFiles(splitDir, "*.png")
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                Check(split.Count(r => r.Ok) == 3 && pages.Length == 3 &&
                      Path.GetFileName(pages[0]).EndsWith("-p001.png", StringComparison.Ordinal) &&
                      Path.GetFileName(pages[2]).EndsWith("-p003.png", StringComparison.Ordinal),
                    $"3 页 TIFF 拆成 -p001/-p002/-p003.png，单帧 PNG 被跳过（实际 {pages.Length} 页）");
                Check(pages.Length == 3 && pages.All(p => CodecService.Load(p).Width > 0),
                    "拆出的每一页 PNG 都能被 SimdCodecs 回读");
                string splitManifest = File.ReadAllText(
                    Path.Combine(splitDir, BatchService.ManifestFileName));
                Check(splitManifest.Contains("-p003.png") && splitManifest.Contains(",Tiff,3,"),
                    "转换清单含第 3 页与 Tiff 源格式、页码列，可对账");
            }

            Console.WriteLine("15) 纯托管处理层与 OCR / CV 输入");
            ProcessResult resized = ImageProcessor.Apply(pixels, w, h,
                new ProcessOptions(ResizeWidth: 137, ResizeHeight: 91));
            Check(resized.Width == 137 && resized.Height == 91 &&
                  resized.Pixels.Length == 137 * 91 * 4 && HasVariance(resized.Pixels),
                "任意尺寸双线性缩放 320×240 → 137×91，像素有内容");

            ProcessResult gray = ImageProcessor.Apply(pixels, w, h,
                new ProcessOptions(Grayscale: true, Layout: PixelLayout.Gray8));
            byte[] grayDisplay = ImageProcessor.ToDisplayBgra(gray);
            bool allGray = true;
            for (int i = 0; i < grayDisplay.Length; i += 4)
                if (grayDisplay[i] != grayDisplay[i + 1] || grayDisplay[i + 1] != grayDisplay[i + 2])
                { allGray = false; break; }
            Check(gray.Pixels.Length == w * h && gray.Stride == w && allGray,
                "Gray8 布局为 w×h 连续字节，转回 Bgra32 后 R=G=B");

            ProcessResult bgr = ImageProcessor.Apply(pixels, w, h,
                new ProcessOptions(Layout: PixelLayout.Bgr24));
            Check(bgr.Pixels.Length == w * h * 3 && bgr.Stride == w * 3 &&
                  bgr.Pixels[0] == pixels[0] && bgr.Pixels[1] == pixels[1] &&
                  bgr.Pixels[2] == pixels[2] &&
                  ImageOps.ToBgra32(bgr.Pixels, PixelLayout.Bgr24, w, h).SequenceEqual(pixels),
                "Bgr24 字节序与 OpenCV CV_8UC3 一致，往返 Bgra32 逐字节无损");

            byte[] flat = new byte[16 * 16 * 4];
            for (int i = 0; i < flat.Length; i += 4)
            { flat[i] = flat[i + 1] = flat[i + 2] = 128; flat[i + 3] = 255; }
            ProcessResult bright = ImageProcessor.Apply(flat, 16, 16,
                new ProcessOptions(Brightness: 20));
            ProcessResult inverted = ImageProcessor.Apply(flat, 16, 16,
                new ProcessOptions(Invert: true));
            Check(bright.Pixels[0] > 128 && bright.Pixels[0] < 255 &&
                  Math.Abs(inverted.Pixels[0] - 127) <= 1,
                $"亮度 / 反色滤镜生效：128 → 亮度 {bright.Pixels[0]} / 反色 {inverted.Pixels[0]}（理论 179 / 127）");

            ProcessResult markA = ImageProcessor.Apply(pixels, w, h,
                new ProcessOptions(Watermark: "DEMO-A"));
            ProcessResult markB = ImageProcessor.Apply(pixels, w, h,
                new ProcessOptions(Watermark: "DEMO-B"));
            Check(!markA.Pixels.SequenceEqual(pixels) && !markA.Pixels.SequenceEqual(markB.Pixels),
                "几何水印改变像素，且不同文本产生不同图案（FNV 相位）");

            // 真实下游算子：白底 + 3 条黑色文本带 → Gray8 → Otsu → 行投影应检出 3 行。
            const int dw = 120, dh = 80;
            byte[] doc = new byte[dw * dh * 4];
            for (int i = 0; i < doc.Length; i += 4)
            { doc[i] = doc[i + 1] = doc[i + 2] = 255; doc[i + 3] = 255; }
            foreach (int bandTop in new[] { 15, 35, 55 })
                for (int y = bandTop; y < bandTop + 6; y++)
                    for (int x = 10; x < 110; x++)
                    {
                        int i = (y * dw + x) * 4;
                        doc[i] = doc[i + 1] = doc[i + 2] = 0;
                    }
            byte[] docGray = ImageOps.ToGray8(doc, dw, dh);
            CvAnalysis cv = CvInput.Analyze(docGray, dw, dh);
            CvAnalysis cvViaBgr = ImageProcessor.AnalyzeForOcr(
                ImageProcessor.Apply(doc, dw, dh, new ProcessOptions(Layout: PixelLayout.Bgr24)));
            Check(cv.TextRows == 3 && cv.InkRatio > 0 && cv.InkRatio < 0.5 &&
                  cvViaBgr.TextRows == 3,
                $"Otsu 二值化 + 行投影检出 {cv.TextRows} 个文本行（阈值 {cv.Threshold}，墨迹 {cv.InkRatio:P1}），Bgr24 布局结果一致");

            Console.WriteLine("13) 设置持久化 + 本地化");
            string tempSettings = Path.Combine(dir, "selftest-settings.json");
            using (SettingsService.UseFileForTest(tempSettings))
            {
                var written = new AppSettings
                {
                    Language = "en", WindowWidth = 1440.5, WindowHeight = 900,
                    OutputDir = Path.Combine(dir, "out"), ScaleIndex = 2,
                    OrientOnLoad = false, FormatIndex = 1, Quality = 85.5,
                    PresetIndex = 3, AllFrames = true, ResizeW = 1920, ResizeH = 0,
                    Watermark = "测试水印 © 2026", Brightness = -20, Contrast = 1.75,
                    Grayscale = true, Invert = false, LayoutIndex = 2
                };
                SettingsService.Save(written);
                AppSettings read = SettingsService.Load();
                bool roundTrip = read.Language == written.Language
                    && Math.Abs(read.WindowWidth - written.WindowWidth) < 0.001
                    && Math.Abs(read.WindowHeight - written.WindowHeight) < 0.001
                    && read.OutputDir == written.OutputDir
                    && read.ScaleIndex == written.ScaleIndex
                    && read.OrientOnLoad == written.OrientOnLoad
                    && read.FormatIndex == written.FormatIndex
                    && Math.Abs(read.Quality - written.Quality) < 0.001
                    && read.PresetIndex == written.PresetIndex
                    && read.AllFrames == written.AllFrames
                    && read.ResizeW == written.ResizeW && read.ResizeH == written.ResizeH
                    && read.Watermark == written.Watermark
                    && Math.Abs(read.Brightness - written.Brightness) < 0.001
                    && Math.Abs(read.Contrast - written.Contrast) < 0.001
                    && read.Grayscale == written.Grayscale
                    && read.Invert == written.Invert
                    && read.LayoutIndex == written.LayoutIndex;
                Check(roundTrip, $"设置 JSON round-trip 18 项字段全等（{Path.GetFileName(tempSettings)}）");

                File.WriteAllText(tempSettings, "{ this is not json ");
                Check(SettingsService.Load() is { ScaleIndex: 0, Language: "zh" },
                    "损坏的设置文件回退默认值且不抛异常");
            }
            if (File.Exists(tempSettings)) File.Delete(tempSettings);

            L10n.Language = "zh";
            Check(L10n.Tr("打开图片…") == "打开图片…", "zh 语言直返原文");
            L10n.Language = "en";
            Check(L10n.Tr("打开图片…") == "Open…"
                  && L10n.Tr("不使用预设") == "No preset"
                  && L10n.Tr("未在词典里的原文") == "未在词典里的原文",
                "en 语言查表生效且未知 key 回退原文");
            L10n.Language = "zh";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"自检异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            failures++;
        }

        Console.WriteLine(failures == 0 ? "== 自检全部通过 ==" : $"== 自检失败 {failures} 项 ==");
        return failures == 0 ? 0 : 1;
    }

    internal static byte[] MakeGradient(int w, int h)
    {
        byte[] pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            pixels[i] = (byte)(255 - x * 255 / w);
            pixels[i + 1] = (byte)(y * 255 / h);
            pixels[i + 2] = (byte)(x * 255 / w);
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    internal static byte[] MakePhoto(int w, int h)
    {
        byte[] pixels = new byte[w * h * 4];
        var rnd = new Random(42);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            pixels[i] = (byte)Math.Clamp(30 + y / 3 + rnd.Next(-30, 31), 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(60 + x / 3 + rnd.Next(-30, 31), 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(120 + (x + y) / 5 + rnd.Next(-30, 31), 0, 255);
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static bool RegionMatches(LoadedImage full, LoadedImage crop, int ox, int oy)
    {
        if (crop.Width + ox > full.Width || crop.Height + oy > full.Height) return false;
        for (int y = 0; y < crop.Height; y++)
        {
            int a = y * crop.Stride;
            int b = (y + oy) * full.Stride + ox * 4;
            if (!full.Pixels.AsSpan(b, crop.Width * 4).SequenceEqual(crop.Pixels.AsSpan(a, crop.Width * 4)))
                return false;
        }
        return true;
    }

    private static bool HasPaletteChunk(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        for (int i = 8; i + 4 <= bytes.Length; i++)
            if (bytes[i] == 'P' && bytes[i + 1] == 'L' && bytes[i + 2] == 'T' && bytes[i + 3] == 'E')
                return true;
        return false;
    }

    /// <summary>测试样本不随发布产物分发：从当前目录或程序集目录向上找源码树的 testdata。</summary>
    private static string? FindTestData(string name)
    {
        string fromCwd = Path.Combine(Directory.GetCurrentDirectory(), "testdata", name);
        if (File.Exists(fromCwd)) return fromCwd;
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "testdata", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>解码出的像素不能是纯色/全黑——用来确认是真的解出了画面，而不是填了缓冲区。</summary>
    private static bool HasVariance(byte[] pixels)
    {
        if (pixels.Length < 8) return false;
        byte a = pixels[0], b = pixels[1], c = pixels[2];
        for (int i = 0; i + 3 < pixels.Length; i += 4096)
            if (pixels[i] != a || pixels[i + 1] != b || pixels[i + 2] != c) return true;
        return false;
    }
}
