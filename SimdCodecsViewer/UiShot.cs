using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SimdCodecsViewer;

/// <summary>
/// 无交互 UI 截图：构建窗口 -> 加载示例图 -> 泵送布局 -> RenderTargetBitmap 导出 PNG。
/// 用于自动化视觉自查（美化迭代时确认配色/布局），不影响正常运行。
/// </summary>
public static class UiShot
{
    public static int Run(string path, int width, int height, bool showDocumentPanel = false)
    {
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();

        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        try
        {
            Pump(TimeSpan.FromSeconds(5), () => window.Bounds.Width < width);

            // 有测试图就放入队列并选中，让截图呈现真实工作状态。
            string? sample = FindSample();
            if (sample is not null)
            {
                window.AddFiles(new[] { sample });
                Task t = window.DebugLoadAsync(0);
                Pump(TimeSpan.FromSeconds(10), () => !t.IsCompleted);
                t.GetAwaiter().GetResult();
            }

            if (showDocumentPanel) window.FocusDocumentPanel();
            Pump(TimeSpan.FromSeconds(2), () => false);

            using var rtb = new RenderTargetBitmap(
                new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                new Vector(96, 96));
            rtb.Render(window);
            using FileStream fs = File.Create(path);
            rtb.Save(fs);
            Console.WriteLine($"screenshot -> {path} ({new FileInfo(path).Length / 1024} KB)");
        }
        finally
        {
            window.Close();
        }
        return 0;
    }

    private static void Pump(TimeSpan budget, Func<bool> keepWaiting)
    {
        DateTime start = DateTime.UtcNow;
        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(16);
        }
        while (DateTime.UtcNow - start < budget && keepWaiting());
    }

    private static string? FindSample()
    {
        foreach (string name in new[] { "example.heic", "gradient.png", "multipage-pillow.tiff" })
        {
            string? p = FindTestData(name);
            if (p is not null) return p;
        }
        return null;
    }

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
}
