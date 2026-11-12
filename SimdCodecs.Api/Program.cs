using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimdCodecs.Api;
using SimdCodecs.Core;

namespace SimdCodecs.Api;

public static class Program
{
    /// <summary>
    /// 默认启动 Kestrel 服务；--apitest 改成「启动真实服务 + HttpClient 打全套端点」的集成测试。
    /// 两者共用同一个 Build()，测的就是线上跑的那套管线。
    /// </summary>
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--apitest")
            return ApiSelfTest.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();

        WebApplication app = Build(args);
        app.Run();
        return 0;
    }

    private static string NormalizeRuntimeArg(string arg)
    {
        if (!arg.StartsWith("--ApiRuntime__", StringComparison.Ordinal)) return arg;
        int eq = arg.IndexOf('=');
        string key = eq < 0 ? arg : arg[..eq];
        string suffix = eq < 0 ? "" : arg[eq..];
        return key.Replace("__", ":", StringComparison.Ordinal) + suffix;
    }

    public static WebApplication Build(string[] args)
    {
        // 内容根固定在程序集同目录：测试台与配置随 build / publish 一起走，
        // 从解决方案根目录、发布目录或任意工作目录启动都能正确加载。
        var options = new WebApplicationOptions
        {
            // 命令行配置用 ":" 分层，环境变量才用 "__"；这里兼容大家习惯的 --ApiRuntime__A__B 写法。
            Args = args.Select(NormalizeRuntimeArg).ToArray(),
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot"
        };
        WebApplicationBuilder builder = WebApplication.CreateBuilder(options);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = ImageEndpoints.MaxBodyBytes);
        // 鉴权 / 限流 / 结构化日志 / 指标：默认关闭，命令行或环境变量可开。
        ApiRuntime.Add(builder);

        // 编解码实现由容器提供：将来要回退到 Magick.NET / ImageSharp，
        // 只需改这里的注册（或先调 CodecServices.Use(...)），端点层无需改动。
        builder.Services.AddSingleton<ICodecService>(_ => CodecServices.Current);
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("*")));

        WebApplication app = builder.Build();
        app.UseCors();
        ApiRuntime.Use(app);
        app.UseDefaultFiles();
        app.UseStaticFiles();
        ApiRuntime.Map(app);
        ImageEndpoints.Map(app);
        return app;
    }
}
