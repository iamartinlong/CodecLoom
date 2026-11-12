using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Core;

namespace SimdCodecs.Api;

/// <summary>
/// 真实 HTTP 集成测试：启动同一个 Kestrel 管线（随机端口），用 HttpClient 打全部端点，
/// 再把响应字节交回 Core 解码回读。不是 mock，也不是 TestServer 的内存管道。
/// </summary>
public static class ApiSelfTest
{
    private static int _pass;
    private static int _fail;

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        WebApplication app = Program.Build(Array.Empty<string>());
        app.Urls.Add("http://127.0.0.1:0");
        try
        {
            await app.StartAsync();
            IServer server = app.Services.GetRequiredService<IServer>();
            string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                             ?? app.Urls.First();
            if (!address.EndsWith('/')) address += "/";
            Console.WriteLine($"Kestrel 已启动：{address}");

            using var http = new HttpClient
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromMinutes(2)
            };
            await RunCasesAsync(http);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] 集成测试异常：{ex}");
            _fail++;
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        Console.WriteLine(_fail == 0
            ? $"== API 集成测试全部通过（{_pass}/{_pass}）=="
            : $"== API 集成测试失败 {_fail} 项（通过 {_pass} 项）==");
        return _fail == 0 ? 0 : 1;
    }

    private static async Task RunCasesAsync(HttpClient http)
    {
        // ---------- 样本 ----------
        const int w = 320, h = 240;
        byte[] pixels = Gradient(w, h);
        byte[] png = CodecService.EncodeBytes(pixels, w, h, ImageFormat.Png);
        byte[] jpeg = CodecService.EncodeBytes(pixels, w, h, ImageFormat.Jpeg, quality: 92);
        byte[] webp = CodecService.EncodeBytes(pixels, w, h, ImageFormat.Webp, quality: 90);
        byte[] gif = MakeAnimatedGif();
        byte[] broken = Encoding.ASCII.GetBytes("this is definitely not an image");
        byte[] bomb = MakePngBomb(40_000, 40_000);
        Check(png.Length > 0 && jpeg.Length > 0 && webp.Length > 0 && gif.Length > 0,
            $"内存样本生成：PNG {png.Length} B / JPEG {jpeg.Length} B / WebP {webp.Length} B / GIF {gif.Length} B");

        // ---------- 元信息与静态页 ----------
        Console.WriteLine("1) 静态测试台与元信息端点");
        using (HttpResponseMessage page = await http.GetAsync("/"))
        {
            string html = await page.Content.ReadAsStringAsync();
            Check(page.StatusCode == HttpStatusCode.OK && html.Contains("SimdCodecs"),
                $"GET / 返回测试台 HTML（{(int)page.StatusCode}，{html.Length} 字符）");
        }

        using (HttpResponseMessage health = await http.GetAsync("/api/health"))
        {
            using JsonDocument doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            bool heif = doc.RootElement.GetProperty("heifRegistered").GetBoolean();
            Check(health.StatusCode == HttpStatusCode.OK && heif,
                $"GET /api/health（{(int)health.StatusCode}）HEIF 已注册={heif}");
        }

        using (HttpResponseMessage formats = await http.GetAsync("/api/formats"))
        {
            using JsonDocument doc = JsonDocument.Parse(await formats.Content.ReadAsStringAsync());
            JsonElement inputs = doc.RootElement.GetProperty("inputs");
            int count = inputs.GetArrayLength();
            bool avifBlocked = inputs.EnumerateArray().Any(e =>
                e.GetProperty("name").GetString()!.Contains("Avif", StringComparison.OrdinalIgnoreCase)
                && !e.GetProperty("decodable").GetBoolean());
            Check(formats.StatusCode == HttpStatusCode.OK && count > 10 && avifBlocked,
                $"GET /api/formats 列出 {count} 种输入格式，AVIF 标记为不可解码={avifBlocked}");
        }

        // ---------- 预检 ----------
        Console.WriteLine("2) 上传前置校验");
        using (HttpResponseMessage inspect = await http.PostAsync("/api/inspect", Form(png, "gradient.png")))
        {
            string body = await inspect.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(body);
            int iw = doc.RootElement.GetProperty("width").GetInt32();
            int ih = doc.RootElement.GetProperty("height").GetInt32();
            string fmt = doc.RootElement.GetProperty("format").GetString() ?? "";
            Check(inspect.StatusCode == HttpStatusCode.OK && iw == w && ih == h && fmt == "Png",
                $"POST /api/inspect 只读头部：{fmt} {iw}x{ih}");
        }

        using (HttpResponseMessage bad = await http.PostAsync("/api/inspect", Form(broken, "broken.bin")))
            Check((int)bad.StatusCode is >= 400 and < 500,
                $"损坏文件被拒绝：HTTP {(int)bad.StatusCode} · {await DetailAsync(bad)}");

        GuardVerdict guard = ImageGuard.Validate(bomb, "bomb.png");
        Check(!guard.Accepted, $"Core 上传闸门拦住图片炸弹：HTTP {guard.StatusCode} · {guard.Reason}");
        using (HttpResponseMessage bombResp = await http.PostAsync("/api/inspect", Form(bomb, "bomb.png")))
            Check((int)bombResp.StatusCode is >= 400 and < 500,
                $"HTTP 层同样拦住 40000x40000 声明：{(int)bombResp.StatusCode} · {await DetailAsync(bombResp)}");

        using (HttpResponseMessage noFile = await http.PostAsync("/api/inspect",
                   new MultipartFormDataContent { { new StringContent("x"), "note" } }))
            Check((int)noFile.StatusCode is >= 400 and < 500,
                $"无文件字段返回 4xx：HTTP {(int)noFile.StatusCode}");

        // ---------- 单文件转换 ----------
        Console.WriteLine("3) 转换 / 缩放 / 裁剪 / 指定帧");
        using (HttpResponseMessage convert = await http.PostAsync("/api/convert",
                   Form(png, "gradient.png", ("target", "WebP"), ("quality", "80"))))
        {
            byte[] output = await convert.Content.ReadAsByteArrayAsync();
            LoadedImage back = output.Length > 0 ? CodecService.Load(output, "out.webp") : null!;
            Check(convert.StatusCode == HttpStatusCode.OK && back.Width == w && back.Height == h,
                $"POST /api/convert PNG→WebP：{png.Length} B → {output.Length} B，回读 {back.Width}x{back.Height}");
            Check(convert.Content.Headers.ContentType?.MediaType == "image/webp",
                "响应 Content-Type = image/webp");
            Check(convert.Headers.TryGetValues("X-Saved-Percent", out _),
                $"统计响应头齐全（节省 {Header(convert, "X-Saved-Percent")}%，解码 {Header(convert, "X-Decode-Ms")} ms）");
        }

        using (HttpResponseMessage scaled = await http.PostAsync("/api/convert",
                   Form(png, "gradient.png", ("target", "PNG"), ("scale", "2"))))
        {
            byte[] output = await scaled.Content.ReadAsByteArrayAsync();
            LoadedImage back = CodecService.Load(output, "half.png");
            Check(scaled.StatusCode == HttpStatusCode.OK && back.Width == w / 2 && back.Height == h / 2,
                $"解码期 1/2 缩放：{back.Width}x{back.Height}");
        }

        using (HttpResponseMessage cropped = await http.PostAsync("/api/convert",
                   Form(png, "gradient.png", ("target", "PNG"),
                       ("x", "20"), ("y", "15"), ("w", "60"), ("h", "45"))))
        {
            byte[] output = await cropped.Content.ReadAsByteArrayAsync();
            LoadedImage back = CodecService.Load(output, "crop.png");
            Check(cropped.StatusCode == HttpStatusCode.OK && back.Width == 60 && back.Height == 45,
                $"DecodeOptions.Region 裁剪：{back.Width}x{back.Height}");
        }

        using (HttpResponseMessage frame = await http.PostAsync("/api/convert",
                   Form(gif, "anim.gif", ("target", "PNG"), ("frame", "1"))))
        {
            byte[] output = await frame.Content.ReadAsByteArrayAsync();
            Check(frame.StatusCode == HttpStatusCode.OK && output.Length > 0 &&
                  Header(frame, "X-Frame-Index") == "1" && Header(frame, "X-Frame-Count") == "3",
                $"取动画第 2 帧：X-Frame-Index={Header(frame, "X-Frame-Index")} / " +
                $"X-Frame-Count={Header(frame, "X-Frame-Count")}，{output.Length} B");
        }

        using (HttpResponseMessage unknown = await http.PostAsync("/api/convert",
                   Form(png, "gradient.png", ("target", "AVIF"))))
            Check(unknown.StatusCode == HttpStatusCode.BadRequest,
                $"未知目标格式返回 400：{await DetailAsync(unknown)}");

        // ---------- 缩略图 ----------
        Console.WriteLine("4) 缩略图");
        using (HttpResponseMessage thumb = await http.PostAsync("/api/thumbnail",
                   Form(png, "gradient.png", ("box", "64"))))
        {
            byte[] output = await thumb.Content.ReadAsByteArrayAsync();
            LoadedImage back = CodecService.Load(output, "thumb.png");
            Check(thumb.StatusCode == HttpStatusCode.OK && back.Width <= 64 && back.Height <= 64,
                $"POST /api/thumbnail → {back.Width}x{back.Height} PNG（{output.Length} B）");
        }

        // ---------- 批量 zip ----------
        Console.WriteLine("5) 批量转换与多帧拆页（zip）");
        using (HttpResponseMessage batch = await http.PostAsync("/api/batch",
                   FormMany(new[] { (png, "gradient.png"), (jpeg, "gradient.jpg"), (webp, "gradient.webp") },
                       ("target", "PNG 量化"), ("quality", "80"))))
        {
            byte[] zipBytes = await batch.Content.ReadAsByteArrayAsync();
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            string[] names = zip.Entries.Select(e => e.FullName).ToArray();
            Check(batch.StatusCode == HttpStatusCode.OK && names.Count(n => n.EndsWith(".png")) == 3,
                $"POST /api/batch → zip {zipBytes.Length} B，3 张输出 + _report.txt");
            Check(names.Contains("_report.txt"), "zip 内含 _report.txt 逐项报告");
            using (StreamReader reader = new(zip.GetEntry("_report.txt")!.Open(), Encoding.UTF8))
            {
                string text = await reader.ReadToEndAsync();
                Check(text.Contains("summary:") && text.Contains("OK "),
                    "报告含逐项结果与汇总");
            }
            using (Stream entry = zip.Entries.First(e => e.FullName.EndsWith(".png")).Open())
            using (var ms = new MemoryStream())
            {
                await entry.CopyToAsync(ms);
                LoadedImage back = CodecService.Load(ms.ToArray(), "zip.png");
                Check(back.Width == w && back.Height == h,
                    $"zip 内图片可回读：{back.Width}x{back.Height}");
            }
        }

        using (HttpResponseMessage frames = await http.PostAsync("/api/frames",
                   Form(gif, "anim.gif", ("target", "PNG"))))
        {
            byte[] zipBytes = await frames.Content.ReadAsByteArrayAsync();
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            int images = zip.Entries.Count(e => e.FullName.EndsWith(".png"));
            Check(frames.StatusCode == HttpStatusCode.OK && images == 3,
                $"POST /api/frames 拆出 {images} 帧（zip {zipBytes.Length} B）");
            Check(Header(frames, "X-Batch-Ok") == "3",
                $"批量统计头：ok={Header(frames, "X-Batch-Ok")} failed={Header(frames, "X-Batch-Failed")}");
        }

        // ---------- 错误隔离 ----------
        using (HttpResponseMessage mixed = await http.PostAsync("/api/batch",
                   FormMany(new[] { (png, "good.png"), (broken, "bad.bin") }, ("target", "WebP"))))
        {
            byte[] zipBytes = await mixed.Content.ReadAsByteArrayAsync();
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            Check(mixed.StatusCode == HttpStatusCode.OK && Header(mixed, "X-Batch-Ok") == "1" &&
                  Header(mixed, "X-Batch-Failed") == "1",
                "坏文件被隔离，好文件正常输出，整体仍返回 200");
        }

        // ---------- 真实 HEIC ----------
        Console.WriteLine("6) 真实 HEIC 实图端到端");
        string? heicPath = TestDataPath("example.heic");
        if (heicPath is null)
        {
            Console.WriteLine("  [SKIP] 未找到 testdata/example.heic（脱离源码仓库运行时属正常）");
        }
        else
        {
            byte[] heic = File.ReadAllBytes(heicPath);
            ImageEntry head = CodecService.Inspect(heic, "example.heic");
            Check(head.Format == ImageFormat.Heif && head.Decodable && head.Width > 0,
                $"HEIC 头部预检：{head.Format} {head.Width}x{head.Height}，{heic.Length} B");

            using (HttpResponseMessage inspect = await http.PostAsync("/api/inspect",
                       Form(heic, "example.heic")))
            {
                string body = await inspect.Content.ReadAsStringAsync();
                using JsonDocument doc = JsonDocument.Parse(body);
                string fmt = doc.RootElement.GetProperty("format").GetString() ?? "";
                int iw = doc.RootElement.GetProperty("width").GetInt32();
                int ih = doc.RootElement.GetProperty("height").GetInt32();
                Check(inspect.StatusCode == HttpStatusCode.OK && fmt == "Heif" &&
                      iw == head.Width && ih == head.Height,
                    $"POST /api/inspect 识别真实 HEIC：{fmt} {iw}x{ih}");
            }

            using (HttpResponseMessage convert = await http.PostAsync("/api/convert",
                       Form(heic, "example.heic", ("target", "JPEG"), ("quality", "85"))))
            {
                byte[] output = await convert.Content.ReadAsByteArrayAsync();
                LoadedImage? back = output.Length > 0 ? CodecService.Load(output, "heic.jpg") : null;
                Check(convert.StatusCode == HttpStatusCode.OK && back is not null &&
                      back.Width == head.Width && back.Height == head.Height &&
                      Header(convert, "X-Source-Format") == "Heif",
                    $"POST /api/convert HEIC→JPEG：{heic.Length} B → {output.Length} B，" +
                    $"回读 {back?.Width}x{back?.Height}，解码 {Header(convert, "X-Decode-Ms")} ms");
            }

            using (HttpResponseMessage pngOut = await http.PostAsync("/api/convert",
                       Form(heic, "example.heic", ("target", "PNG"))))
            {
                byte[] output = await pngOut.Content.ReadAsByteArrayAsync();
                LoadedImage? back = output.Length > 0 ? CodecService.Load(output, "heic.png") : null;
                Check(pngOut.StatusCode == HttpStatusCode.OK && back is not null &&
                      back.Width == head.Width && back.Height == head.Height,
                    $"POST /api/convert HEIC→PNG：{output.Length} B，回读 {back?.Width}x{back?.Height}");
            }

            using (HttpResponseMessage thumb = await http.PostAsync("/api/thumbnail",
                       Form(heic, "example.heic", ("box", "96"))))
            {
                byte[] output = await thumb.Content.ReadAsByteArrayAsync();
                LoadedImage? back = output.Length > 0 ? CodecService.Load(output, "heic-thumb.png") : null;
                Check(thumb.StatusCode == HttpStatusCode.OK && back is not null &&
                      back.Width <= 96 && back.Height <= 96 &&
                      Header(thumb, "X-Source-Format") == "Heif",
                    $"POST /api/thumbnail HEIC → {back?.Width}x{back?.Height} PNG");
            }

            using (HttpResponseMessage batch = await http.PostAsync("/api/batch",
                       FormMany(new[] { (heic, "example.heic"), (png, "gradient.png") },
                           ("target", "JPEG"))))
            {
                byte[] zipBytes = await batch.Content.ReadAsByteArrayAsync();
                using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
                int images = zip.Entries.Count(e => e.FullName.EndsWith(".jpg"));
                Check(batch.StatusCode == HttpStatusCode.OK && images == 2 &&
                      Header(batch, "X-Batch-Ok") == "2",
                    $"POST /api/batch 含 HEIC 的混合批次 → zip 内 {images} 张 JPEG");
            }
        }

        // ---------- 一键预设 ----------
        Console.WriteLine("7) 一键预设：三端共用的业务动作");
        using (HttpResponseMessage presets = await http.GetAsync("/api/presets"))
        {
            string body = await presets.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement list = doc.RootElement.GetProperty("presets");
            string ids = string.Join(",", list.EnumerateArray()
                .Select(e => e.GetProperty("id").GetString()));
            Check(presets.StatusCode == HttpStatusCode.OK && list.GetArrayLength() == 4 &&
                  ids.Contains("heic-jpeg") && ids.Contains("scan-split"),
                $"GET /api/presets 返回 4 个预设：{ids}");
        }
        using (HttpResponseMessage health2 = await http.GetAsync("/api/health"))
        {
            string body = await health2.Content.ReadAsStringAsync();
            Check(body.Contains("presets") && body.Contains("/api/process") && body.Contains("/api/pixels"),
                "GET /api/health 暴露预设与新增端点，测试台可动态发现能力");
        }

        string? heicPath2 = TestDataPath("example.heic");
        string? heic2Path = TestDataPath("C002.heic");
        string? tiffPath = TestDataPath("multipage-pillow.tiff");
        if (heicPath2 is null || heic2Path is null)
        {
            Console.WriteLine("  [SKIP] 未找到 HEIC 样本，跳过预设批量断言");
        }
        else
        {
            byte[] heic1 = File.ReadAllBytes(heicPath2);
            byte[] heic2 = File.ReadAllBytes(heic2Path);
            using HttpResponseMessage reject = await http.PostAsync("/api/convert",
                Form(png, "gradient.png", ("preset", "heic-jpeg")));
            Check(reject.StatusCode == HttpStatusCode.BadRequest,
                "POST /api/convert 用 HEIC 预设喂 PNG → 400，源格式约束生效");

            using HttpResponseMessage batch = await http.PostAsync("/api/batch",
                FormMany(new[] { (heic1, "example.heic"), (heic2, "C002.heic"), (png, "gradient.png") },
                    ("preset", "heic-jpeg")));
            byte[] zipBytes = await batch.Content.ReadAsByteArrayAsync();
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            string[] jpgs = zip.Entries.Select(e => e.FullName)
                .Where(n => n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).ToArray();
            string report = ReadZipText(zip, "_report.txt");
            string manifest = ReadZipText(zip, BatchService.ManifestFileName);
            bool jpgsReadable = true;
            foreach (string name in jpgs)
            {
                byte[] bytes = ReadZipBytes(zip, name);
                if (CodecService.Load(bytes, name).Width <= 0) jpgsReadable = false;
            }
            Check(batch.StatusCode == HttpStatusCode.OK && jpgs.Length == 2 &&
                  Header(batch, "X-Batch-Ok") == "2" && Header(batch, "X-Batch-Skipped") == "1" &&
                  Header(batch, "X-Preset-Id") == "heic-jpeg" && jpgsReadable,
                $"预设批量：2 张 HEIC → 2 张可回读 JPEG，1 张 PNG 跳过（{zipBytes.Length} B zip）");
            Check(report.Contains("preset=heic-jpeg") && manifest.Contains("SKIP"),
                "zip 内 _report.txt / _manifest.csv 记录预设与被跳过项");
        }

        if (tiffPath is null)
        {
            Console.WriteLine("  [SKIP] 未找到 multipage-pillow.tiff，跳过拆页断言");
        }
        else
        {
            byte[] tiff = File.ReadAllBytes(tiffPath);
            using HttpResponseMessage split = await http.PostAsync("/api/batch",
                FormMany(new[] { (tiff, "multipage-pillow.tiff") }, ("preset", "scan-split")));
            byte[] zipBytes = await split.Content.ReadAsByteArrayAsync();
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            string[] pages = zip.Entries.Select(e => e.FullName)
                .Where(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            string report = ReadZipText(zip, "_report.txt");
            string manifest = ReadZipText(zip, BatchService.ManifestFileName);
            Check(split.StatusCode == HttpStatusCode.OK && pages.Length == 3 &&
                  pages[0].EndsWith("-p001.png") && pages[2].EndsWith("-p003.png") &&
                  Header(split, "X-Batch-Ok") == "3",
                $"扫描件拆页预设：3 页 TIFF → {string.Join(" / ", pages)}");
            Check(pages.All(n => CodecService.Load(ReadZipBytes(zip, n), n).Width > 0) &&
                  report.Contains("pageNumbering=True") && manifest.Contains(",Tiff,3,"),
                "每页可回读，报告含页码命名标记，清单含源格式与页码列");
        }

        // ---------- 处理层 / CV 输入 ----------
        Console.WriteLine("8) 纯托管处理层：任意尺寸缩放 / 滤镜 / 水印");
        using (HttpResponseMessage processed = await http.PostAsync("/api/process",
                   Form(png, "gradient.png", ("target", "PNG"), ("resizeW", "137"),
                       ("resizeH", "91"), ("brightness", "10"), ("gray", "true"),
                       ("watermark", "API-TEST"))))
        {
            byte[] output = await processed.Content.ReadAsByteArrayAsync();
            LoadedImage? back = output.Length > 0 ? CodecService.Load(output, "processed.png") : null;
            Check(processed.StatusCode == HttpStatusCode.OK && back is { Width: 137, Height: 91 } &&
                  Header(processed, "X-Output-Width") == "137" &&
                  Header(processed, "X-Cv-Text-Rows") is not "",
                $"POST /api/process 137×91 灰度+亮度+水印 → {output.Length} B PNG 可回读，" +
                $"Otsu 阈值 {Header(processed, "X-Cv-Otsu-Threshold")}");
        }
        using (HttpResponseMessage unknownLayout = await http.PostAsync("/api/pixels",
                   Form(png, "gradient.png", ("layout", "yuv42"))))
        {
            Check(unknownLayout.StatusCode == HttpStatusCode.BadRequest,
                "POST /api/pixels 未知布局 → 400，不 silently fallback");
        }

        Console.WriteLine("9) OCR / CV 输入层：Gray8 / Bgr24 连续像素 + 真实下游算子");
        using (HttpResponseMessage grayResp = await http.PostAsync("/api/pixels",
                   Form(png, "gradient.png", ("layout", "gray8"))))
        {
            byte[] raw = await grayResp.Content.ReadAsByteArrayAsync();
            Check(grayResp.StatusCode == HttpStatusCode.OK && raw.Length == w * h &&
                  Header(grayResp, "X-Stride") == w.ToString() &&
                  Header(grayResp, "X-Bytes-Per-Pixel") == "1" &&
                  Header(grayResp, "X-Cv-Otsu-Threshold") is not "",
                $"Gray8 返回 {raw.Length} B 连续像素，stride={Header(grayResp, "X-Stride")}，" +
                $"Otsu 阈值 {Header(grayResp, "X-Cv-Otsu-Threshold")}，投影 {Header(grayResp, "X-Cv-Text-Rows")} 行");
        }
        using (HttpResponseMessage bgrResp = await http.PostAsync("/api/pixels",
                   Form(png, "gradient.png", ("layout", "bgr24"))))
        {
            byte[] raw = await bgrResp.Content.ReadAsByteArrayAsync();
            Check(bgrResp.StatusCode == HttpStatusCode.OK && raw.Length == w * h * 3 &&
                  Header(bgrResp, "X-Stride") == (w * 3).ToString() &&
                  raw[0] == pixels[0] && raw[1] == pixels[1] && raw[2] == pixels[2],
                $"Bgr24 返回 {raw.Length} B，前三字节 {raw[0]},{raw[1]},{raw[2]} 与源 BGRA 的 B/G/R 一致");
        }
    }

    private static string ReadZipText(ZipArchive zip, string name)
    {
        ZipArchiveEntry? entry = zip.GetEntry(name);
        if (entry is null) return "";
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static byte[] ReadZipBytes(ZipArchive zip, string name)
    {
        using var ms = new MemoryStream();
        using (Stream s = zip.GetEntry(name)!.Open()) s.CopyTo(ms);
        return ms.ToArray();
    }

    // ---------------- 工具 ----------------

    private static void Check(bool ok, string label)
    {
        Console.WriteLine(ok ? $"  [PASS] {label}" : $"  [FAIL] {label}");
        if (ok) _pass++;
        else _fail++;
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? values.FirstOrDefault() ?? "" : "";

    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("detail", out JsonElement detail)
                ? detail.GetString() ?? body : body;
        }
        catch { return body; }
    }

    private static MultipartFormDataContent Form(byte[] file, string name,
        params (string Key, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(file);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(bytes, "file", name);
        foreach ((string key, string value) in fields) content.Add(new StringContent(value), key);
        return content;
    }

    private static MultipartFormDataContent FormMany(
        IEnumerable<(byte[] Data, string Name)> files, params (string Key, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        foreach ((byte[] data, string name) in files)
        {
            var bytes = new ByteArrayContent(data);
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(bytes, "files", name);
        }
        foreach ((string key, string value) in fields) content.Add(new StringContent(value), key);
        return content;
    }

    private static byte[] Gradient(int w, int h)
    {
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(255 - x * 255 / w);
                px[i + 1] = (byte)(y * 255 / h);
                px[i + 2] = (byte)((x + y) * 255 / (w + h));
                px[i + 3] = 255;
            }
        return px;
    }

    private static byte[] MakeAnimatedGif()
    {
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
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>构造只有 IHDR + IEND、CRC 正确、却声明 40000x40000 的 PNG，用来验证像素炸弹闸门。</summary>
    private static byte[] MakePngBomb(int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)h);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // RGBA
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        byte[] crcInput = typeBytes.Concat(data).ToArray();
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>测试样本不随发布产物分发：从当前目录或程序集目录向上找源码树的 testdata。</summary>
    private static string? TestDataPath(string name)
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
}
