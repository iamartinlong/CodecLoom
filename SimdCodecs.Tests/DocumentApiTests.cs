using System.Net;
using System.Text.Json;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Core;
using Xunit;

namespace SimdCodecs.Tests;

/// <summary>文档端点的真实 Kestrel 集成：与 DefaultApiFixture 共用同一套 Program.Build。</summary>
public sealed class DocumentApiFixture : ApiAppFixture
{
    protected override string[] ExtraArgs => new[] { "--Document__Enabled=true" };
}

public class DocumentApiTests(DocumentApiFixture fixture) : IClassFixture<DocumentApiFixture>
{
    private const int W = 320, H = 240;
    private static readonly byte[] DocPng =
        CodecService.EncodeBytes(TestData.Document(W, H), W, H, ImageFormat.Png);
    private HttpClient Http => fixture.Http;

    [Fact]
    public async Task 健康与就绪探针报告文档模块状态()
    {
        using HttpResponseMessage health = await Http.GetAsync("/api/health");
        using JsonDocument h = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.True(h.RootElement.GetProperty("document").GetProperty("enabled").GetBoolean());
        Assert.Equal("tiny", h.RootElement.GetProperty("document").GetProperty("model").GetString());
        string endpoints = h.RootElement.GetProperty("endpoints").ToString();
        Assert.Contains("/api/enhance", endpoints);
        Assert.Contains("/api/ocr", endpoints);
        Assert.Contains("/api/document", endpoints);

        using HttpResponseMessage ready = await Http.GetAsync("/api/ready");
        using JsonDocument r = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.True(r.RootElement.GetProperty("documentEnabled").GetBoolean());
    }

    [Fact]
    public async Task 增强端点返回可回读的_PNG_与诊断头()
    {
        using HttpResponseMessage resp = await Http.PostAsync("/api/enhance",
            TestData.Form(DocPng, "doc.png"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("cpu", resp.Headers.GetValues("X-Document-Backend").First());
        Assert.Equal("false", resp.Headers.GetValues("X-Document-Degraded").First());
        Assert.Equal("320", resp.Headers.GetValues("X-Output-Width").First());
        byte[] png = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(png.Length > 0);
        LoadedImage back = CodecService.Load(png, "enhanced.png");
        Assert.Equal(W, back.Width);
        Assert.Equal(H, back.Height);
        Assert.Equal(W * H * 4, back.Pixels.Length);
    }

    [Fact]
    public async Task OCR_端点返回结构化_JSON_且响应头不泄露正文()
    {
        using HttpResponseMessage resp = await Http.PostAsync("/api/ocr",
            TestData.Form(DocPng, "doc.png", ("ctc", "true")));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal("tiny", root.GetProperty("model").GetString());
        Assert.Equal("cpu", root.GetProperty("backend").GetString());
        Assert.True(root.TryGetProperty("text", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("lines").ValueKind);
        Assert.NotNull(resp.Headers.GetValues("X-Document-Text-Hash").FirstOrDefault());
        Assert.Equal(root.GetProperty("lineCount").GetInt32().ToString(),
            resp.Headers.GetValues("X-Document-Lines").First());
    }

    [Fact]
    public async Task 一步式端点支持增强前后对比()
    {
        using HttpResponseMessage resp = await Http.PostAsync("/api/document",
            TestData.Form(DocPng, "doc.png", ("compare", "true")));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("enhanced").GetBoolean());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("original").ValueKind);
        Assert.True(root.GetProperty("totalMs").GetDouble() > 0);
        Assert.True(root.GetProperty("enhanceMs").GetDouble() > 0);
    }

    [Fact]
    public async Task 超过文档闸门的图返回_413_且不解码()
    {
        const int big = 2500;
        byte[] png = CodecService.EncodeBytes(new byte[big * big * 4], big, big, ImageFormat.Png);
        using HttpResponseMessage resp = await Http.PostAsync("/api/document",
            TestData.Form(png, "big.png"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("长边", body);
    }
}

/// <summary>默认部署 opt-in：未启用时三个端点一律 501，行为可预期。</summary>
public class DisabledDocumentApiTests(DefaultApiFixture fixture) : IClassFixture<DefaultApiFixture>
{
    private static readonly byte[] DocPng =
        CodecService.EncodeBytes(TestData.Document(320, 240), 320, 240, ImageFormat.Png);

    [Theory]
    [InlineData("/api/enhance")]
    [InlineData("/api/ocr")]
    [InlineData("/api/document")]
    public async Task 未启用时返回_501(string path)
    {
        using HttpResponseMessage resp = await fixture.Http.PostAsync(path,
            TestData.Form(DocPng, "doc.png"));
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }

    [Fact]
    public async Task 默认健康报告模块关闭()
    {
        using HttpResponseMessage resp = await fixture.Http.GetAsync("/api/health");
        using JsonDocument h = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(h.RootElement.GetProperty("document").GetProperty("enabled").GetBoolean());
    }
}

/// <summary>
/// 隐私默认值：开启鉴权后，文档端点先过 ApiRuntime 的 401，
/// 证明 OCR 正文不会在无认证情况下被取走。
/// </summary>
public class DocumentAuthTests(SecuredApiFixture fixture) : IClassFixture<SecuredApiFixture>
{
    private static readonly byte[] DocPng =
        CodecService.EncodeBytes(TestData.Document(320, 240), 320, 240, ImageFormat.Png);

    [Fact]
    public async Task 无_API_Key_的_OCR_请求返回_401()
    {
        using HttpResponseMessage resp = await fixture.Http.PostAsync("/api/ocr",
            TestData.Form(DocPng, "doc.png"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 带_API_Key_时到达模块开关判定()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ocr")
        {
            Content = TestData.Form(DocPng, "doc.png")
        };
        request.Headers.Add("X-Api-Key", SecuredApiFixture.Key);
        using HttpResponseMessage resp = await fixture.Http.SendAsync(request);
        // 该 fixture 未启用 Document，所以鉴权通过后是 501，而不是 200。
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }
}
