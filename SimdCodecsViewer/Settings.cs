using System.Text;
using System.Text.Json;

namespace SimdCodecsViewer;

/// <summary>
/// 桌面工作台的持久化设置。刻意手写 JSON 生成 + JsonDocument 解析，
/// 而不是 JsonSerializer：NativeAOT 下对 POCO 的反射序列化会触发 IL3050，
/// 字段固定手写则零警告、零依赖。
/// </summary>
public sealed class AppSettings
{
    /// <summary>界面语言："zh" / "en"。</summary>
    public string Language { get; set; } = "zh";

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    /// <summary>输出目录（批量转换落盘位置）。</summary>
    public string OutputDir { get; set; } = "";

    public int ScaleIndex { get; set; }          // 解码缩放下拉 0..3
    public bool OrientOnLoad { get; set; } = true;
    public int FormatIndex { get; set; }         // 导出格式下拉
    public double Quality { get; set; } = 90;
    public int PresetIndex { get; set; }         // 0 = 不使用预设
    public bool AllFrames { get; set; }
    public int ResizeW { get; set; }
    public int ResizeH { get; set; }
    public string Watermark { get; set; } = "";
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1.0;
    public bool Grayscale { get; set; }
    public bool Invert { get; set; }
    public int LayoutIndex { get; set; }         // 裸像素布局下拉

    /// <summary>文档智能模块 opt-in 开关（默认关闭：不构造推理会话、不占内存）。</summary>
    public bool DocumentEnabled { get; set; }
    public bool DocumentCtc { get; set; }        // 是否附带逐字符估算框
    public bool DocumentCompare { get; set; }    // 一步式是否同时跑原图 OCR 对比

    /// <summary>深拷贝，供自检比对 round-trip 结果。</summary>
    public AppSettings Clone() => new()
    {
        Language = Language,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        OutputDir = OutputDir,
        ScaleIndex = ScaleIndex,
        OrientOnLoad = OrientOnLoad,
        FormatIndex = FormatIndex,
        Quality = Quality,
        PresetIndex = PresetIndex,
        AllFrames = AllFrames,
        ResizeW = ResizeW,
        ResizeH = ResizeH,
        Watermark = Watermark,
        Brightness = Brightness,
        Contrast = Contrast,
        Grayscale = Grayscale,
        Invert = Invert,
        LayoutIndex = LayoutIndex,
        DocumentEnabled = DocumentEnabled,
        DocumentCtc = DocumentCtc,
        DocumentCompare = DocumentCompare
    };
}

public static class SettingsService
{
    private static string? _overridePath;

    private static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimdCodecsViewer",
            "settings.json");

    /// <summary>当前存储位置；自检可通过 <see cref="UseFileForTest"/> 重定向。</summary>
    public static string StoragePath => _overridePath ?? DefaultPath;

    public static AppSettings Load()
    {
        try
        {
            string path = StoragePath;
            if (!File.Exists(path)) return new AppSettings();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            var s = new AppSettings();
            T Get<T>(string name, T fallback, Func<JsonElement, T> pick)
            {
                if (root.TryGetProperty(name, out JsonElement el))
                {
                    try { return pick(el); } catch { return fallback; }
                }
                return fallback;
            }
            s.Language = Get("language", s.Language, e => e.GetString() ?? "zh");
            s.WindowWidth = Get("windowWidth", s.WindowWidth, e => e.GetDouble());
            s.WindowHeight = Get("windowHeight", s.WindowHeight, e => e.GetDouble());
            s.OutputDir = Get("outputDir", s.OutputDir, e => e.GetString() ?? "");
            s.ScaleIndex = Get("scaleIndex", s.ScaleIndex, e => e.GetInt32());
            s.OrientOnLoad = Get("orientOnLoad", s.OrientOnLoad, e => e.GetBoolean());
            s.FormatIndex = Get("formatIndex", s.FormatIndex, e => e.GetInt32());
            s.Quality = Get("quality", s.Quality, e => e.GetDouble());
            s.PresetIndex = Get("presetIndex", s.PresetIndex, e => e.GetInt32());
            s.AllFrames = Get("allFrames", s.AllFrames, e => e.GetBoolean());
            s.ResizeW = Get("resizeW", s.ResizeW, e => e.GetInt32());
            s.ResizeH = Get("resizeH", s.ResizeH, e => e.GetInt32());
            s.Watermark = Get("watermark", s.Watermark, e => e.GetString() ?? "");
            s.Brightness = Get("brightness", s.Brightness, e => e.GetDouble());
            s.Contrast = Get("contrast", s.Contrast, e => e.GetDouble());
            s.Grayscale = Get("grayscale", s.Grayscale, e => e.GetBoolean());
            s.Invert = Get("invert", s.Invert, e => e.GetBoolean());
            s.LayoutIndex = Get("layoutIndex", s.LayoutIndex, e => e.GetInt32());
            // 旧版本设置文件没有这三个键时自然回退 false（opt-in 不变）。
            s.DocumentEnabled = Get("documentEnabled", s.DocumentEnabled, e => e.GetBoolean());
            s.DocumentCtc = Get("documentCtc", s.DocumentCtc, e => e.GetBoolean());
            s.DocumentCompare = Get("documentCompare", s.DocumentCompare, e => e.GetBoolean());
            return s;
        }
        catch
        {
            // 设置文件损坏不阻塞启动：回默认值即可，下次保存会覆盖。
            return new AppSettings();
        }
    }

    public static void Save(AppSettings s)
    {
        string path = StoragePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.Append('{').Append('\n');

        void Str(string name, string value) =>
            sb.Append("  \"").Append(name).Append("\": \"")
              .Append(JsonEncodedText.Encode(value ?? ""))
              .Append("\",\n");
        void Num(string name, double value) =>
            sb.Append("  \"").Append(name).Append("\": ")
              .Append(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\n");
        void Bool(string name, bool value) =>
            sb.Append("  \"").Append(name).Append("\": ").Append(value ? "true" : "false").Append(",\n");
        void Int(string name, int value) =>
            sb.Append("  \"").Append(name).Append("\": ").Append(value).Append(",\n");

        Str("language", s.Language);
        Num("windowWidth", s.WindowWidth);
        Num("windowHeight", s.WindowHeight);
        Str("outputDir", s.OutputDir);
        Int("scaleIndex", s.ScaleIndex);
        Bool("orientOnLoad", s.OrientOnLoad);
        Int("formatIndex", s.FormatIndex);
        Num("quality", s.Quality);
        Int("presetIndex", s.PresetIndex);
        Bool("allFrames", s.AllFrames);
        Int("resizeW", s.ResizeW);
        Int("resizeH", s.ResizeH);
        Str("watermark", s.Watermark);
        Num("brightness", s.Brightness);
        Num("contrast", s.Contrast);
        Bool("grayscale", s.Grayscale);
        Bool("invert", s.Invert);
        Int("layoutIndex", s.LayoutIndex);
        Bool("documentEnabled", s.DocumentEnabled);
        Bool("documentCtc", s.DocumentCtc);
        Bool("documentCompare", s.DocumentCompare);

        // 去掉最后一项的逗号，保持合法 JSON。
        sb.Remove(sb.Length - 2, 1);
        sb.Append('}');
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>测试隔离：把存储重定向到指定文件，Dispose 后恢复。</summary>
    public static IDisposable UseFileForTest(string path) => new TempRedirect(path);

    private sealed class TempRedirect : IDisposable
    {
        private readonly string? _old;
        public TempRedirect(string path)
        {
            _old = _overridePath;
            _overridePath = path;
        }
        public void Dispose() => _overridePath = _old;
    }
}
