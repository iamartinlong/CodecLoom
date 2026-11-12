using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Document;
using SimdCodecs.Processing;

namespace SimdCodecs.Api;

/// <summary>生产化开关。默认全关，因此本地开发 / 测试台 / 现有回归零改动即可运行。</summary>
public sealed class ApiRuntimeOptions
{
    public const string Section = "ApiRuntime";
    public ApiAuthOptions Auth { get; set; } = new();
    public ApiRateLimitOptions RateLimit { get; set; } = new();
    public bool StructuredLog { get; set; } = true;
}

public sealed class ApiAuthOptions
{
    public bool Enabled { get; set; }
    public string Key { get; set; } = "";
    public string HeaderName { get; set; } = "X-Api-Key";
}

public sealed class ApiRateLimitOptions
{
    public bool Enabled { get; set; }
    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// 进程内指标。刻意不引入 Prometheus / OpenTelemetry NuGet 包，保持「零额外依赖 + AOT 友好」，
/// /api/metrics 同时提供 JSON 与 Prometheus 文本两种导出。
/// </summary>
public sealed class ApiMetrics
{
    private const int SampleCapacity = 1024;
    private readonly ConcurrentDictionary<string, long> _byStatus = new();
    private readonly ConcurrentDictionary<string, long> _byEndpoint = new();
    private readonly double[] _latency = new double[SampleCapacity];
    private readonly object _latencyGate = new();
    private long _total, _api, _errors, _rejected, _bytes;
    private int _sampleCount, _sampleIndex;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void Record(string path, int status, double ms, long bytes)
    {
        Interlocked.Increment(ref _total);
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _api);
            _byEndpoint.AddOrUpdate(Normalize(path), 1, (_, n) => n + 1);
        }
        if (status >= 400) Interlocked.Increment(ref _errors);
        if (status == 429) Interlocked.Increment(ref _rejected);
        if (bytes > 0) Interlocked.Add(ref _bytes, bytes);
        _byStatus.AddOrUpdate(status.ToString(CultureInfo.InvariantCulture), 1, (_, n) => n + 1);
        lock (_latencyGate)
        {
            _latency[_sampleIndex++ % SampleCapacity] = ms;
            _sampleCount = Math.Min(SampleCapacity, _sampleCount + 1);
        }
    }

    private static string Normalize(string path)
    {
        // /api/xxx 的路径里没有用户 ID，直接作为标签即可；避免把文件名当标签造成基数爆炸。
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"/{parts[0]}/{parts[1]}" : path;
    }

    private (double Avg, double P95) Latency()
    {
        double[] snapshot;
        lock (_latencyGate)
        {
            snapshot = _latency.Take(_sampleCount).ToArray();
        }
        if (snapshot.Length == 0) return (0, 0);
        Array.Sort(snapshot);
        return (snapshot.Average(), snapshot[(int)Math.Floor((snapshot.Length - 1) * 0.95)]);
    }

    public object Snapshot(ApiRuntimeOptions options)
    {
        (double avg, double p95) = Latency();
        return new
        {
            status = "ok",
            uptimeSeconds = Math.Round((DateTimeOffset.UtcNow - StartedAt).TotalSeconds, 1),
            totalRequests = Interlocked.Read(ref _total),
            apiRequests = Interlocked.Read(ref _api),
            errors = Interlocked.Read(ref _errors),
            rateLimited = Interlocked.Read(ref _rejected),
            responseBytes = Interlocked.Read(ref _bytes),
            avgLatencyMs = Math.Round(avg, 2),
            p95LatencyMs = Math.Round(p95, 2),
            byStatus = _byStatus.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value),
            byEndpoint = _byEndpoint.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value),
            authEnabled = options.Auth.Enabled,
            rateLimitEnabled = options.RateLimit.Enabled,
            runtime = Environment.Version.ToString()
        };
    }

    public string Prometheus(ApiRuntimeOptions options)
    {
        (double avg, double p95) = Latency();
        var sb = new StringBuilder();
        sb.AppendLine("# HELP simdcodecs_requests_total Total HTTP requests since process start.");
        sb.AppendLine("# TYPE simdcodecs_requests_total counter");
        sb.Append("simdcodecs_requests_total ").Append(Interlocked.Read(ref _total)).Append('\n');
        sb.AppendLine("# HELP simdcodecs_api_requests_total Requests whose path starts with /api.");
        sb.AppendLine("# TYPE simdcodecs_api_requests_total counter");
        sb.Append("simdcodecs_api_requests_total ").Append(Interlocked.Read(ref _api)).Append('\n');
        sb.AppendLine("# HELP simdcodecs_errors_total Responses with status >= 400.");
        sb.AppendLine("# TYPE simdcodecs_errors_total counter");
        sb.Append("simdcodecs_errors_total ").Append(Interlocked.Read(ref _errors)).Append('\n');
        sb.AppendLine("# HELP simdcodecs_rate_limited_total Responses rejected with HTTP 429.");
        sb.AppendLine("# TYPE simdcodecs_rate_limited_total counter");
        sb.Append("simdcodecs_rate_limited_total ").Append(Interlocked.Read(ref _rejected)).Append('\n');
        sb.AppendLine("# HELP simdcodecs_request_duration_ms Average and p95 request latency in ms.");
        sb.AppendLine("# TYPE simdcodecs_request_duration_ms gauge");
        sb.Append("simdcodecs_request_duration_ms{quantile=\"avg\"} ")
          .Append(avg.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("simdcodecs_request_duration_ms{quantile=\"p95\"} ")
          .Append(p95.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');
        foreach ((string status, long count) in _byStatus.OrderBy(kv => kv.Key))
            sb.Append("simdcodecs_responses_total{status=\"").Append(status).Append("\"} ")
              .Append(count).Append('\n');
        sb.Append("# HELP simdcodecs_uptime_seconds Process uptime.\n# TYPE simdcodecs_uptime_seconds gauge\n")
          .Append("simdcodecs_uptime_seconds ")
          .Append(Math.Round((DateTimeOffset.UtcNow - StartedAt).TotalSeconds, 1)
              .ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("simdcodecs_auth_enabled ")
          .Append(options.Auth.Enabled ? 1 : 0).Append('\n');
        sb.Append("simdcodecs_rate_limit_enabled ")
          .Append(options.RateLimit.Enabled ? 1 : 0).Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// API 生产化管线：请求 ID、可选 API Key 鉴权、固定窗口限流、结构化单行 JSON 日志、
/// 进程内指标、就绪探针。全部可通过配置开关，默认不影响本地开发与既有回归。
/// </summary>
public static class ApiRuntime
{
    public const string LogCategory = "SimdCodecs.Api.ApiRuntime";
    public const string RatePolicy = "api-fixed-window";

    public static void Add(WebApplicationBuilder builder)
    {
        ApiRuntimeOptions options =
            builder.Configuration.GetSection(ApiRuntimeOptions.Section).Get<ApiRuntimeOptions>()
            ?? new ApiRuntimeOptions();
        options.Auth ??= new ApiAuthOptions();
        options.RateLimit ??= new ApiRateLimitOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ApiMetrics>();

        if (options.Auth.Enabled && string.IsNullOrWhiteSpace(options.Auth.Key))
            throw new InvalidOperationException(
                "启用了 ApiRuntime:Auth 但未提供 Key；请设置 ApiRuntime__Auth__Key 环境变量。");

        if (options.RateLimit.Enabled)
        {
            int permits = Math.Max(1, options.RateLimit.PermitLimit);
            int window = Math.Clamp(options.RateLimit.WindowSeconds, 1, 3600);
            builder.Services.AddRateLimiter(limiter =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.AddPolicy(RatePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permits,
                        Window = TimeSpan.FromSeconds(window),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
                limiter.OnRejected = async (ctx, token) =>
                {
                    ctx.HttpContext.Response.Headers["Retry-After"] = window.ToString(CultureInfo.InvariantCulture);
                    ctx.HttpContext.Response.ContentType = "application/problem+json";
                    await ctx.HttpContext.Response.WriteAsJsonAsync(new
                    {
                        title = "Too many requests",
                        detail = $"固定窗口限流：{permits} 次 / {window} 秒。",
                        status = 429
                    }, cancellationToken: token);
                };
            });
        }

        // 结构化日志：整行 JSON、字段可直接被采集器索引；其余类别仍只放行 Warning。
        if (options.StructuredLog)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddFilter(LogCategory, LogLevel.Information);
        }
    }

    public static void Use(WebApplication app)
    {
        ApiRuntimeOptions options = app.Services.GetRequiredService<ApiRuntimeOptions>();
        ApiMetrics metrics = app.Services.GetRequiredService<ApiMetrics>();
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);

        app.Use(async (ctx, next) =>
        {
            string requestId = ctx.Request.Headers["X-Request-Id"].FirstOrDefault()
                               ?? Guid.NewGuid().ToString("n")[..16];
            ctx.Response.Headers["X-Request-Id"] = requestId;
            var sw = Stopwatch.StartNew();
            int status = 500;
            try
            {
                if (options.Auth.Enabled && IsProtected(ctx.Request) && !Authorized(ctx.Request, options.Auth))
                {
                    status = StatusCodes.Status401Unauthorized;
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/problem+json";
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        title = "Unauthorized",
                        detail = $"请在 {options.Auth.HeaderName} 或 Authorization: Bearer 中提供 API Key。",
                        status
                    }, cancellationToken: ctx.RequestAborted);
                }
                else
                {
                    await next();
                    status = ctx.Response.StatusCode;
                }
            }
            finally
            {
                sw.Stop();
                string path = ctx.Request.Path.Value ?? "/";
                metrics.Record(path, status, sw.Elapsed.TotalMilliseconds,
                    ctx.Response.ContentLength ?? 0);
                if (options.StructuredLog && path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "http_request ts={Timestamp} level={Level} method={Method} path={Path} " +
                        "status={Status} elapsedMs={ElapsedMs} requestId={RequestId} " +
                        "bytes={ResponseBytes} traceParent={TraceParent}",
                        DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        status >= 500 ? "error" : status >= 400 ? "warn" : "info",
                        ctx.Request.Method, path, status,
                        Math.Round(sw.Elapsed.TotalMilliseconds, 2), requestId,
                        ctx.Response.ContentLength ?? 0,
                        ctx.Request.Headers["traceparent"].FirstOrDefault());
                }
            }
        });

        if (options.RateLimit.Enabled) app.UseRateLimiter();
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/ready", (ApiRuntimeOptions options, ApiMetrics metrics,
            DocumentRuntime document) =>
        {
            bool heif = ImageCodecs.IsDecodable(ImageFormat.Heif);
            bool png = ImageCodecs.IsDecodable(ImageFormat.Png);
            bool processing = ImageProcessor.Apply(new byte[] { 10, 20, 30, 255 }, 1, 1)
                .Pixels.Length == 4;
            bool ready = heif && png && processing;
            var payload = new
            {
                status = ready ? "ready" : "starting",
                checks = new { heifRegistered = heif, pngDecodable = png, processingLoaded = processing },
                uptimeSeconds = Math.Round((DateTimeOffset.UtcNow - metrics.StartedAt).TotalSeconds, 1),
                authEnabled = options.Auth.Enabled,
                rateLimitEnabled = options.RateLimit.Enabled,
                documentEnabled = document.Options.Enabled,
                documentModel = "tiny",
                documentBackend = document.Options.Backend
            };
            return ready ? Results.Ok(payload) : Results.Problem(
                title: "Not ready", detail: "编解码器或处理层自检未通过。", statusCode: 503);
        });

        app.MapGet("/api/metrics", (HttpContext ctx, ApiMetrics metrics, ApiRuntimeOptions options) =>
            string.Equals(ctx.Request.Query["format"], "prometheus", StringComparison.OrdinalIgnoreCase)
                ? Results.Text(metrics.Prometheus(options), "text/plain; version=0.0.4; charset=utf-8")
                : Results.Ok(metrics.Snapshot(options)));
    }

    private static bool IsProtected(HttpRequest request) =>
        request.Method != HttpMethods.Options &&
        request.Path.StartsWithSegments("/api") &&
        !request.Path.StartsWithSegments("/api/health") &&
        !request.Path.StartsWithSegments("/api/ready");

    private static bool Authorized(HttpRequest request, ApiAuthOptions options)
    {
        string? candidate = request.Headers[options.HeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(candidate))
        {
            string bearer = request.Headers["Authorization"].ToString();
            if (bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                candidate = bearer[7..].Trim();
        }
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(options.Key)) return false;
        byte[] supplied = Encoding.UTF8.GetBytes(candidate);
        byte[] expected = Encoding.UTF8.GetBytes(options.Key);
        return supplied.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}
