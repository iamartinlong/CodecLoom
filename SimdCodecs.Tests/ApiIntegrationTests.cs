using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Api;
using SimdCodecs.Core;
using Xunit;

namespace SimdCodecs.Tests;

/// <summary>
/// 真实 Kestrel + HttpClient 集成测试（不是 TestServer 内存管道）。三套配置各自起独立端口：
/// 默认 / 开启鉴权 / 开启限流，验证生产化开关不会污染默认行为。
/// </summary>
public abstract class ApiAppFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    protected virtual string[] ExtraArgs => Array.Empty<string>();

    public async Task InitializeAsync()
    {
        App = Program.Build(ExtraArgs);
        App.Urls.Add("http://127.0.0.1:0");
        await App.StartAsync();
        IServer server = App.Services.GetRequiredService<IServer>();
        string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                         ?? App.Urls.First();
        if (!address.EndsWith('/')) address += "/";
        Http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task DisposeAsync()
    {
        Http?.Dispose();
        if (App is not null)
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}

public sealed class DefaultApiFixture : ApiAppFixture;

public sealed class SecuredApiFixture : ApiAppFixture
{
    public const string Key = "ci-secret";
    protected override string[] ExtraArgs => new[]
    {
        "--ApiRuntime__Auth__Enabled=true",
        $"--ApiRuntime__Auth__Key={Key}"
    };
}

public sealed class RateLimitedApiFixture : ApiAppFixture
{
    protected override string[] ExtraArgs => new[]
    {
        "--ApiRuntime__RateLimit__Enabled=true",
        "--ApiRuntime__RateLimit__PermitLimit=3",
        "--ApiRuntime__RateLimit__WindowSeconds=60"
    };
}

public class DefaultApiTests(DefaultApiFixture fixture) : IClassFixture<DefaultApiFixture>
{
    private const int W = 320, H = 240;
    private static readonly byte[] Png =
        CodecService.EncodeBytes(TestData.Gradient(W, H), W, H, ImageFormat.Png);
    private static readonly byte[] DocPng =
        CodecService.EncodeBytes(TestData.Document(W, H), W, H, ImageFormat.Png);
    private HttpClient Http => fixture.Http;

    [Fact]
    public async Task 静态测试台与元信息端点可用()
    {
        using HttpResponseMessage page = await Http.GetAsync("/");
        string html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("SimdCodecs", html);

        using HttpResponseMessage health = await Http.GetAsync("/api/health");
        using JsonDocument h = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.True(h.RootElement.GetProperty("heifRegistered").GetBoolean());
        Assert.True(h.RootElement.GetProperty("pureManaged").GetBoolean());

        using HttpResponseMessage formats = await Http.GetAsync("/api/formats");
        using JsonDocument f = JsonDocument.Parse(await formats.Content.ReadAsStringAsync());
        Assert.True(f.RootElement.GetProperty("inputs").GetArrayLength() > 10);

        using HttpResponseMessage presets = await Http.GetAsync("/api/presets");
        using JsonDocument p = JsonDocument.Parse(await presets.Content.ReadAsStringAsync());
        Assert.Equal(4, p.RootElement.GetProperty("presets").GetArrayLength());
    }

    [Fact]
    public async Task 就绪探针检查编解码器与处理层()
    {
        using HttpResponseMessage resp = await Http.GetAsync("/api/ready");
        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
        JsonElement checks = doc.RootElement.GetProperty("checks");
        Assert.True(checks.GetProperty("heifRegistered").GetBoolean());
        Assert.True(checks.GetProperty("pngDecodable").GetBoolean());
        Assert.True(checks.GetProperty("processingLoaded").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("authEnabled").GetBoolean());
    }

    [Fact]
    public async Task 指标端点提供_JSON_与_Prometheus_两种导出()
    {
        _ = await Http.GetAsync("/api/health");

        using HttpResponseMessage json = await Http.GetAsync("/api/metrics");
        using JsonDocument doc = JsonDocument.Parse(await json.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        Assert.True(doc.RootElement.GetProperty("totalRequests").GetInt64() > 0);
        Assert.True(doc.RootElement.GetProperty("apiRequests").GetInt64() > 0);
        Assert.Contains("200", doc.RootElement.GetProperty("byStatus").ToString());
        Assert.Contains("uptimeSeconds", doc.RootElement.ToString());

        using HttpResponseMessage text = await Http.GetAsync("/api/metrics?format=prometheus");
        string prom = await text.Content.ReadAsStringAsync();
        Assert.Contains("text/plain", text.Content.Headers.ContentType!.ToString());
        Assert.Contains("simdcodecs_requests_total", prom);
        Assert.Contains("# TYPE", prom);
        Assert.Contains("simdcodecs_rate_limit_enabled 0", prom);
    }

    [Fact]
    public async Task 每个响应都带请求_ID()
    {
        using HttpResponseMessage a = await Http.GetAsync("/api/health");
        using HttpResponseMessage b = await Http.GetAsync("/api/health");
        string idA = a.Headers.GetValues("X-Request-Id").Single();
        string idB = b.Headers.GetValues("X-Request-Id").Single();
        Assert.Equal(16, idA.Length);
        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public async Task Inspect_返回头部信息且不解码()
    {
        using HttpResponseMessage resp = await Http.PostAsync("/api/inspect", TestData.Form(Png, "g.png"));
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Png", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(W, doc.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(H, doc.RootElement.GetProperty("height").GetInt32());
        Assert.True(doc.RootElement.GetProperty("decodable").GetBoolean());
    }

    [Fact]
    public async Task 图片炸弹返回_413_且带_ProblemDetails()
    {
        using HttpResponseMessage resp = await Http.PostAsync("/api/inspect",
            TestData.Form(TestData.PngBomb(), "bomb.png"));
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Contains("图片炸弹", body);
    }

    [Fact]
    public async Task 缺少_multipart_返回_415()
    {
        using var content = new StringContent("not a form");
        using HttpResponseMessage resp = await Http.PostAsync("/api/convert", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
    }

    [Fact]
    public async Task Convert_输出字节可回读且统计头完整()
    {
        using HttpResponseMessage resp = await Http.PostAsync("/api/convert",
            TestData.Form(Png, "g.png", ("target", "PNG"), ("quality", "90")));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Png", resp.Headers.GetValues("X-Source-Format").Single());
        Assert.Equal(W.ToString(), resp.Headers.GetValues("X-Decoded-Width").Single());
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
        LoadedImage back = CodecService.Load(bytes, "out.png");
        Assert.Equal(W, back.Width);
        Assert.Equal(H, back.Height);
    }

    [Fact]
    public async Task Process_任意尺寸缩放后输出仍是合法图片()
    {
        using HttpResponseMessage resp = await Http.PostAsync("/api/process",
            TestData.Form(Png, "g.png", ("resizeW", "137"), ("resizeH", "91"),
                ("gray", "true"), ("watermark", "proof"), ("target", "PNG")));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("137", resp.Headers.GetValues("X-Output-Width").Single());
        Assert.Equal("91", resp.Headers.GetValues("X-Output-Height").Single());
        Assert.Contains("双线性", Uri.UnescapeDataString(
            resp.Headers.GetValues("X-Process-Steps").Single()));
        LoadedImage back = CodecService.Load(await resp.Content.ReadAsByteArrayAsync(), "p.png");
        Assert.Equal(137, back.Width);
        Assert.Equal(91, back.Height);
    }

    [Fact]
    public async Task Pixels_返回_Gray8_连续布局并跑通_OCR_行投影()
    {
        using HttpResponseMessage resp = await Http.PostAsync("/api/pixels",
            TestData.Form(DocPng, "doc.png", ("layout", "gray8")));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Gray8", resp.Headers.GetValues("X-Pixel-Layout").Single());
        Assert.Equal("320", resp.Headers.GetValues("X-Stride").Single());
        Assert.Equal("3", resp.Headers.GetValues("X-Cv-Text-Rows").Single());
        byte[] raw = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(W * H, raw.Length);
        Assert.True(int.Parse(resp.Headers.GetValues("X-Cv-Otsu-Threshold").Single()) > 0);
    }

    [Fact]
    public async Task 未知布局返回_400_而不是崩溃()
    {
        using HttpResponseMessage resp = await Http.PostAsync("/api/pixels",
            TestData.Form(Png, "g.png", ("layout", "yuv420")));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("detail", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 批量预设对不支持的源格式给出_Skipped_统计()
    {
        string? heic = TestData.Find("example.heic");
        var files = new List<(byte[] Data, string Name)> { (Png, "not-heic.png") };
        if (heic is not null) files.Add((await File.ReadAllBytesAsync(heic), "example.heic"));

        using HttpResponseMessage resp = await Http.PostAsync("/api/batch",
            TestData.FormMany(files, ("preset", "heic-jpeg")));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.TryGetValues("X-Batch-Ok", out var ok));
        Assert.True(resp.Headers.TryGetValues("X-Batch-Skipped", out var skipped));
        Assert.Equal(heic is null ? "0" : "1", ok!.Single());
        Assert.Equal("1", skipped!.Single());
    }

    [Fact]
    public async Task 启用鉴权时默认配置仍然完全开放()
    {
        // 默认 fixture 的所有业务端点都不需要 Key；这里用最敏感的 metrics 复核一次。
        using HttpResponseMessage resp = await Http.GetAsync("/api/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}

public class SecuredApiTests(SecuredApiFixture fixture) : IClassFixture<SecuredApiFixture>
{
    private HttpClient Http => fixture.Http;

    [Fact]
    public async Task 缺少_Key_的业务请求返回_401_ProblemDetails()
    {
        using HttpResponseMessage resp = await Http.GetAsync("/api/formats");
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Unauthorized", body);
        Assert.Contains("X-Api-Key", body);
        Assert.True(resp.Headers.Contains("X-Request-Id"));
    }

    [Fact]
    public async Task X_Api_Key_与_Bearer_两种凭据都可用()
    {
        using var byHeader = new HttpRequestMessage(HttpMethod.Get, "/api/formats");
        byHeader.Headers.Add("X-Api-Key", SecuredApiFixture.Key);
        using HttpResponseMessage a = await Http.SendAsync(byHeader);
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);

        using var byBearer = new HttpRequestMessage(HttpMethod.Get, "/api/formats");
        byBearer.Headers.Add("Authorization", "Bearer " + SecuredApiFixture.Key);
        using HttpResponseMessage b = await Http.SendAsync(byBearer);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
    }

    [Fact]
    public async Task 错误_Key_被拒绝()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/formats");
        req.Headers.Add("X-Api-Key", "wrong-key");
        using HttpResponseMessage resp = await Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 健康与就绪探针保持免鉴权()
    {
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/api/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task 指标端点也受保护且如实上报开关状态()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/metrics")).StatusCode);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/metrics");
        req.Headers.Add("X-Api-Key", SecuredApiFixture.Key);
        using HttpResponseMessage ok = await Http.SendAsync(req);
        using JsonDocument doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(doc.RootElement.GetProperty("authEnabled").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("rateLimitEnabled").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("errors").GetInt64() > 0);
    }
}

public class RateLimitedApiTests(RateLimitedApiFixture fixture) : IClassFixture<RateLimitedApiFixture>
{
    [Fact]
    public async Task 固定窗口超出配额返回_429_并带_RetryAfter()
    {
        var codes = new List<HttpStatusCode>();
        string? retryAfter = null;
        for (int i = 0; i < 6; i++)
        {
            using HttpResponseMessage resp = await fixture.Http.GetAsync("/api/formats");
            codes.Add(resp.StatusCode);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && retryAfter is null)
                retryAfter = resp.Headers.TryGetValues("Retry-After", out var values)
                    ? values.Single() : null;
        }
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        Assert.Equal("60", retryAfter);
    }
}
