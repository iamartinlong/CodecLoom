namespace SimdCodecs.Processing;

/// <summary>
/// 纯托管像素算子。全部基于 byte[] 运算，无 P/Invoke、无 SkiaSharp / ImageSharp、无字体渲染依赖，
/// 因此可在 NativeAOT、WASM、任意工作目录的容器里直接跑。
/// 输入统一是 Bgra32（与 SimdCodecs 解码输出、Avalonia WriteableBitmap 一致）。
/// </summary>
public static class ImageOps
{
    /// <summary>任意尺寸双线性缩放。SimdCodecs 的 1/2^n 是解码期降采样，这里补的是成像后的插值缩放。</summary>
    public static byte[] ResizeBilinear(byte[] src, int sw, int sh, int tw, int th)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (sw <= 0 || sh <= 0 || tw <= 0 || th <= 0)
            throw new ArgumentOutOfRangeException(nameof(tw), "宽高必须为正数。");
        if (src.Length < sw * 4 * sh)
            throw new ArgumentException("源像素数组长度不足。", nameof(src));
        if ((long)tw * th * 4 > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(tw), "目标尺寸过大，超过单数组上限。");

        byte[] dst = new byte[tw * th * 4];
        // 预先算好每一列的源索引与权重，避免内层重复除法。
        var x0s = new int[tw];
        var x1s = new int[tw];
        var fxs = new float[tw];
        for (int x = 0; x < tw; x++)
        {
            double sx = (x + 0.5) * sw / tw - 0.5;
            int x0 = (int)Math.Floor(sx);
            x0s[x] = Math.Clamp(x0, 0, sw - 1);
            x1s[x] = Math.Clamp(x0 + 1, 0, sw - 1);
            fxs[x] = (float)(sx - x0);
        }

        for (int y = 0; y < th; y++)
        {
            double sy = (y + 0.5) * sh / th - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, sh - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sh - 1);
            float fy = (float)(sy - Math.Floor(sy));
            int row0 = y0 * sw * 4, row1 = y1 * sw * 4, drow = y * tw * 4;

            for (int x = 0; x < tw; x++)
            {
                int a = row0 + x0s[x] * 4, b = row0 + x1s[x] * 4;
                int c = row1 + x0s[x] * 4, d = row1 + x1s[x] * 4;
                float fx = fxs[x];
                int di = drow + x * 4;
                for (int ch = 0; ch < 4; ch++)
                {
                    float top = src[a + ch] + (src[b + ch] - src[a + ch]) * fx;
                    float bottom = src[c + ch] + (src[d + ch] - src[c + ch]) * fx;
                    dst[di + ch] = (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * fy), 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>亮度 / 对比度 / 灰度 / 反色，一次遍历完成。Alpha 通道保持不变。</summary>
    public static void Adjust(byte[] bgra, double brightness, double contrast,
        bool grayscale, bool invert)
    {
        if (brightness == 0 && contrast == 1.0 && !grayscale && !invert) return;
        double offset = brightness * 2.55;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            if (contrast != 1.0 || brightness != 0)
            {
                b = Clamp((b - 128) * contrast + 128 + offset);
                g = Clamp((g - 128) * contrast + 128 + offset);
                r = Clamp((r - 128) * contrast + 128 + offset);
            }
            if (grayscale)
            {
                byte y = Luma(r, g, b);
                b = g = r = y;
            }
            if (invert)
            {
                b = 255 - b; g = 255 - g; r = 255 - r;
            }
            bgra[i] = (byte)b;
            bgra[i + 1] = (byte)g;
            bgra[i + 2] = (byte)r;
        }
    }

    /// <summary>
    /// 几何水印：右下角半透明深色条 + 斜纹 + 角标，文本经 FNV-1a 哈希决定斜纹相位与角标位置。
    /// 刻意不渲染字形 —— 字体栅格化需要平台字体引擎或 SkiaSharp，会破坏零原生依赖与 AOT 兼容性。
    /// 不同文本会产生不同图案，可据此校验水印确实生效。
    /// </summary>
    public static void DrawWatermark(byte[] bgra, int w, int h, string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) { hash ^= c; hash *= 16777619; }

        int bw = Math.Clamp(w * 2 / 5, 24, 260);
        int bh = Math.Clamp(h / 6, 12, 52);
        if (bw < 12 || bh < 8) return;
        int x0 = w - bw - Math.Max(2, w / 40);
        int y0 = h - bh - Math.Max(2, h / 40);
        if (x0 < 0 || y0 < 0) return;
        int phase = (int)(hash % 12);

        for (int y = y0; y < y0 + bh; y++)
        {
            for (int x = x0; x < x0 + bw; x++)
            {
                int i = (y * w + x) * 4;
                bool border = x == x0 || y == y0 || x == x0 + bw - 1 || y == y0 + bh - 1;
                bool stripe = ((x - x0 + y - y0 + phase) % 12) < 3;
                if (border || stripe)
                {
                    bgra[i] = 235; bgra[i + 1] = 240; bgra[i + 2] = 255; // 近白
                }
                else
                {
                    // 50% 混合深蓝底，保留原图轮廓
                    bgra[i] = (byte)((bgra[i] + 18) / 2);
                    bgra[i + 1] = (byte)((bgra[i + 1] + 32) / 2);
                    bgra[i + 2] = (byte)((bgra[i + 2] + 58) / 2);
                }
            }
        }
        // 角标：哈希决定落在水印条的左侧还是右侧
        int cx = x0 + 6 + (int)(hash % (uint)Math.Max(1, bw - 20));
        for (int y = y0 + bh / 2 - 3; y <= y0 + bh / 2 + 3; y++)
            for (int x = cx; x < cx + 8 && x < x0 + bw - 2; x++)
            {
                if (y < y0 + 2 || y > y0 + bh - 3 || x < x0 + 2) continue;
                int i = (y * w + x) * 4;
                bgra[i] = 255; bgra[i + 1] = 210; bgra[i + 2] = 64;
            }
    }

    /// <summary>BT.601 亮度，整数近似，无浮点误差，跨平台逐位一致。</summary>
    public static byte Luma(int r, int g, int b) => (byte)((77 * r + 150 * g + 29 * b) >> 8);

    public static byte[] ToGray8(byte[] bgra, int w, int h)
    {
        byte[] gray = new byte[w * h];
        for (int p = 0, i = 0; p < gray.Length; p++, i += 4)
            gray[p] = Luma(bgra[i + 2], bgra[i + 1], bgra[i]);
        return gray;
    }

    /// <summary>OpenCV Mat(CV_8UC3) 期望的 BGR 连续布局，每行无 padding。</summary>
    public static byte[] ToBgr24(byte[] bgra, int w, int h)
    {
        byte[] bgr = new byte[w * h * 3];
        for (int p = 0, s = 0, d = 0; p < w * h; p++, s += 4, d += 3)
        {
            bgr[d] = bgra[s];
            bgr[d + 1] = bgra[s + 1];
            bgr[d + 2] = bgra[s + 2];
        }
        return bgr;
    }

    public static byte[] ToRgb24(byte[] bgra, int w, int h)
    {
        byte[] rgb = new byte[w * h * 3];
        for (int p = 0, s = 0, d = 0; p < w * h; p++, s += 4, d += 3)
        {
            rgb[d] = bgra[s + 2];
            rgb[d + 1] = bgra[s + 1];
            rgb[d + 2] = bgra[s];
        }
        return rgb;
    }

    /// <summary>任意布局转回 Bgra32，供编码器 / WriteableBitmap 使用。</summary>
    public static byte[] ToBgra32(byte[] pixels, PixelLayout layout, int w, int h)
    {
        byte[] bgra = new byte[w * h * 4];
        switch (layout)
        {
            case PixelLayout.Bgra32:
                Array.Copy(pixels, bgra, Math.Min(pixels.Length, bgra.Length));
                break;
            case PixelLayout.Gray8:
                for (int p = 0, s = 0, d = 0; p < w * h; p++, s++, d += 4)
                    bgra[d] = bgra[d + 1] = bgra[d + 2] = pixels[s];
                break;
            case PixelLayout.Bgr24:
                for (int p = 0, s = 0, d = 0; p < w * h; p++, s += 3, d += 4)
                {
                    bgra[d] = pixels[s];
                    bgra[d + 1] = pixels[s + 1];
                    bgra[d + 2] = pixels[s + 2];
                }
                break;
            case PixelLayout.Rgb24:
                for (int p = 0, s = 0, d = 0; p < w * h; p++, s += 3, d += 4)
                {
                    bgra[d] = pixels[s + 2];
                    bgra[d + 1] = pixels[s + 1];
                    bgra[d + 2] = pixels[s];
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return bgra;
    }

    private static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
}
