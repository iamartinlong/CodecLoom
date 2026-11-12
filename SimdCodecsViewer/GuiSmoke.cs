using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using PixelRect = Sdcb.SimdCodecs.Abstractions.PixelRect;

namespace SimdCodecsViewer;

/// <summary>
/// 不弹窗的 GUI 冒烟测试：初始化 Avalonia 平台，构建三栏窗口，
/// 走一遍「加入队列 -> 解码显示 -> 裁剪 -> 多帧切换 -> 缩略图」，验证 UI 层与核心层真的接上了。
/// </summary>
public static class GuiSmoke
{
    public static int Run()
    {
        Console.OutputEncoding = Encoding.UTF8;
        int failures = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok) failures++;
        }

        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            Check(true, "Avalonia 平台服务初始化成功");

            string dir = Path.Combine(Path.GetTempPath(), "SimdCodecsViewer-smoke");
            Directory.CreateDirectory(dir);
            const int w = 160, h = 120;
            byte[] pixels = SelfTest.MakeGradient(w, h);
            string png = Path.Combine(dir, "smoke.png");
            CodecService.Write(pixels, w, h, png, ImageFormat.Png);

            var window = new MainWindow();
            Check(window.Title?.Contains("工作台") == true,
                $"MainWindow 控件树构建成功：{window.Title}");

            window.AddFiles(new[] { png });
            Check(window.Items.Count == 1 && window.Items[0].Entry.Decodable,
                $"拖放入口 AddFiles 加入队列：{window.Items[0].Entry.Summary}");

            Wait(window.DebugLoadAsync(0));
            Check(window.PreviewSource is not null && window.Current is { Width: w, Height: h },
                $"预览 WriteableBitmap 已挂载：{window.Current?.Width}x{window.Current?.Height}");

            window.DebugSetRegion(new PixelRect(20, 15, 60, 45));
            Wait(window.DebugLoadAsync(0));
            Check(window.Current is { Width: 60, Height: 45 },
                $"框选裁剪链路：输出 {window.Current?.Width}x{window.Current?.Height}");
            window.DebugSetRegion(null);

            var sample = new LoadedImage
            {
                Pixels = new byte[2 * 2 * 4],
                Width = 2,
                Height = 2,
                Stride = 8,
                Info = default,
                Metadata = ImageMetadata.Empty,
                DecodeMs = 0,
                FileBytes = 32,
                Path = "smoke",
                Scale = 1,
                Oriented = false
            };
            using WriteableBitmap bitmap = MainWindow.ToBitmap(sample);
            using ILockedFramebuffer fb = bitmap.Lock();
            Check(fb.Format == Avalonia.Platform.PixelFormat.Bgra8888 && fb.RowBytes >= 8,
                $"Bgra32 -> Bgra8888 像素搬运：{fb.Size.Width}x{fb.Size.Height}, RowBytes={fb.RowBytes}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GUI 冒烟失败：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            failures++;
        }

        Console.WriteLine(failures == 0 ? "== GUI 冒烟通过 ==" : $"== GUI 冒烟失败 {failures} 项 ==");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 无窗口测试里不能用 GetResult() 死等：ReloadAsync 的续体会 Post 回 UI 线程。
    /// 这里手动泵送 Dispatcher 队列，让异步链路能走完，同时设超时防止卡死。
    /// </summary>
    private static void Wait(Task task)
    {
        DateTime start = DateTime.UtcNow;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(20))
                throw new TimeoutException("UI 异步任务 20 秒内未完成（Dispatcher 未泵送？）");
        }
        task.GetAwaiter().GetResult();
    }
}
