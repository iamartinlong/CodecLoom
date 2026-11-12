using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SimdCodecsViewer;

/// <summary>SimdCodecs 的 Bgra32 像素 -> Avalonia WriteableBitmap，逐行复制以兼容两侧 stride 不同。</summary>
public static class BitmapFactory
{
    public static WriteableBitmap Create(byte[] pixels, int width, int height, int stride)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using ILockedFramebuffer fb = bitmap.Lock();
        int rowBytes = Math.Min(stride, fb.RowBytes);
        for (int y = 0; y < height; y++)
            Marshal.Copy(pixels, y * stride, IntPtr.Add(fb.Address, y * fb.RowBytes), rowBytes);
        return bitmap;
    }

    public static WriteableBitmap Create(LoadedImage image) =>
        Create(image.Pixels, image.Width, image.Height, image.Stride);
}
