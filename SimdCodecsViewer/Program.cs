using Avalonia;

namespace SimdCodecsViewer;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 无 GUI 命令行批量转换：dotnet run -- --batch <输入目录> <输出目录> [导出预设]
        if (args.Length > 0 && args[0] == "--batch")
        {
            Environment.ExitCode = BatchCli.Run(args.Skip(1).ToArray());
            return;
        }

        // 无 GUI 的端到端自检：dotnet run -- --selftest [输出目录]
        if (args.Length > 0 && args[0] == "--selftest")
        {
            Environment.ExitCode = SelfTest.Run(args.Length > 1 ? args[1] : null);
            return;
        }

        if (args.Length > 0 && args[0] == "--smoke")
        {
            Environment.ExitCode = GuiSmoke.Run();
            return;
        }

        // UI 截图（视觉自查）：dotnet run -- --screenshot <png 路径> [宽] [高]
        if (args.Length > 0 && args[0] == "--screenshot")
        {
            Environment.ExitCode = UiShot.Run(
                args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "screenshot.png"),
                args.Length > 2 && int.TryParse(args[2], out int w) ? w : 1320,
                args.Length > 3 && int.TryParse(args[3], out int h) ? h : 820);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
