namespace SimdCodecs.Processing;

/// <summary>一次真实下游 CV 算子的输出：OCR 前处理常见的二值化 + 行投影切分结果。</summary>
public sealed record CvAnalysis(int Threshold, double InkRatio, int TextRows, int[] RowDensity)
{
    public string Summary => $"Otsu 阈值 {Threshold} · 墨迹占比 {InkRatio:P1} · 投影检出 {TextRows} 个文本行";
}

/// <summary>
/// OCR / CV 输入层：把 Bgra32 转成下游期望的连续像素布局，并用真实的图像算法验证布局正确。
/// Otsu 二值化 + 行投影是 OCR 预处理的标准第一步，用它代替「假装有下游」的桩调用。
/// </summary>
public static class CvInput
{
    /// <summary>大津法全局阈值。返回二值图（255 = 墨迹）与实际选中的阈值。</summary>
    public static (byte[] Binary, int Threshold) Otsu(byte[] gray)
    {
        ArgumentNullException.ThrowIfNull(gray);
        if (gray.Length == 0) throw new ArgumentException("灰度图为空。", nameof(gray));

        var hist = new int[256];
        foreach (byte v in gray) hist[v]++;
        int total = gray.Length;
        long sumAll = 0;
        for (int t = 0; t < 256; t++) sumAll += (long)t * hist[t];

        long sumB = 0;
        int weightB = 0, best = 0;
        double maxVariance = -1;
        for (int t = 0; t < 256; t++)
        {
            weightB += hist[t];
            if (weightB == 0) continue;
            int weightF = total - weightB;
            if (weightF == 0) break;
            sumB += (long)t * hist[t];
            double meanB = sumB / (double)weightB;
            double meanF = (sumAll - sumB) / (double)weightF;
            double between = weightB * (double)weightF * (meanB - meanF) * (meanB - meanF);
            if (between > maxVariance) { maxVariance = between; best = t; }
        }

        byte[] binary = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
            binary[i] = gray[i] <= best ? (byte)255 : (byte)0;
        return (binary, best);
    }

    /// <summary>水平行投影：统计每行的墨迹像素，连续超阈值的行段记为一个文本行。</summary>
    public static CvAnalysis AnalyzeRows(byte[] binary, int width, int height,
        double minRowInkRatio = 0.02)
    {
        if (binary.Length < width * height)
            throw new ArgumentException("二值图长度与宽高不匹配。", nameof(binary));

        var density = new int[height];
        long ink = 0;
        for (int y = 0; y < height; y++)
        {
            int count = 0;
            int row = y * width;
            for (int x = 0; x < width; x++)
                if (binary[row + x] != 0) count++;
            density[y] = count;
            ink += count;
        }

        int thresholdPixels = Math.Max(1, (int)(width * minRowInkRatio));
        int rows = 0;
        bool inside = false;
        foreach (int d in density)
        {
            bool hot = d >= thresholdPixels;
            if (hot && !inside) rows++;
            inside = hot;
        }
        return new CvAnalysis(0, ink / (double)(width * height), rows, density);
    }

    /// <summary>把 Otsu 得到的阈值填进分析结果，便于一次调用拿到完整报告。</summary>
    public static CvAnalysis Analyze(byte[] gray, int width, int height)
    {
        (byte[] binary, int threshold) = Otsu(gray);
        CvAnalysis raw = AnalyzeRows(binary, width, height);
        return raw with { Threshold = threshold };
    }
}
