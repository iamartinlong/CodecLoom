using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sdcb.SimdCodecs.Abstractions;
using SimdCodecs.Document;
using SimdCodecs.Processing;
using PixelRect = Sdcb.SimdCodecs.Abstractions.PixelRect;
using Path = System.IO.Path;

namespace SimdCodecsViewer;

/// <summary>
/// 三栏客户端：文件队列 / 预览与裁剪 / 信息与批量转换。
/// 分工不变 —— SimdCodecs 负责格式识别、编解码、解码期缩放与裁剪、多帧、元数据；
/// Avalonia 负责显示、拖放、交互与文件对话框。任意尺寸缩放和滤镜仍不在这里。
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly string[] ImagePatterns =
    {
        "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp", "*.ico", "*.cur",
        "*.heic", "*.heif", "*.tif", "*.tiff", "*.jp2", "*.j2k", "*.jbig2", "*.jb2",
        "*.qoi", "*.ppm", "*.pgm", "*.pbm", "*.pam", "*.pfm", "*.apng", "*.jxl", "*.avif"
    };

    private readonly ObservableCollection<ImageItem> _items = new();
    private readonly Avalonia.Controls.Image _view;
    private readonly Grid _previewGrid;
    private readonly Canvas _cropCanvas;
    private readonly Rectangle _cropRectVisual;
    private readonly TextBlock _placeholder;
    private readonly StackPanel _info;
    private readonly TextBlock _status;
    private readonly TextBlock _frameLabel;
    private readonly TextBlock _cropLabel;
    private readonly TextBlock _batchSummary;
    private readonly ComboBox _scaleBox;
    private readonly CheckBox _orientBox;
    private readonly CheckBox _cropBox;
    private readonly CheckBox _allFramesBox;
    private readonly ComboBox _formatBox;
    private readonly ComboBox _presetBox;
    private readonly TextBox _resizeW;
    private readonly TextBox _resizeH;
    private readonly TextBox _watermark;
    private readonly Slider _brightness;
    private readonly Slider _contrast;
    private readonly CheckBox _grayBox;
    private readonly CheckBox _invertBox;
    private readonly ComboBox _layoutBox;
    private readonly TextBlock _processSummary;
    private readonly Slider _quality;
    private readonly TextBox _outputDir;
    private readonly ProgressBar _progressBar;
    private readonly Button _exportButton;
    private readonly Button _prevFrame;
    private readonly Button _nextFrame;
    private readonly Button _playButton;
    private readonly Button _batchButton;
    private readonly Button _clearCropButton;
    private readonly ListBox _list;
    private readonly DispatcherTimer _playTimer;

    // ---- 文档智能（opt-in）：单帧增强 / OCR / 一步式管线 ----
    private readonly CheckBox _docEnabledBox;
    private readonly CheckBox _docCtcBox;
    private readonly CheckBox _docCompareBox;
    private readonly TextBox _docOutput;
    private readonly TextBlock _docSummary;
    private readonly Button _docEnhanceButton;
    private readonly Button _docOcrButton;
    private readonly Button _docPipelineButton;
    private readonly ScrollViewer _rightScroll;
    private DocumentRuntime? _document;
    private bool _docBusy;

    private LoadedImage? _current;
    private PixelRect? _baseRegion;
    private int _frameIndex;
    private bool _frameLoading;
    private bool _selecting;
    private Point _dragStart;
    private CancellationTokenSource? _batchCts;
    private CancellationTokenSource? _decodeCts;
    private readonly AppSettings _settings;

    public MainWindow()
    {
        _settings = SettingsService.Load();
        L10n.Language = string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        Title = L10n.Tr("CodecLoom · 本地图片工作台");
        Width = _settings.WindowWidth > 400 ? _settings.WindowWidth : 1320;
        Height = _settings.WindowHeight > 400 ? _settings.WindowHeight : 820;
        MinWidth = 1040;
        MinHeight = 620;
        Background = Ui.B(Ui.Bg);
        Foreground = Ui.B(Ui.Text);
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, Consolas");
        Closing += (_, _) => PersistSettings();
        DarkTitleBar.Attach(this); // Windows：标题栏跟随暗色主题（含 Win11 同色底）

        // ---------- 顶部工具栏 ----------
        var openButton = Button(L10n.Tr("打开图片…"), OnOpen);
        openButton.Classes.Add("primary");
        var addButton = Button(L10n.Tr("添加文件…"), OnAdd);
        var clearButton = Button(L10n.Tr("清空队列"), OnClear);
        _exportButton = Button(L10n.Tr("导出当前…"), OnExport);
        _exportButton.Classes.Add("primary");

        _scaleBox = new ComboBox
        {
            ItemsSource = new[] { L10n.Tr("1/1 原始"), "1/2", "1/4", "1/8" },
            SelectedIndex = 0,
            MinWidth = 100
        };
        _scaleBox.SelectionChanged += async (_, _) => await ReloadAsync();

        _orientBox = new CheckBox
        {
            Content = L10n.Tr("EXIF 转正"), IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        _orientBox.IsCheckedChanged += async (_, _) => await ReloadAsync();

        _formatBox = new ComboBox
        {
            ItemsSource = CodecService.Targets.Select(t => t.Label).ToArray(),
            SelectedIndex = 0,
            MinWidth = 112
        };

        _quality = new Slider
        {
            Minimum = 10, Maximum = 100, Value = 90, Width = 110,
            VerticalAlignment = VerticalAlignment.Center
        };

        // ---- 一键预设 + 纯托管处理层 ----
        _presetBox = new ComboBox
        {
            ItemsSource = new[] { L10n.Tr("不使用预设") }
                .Concat(BatchPresets.All.Select(p => p.Label)).ToArray(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 11
        };
        _presetBox.SelectionChanged += (_, _) =>
        {
            if (_presetBox.SelectedIndex <= 0) return;
            BatchPreset p = BatchPresets.All[_presetBox.SelectedIndex - 1];
            _status!.Text = $"预设「{p.Label}」：{p.Description}（批量转换时生效）";
        };

        _resizeW = new TextBox { Width = 62, FontSize = 11, Watermark = L10n.Tr("宽") };
        _resizeH = new TextBox { Width = 62, FontSize = 11, Watermark = L10n.Tr("高") };
        _watermark = new TextBox { FontSize = 11, Watermark = L10n.Tr("水印文本（空=不加水印）") };
        _brightness = new Slider { Minimum = -100, Maximum = 100, Value = 0, Width = 150 };
        _contrast = new Slider { Minimum = 0.2, Maximum = 2.5, Value = 1.0, Width = 150 };
        _grayBox = new CheckBox { Content = L10n.Tr("灰度"), FontSize = 11 };
        _invertBox = new CheckBox { Content = L10n.Tr("反色"), FontSize = 11 };
        _layoutBox = new ComboBox
        {
            ItemsSource = new[] { "Bgra32", "Gray8", "Bgr24", "Rgb24" },
            SelectedIndex = 0, Width = 110, FontSize = 11
        };
        _processSummary = new TextBlock
        {
            Text = L10n.Tr("未应用处理"), FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.Wrap
        };

        _docEnabledBox = new CheckBox
        {
            Content = L10n.Tr("启用文档智能（首次点击加载模型）"), FontSize = 11
        };
        _docCtcBox = new CheckBox { Content = L10n.Tr("逐字符框"), FontSize = 11 };
        _docCompareBox = new CheckBox { Content = L10n.Tr("对比增强前后"), FontSize = 11 };
        _docOutput = new TextBox
        {
            Height = 108, FontSize = 11.5, AcceptsReturn = true, IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = L10n.Tr("识别结果（可选中复制）")
        };
        _docSummary = new TextBlock
        {
            Text = L10n.Tr("未启用文档智能。勾选上方开关后即可使用，首次调用会加载约 31 MB 嵌入模型。"),
            FontSize = 10.5, Opacity = 0.75, TextWrapping = TextWrapping.Wrap
        };
        _docEnhanceButton = Button(L10n.Tr("增强到预览"), OnDocEnhance);
        _docOcrButton = Button(L10n.Tr("识别文字"), OnDocOcr);
        _docPipelineButton = Button(L10n.Tr("增强 + 识别"), OnDocPipeline);
        _docEnabledBox.IsCheckedChanged += (_, _) => OnDocumentToggle();

        var langBox = new ComboBox
        {
            ItemsSource = new[] { "中文", "English" },
            SelectedIndex = L10n.IsEnglish ? 1 : 0,
            Width = 88, FontSize = 11
        };
        langBox.SelectionChanged += (_, _) =>
        {
            string lang = langBox.SelectedIndex == 1 ? "en" : "zh";
            if (string.Equals(lang, L10n.Language, StringComparison.Ordinal)) return;
            AppSettings saved = SettingsService.Load();
            saved.Language = lang;
            SettingsService.Save(saved);
            _status!.Text = L10n.Tr("语言切换将在重启后生效");
        };

        // 右栏内容较长（信息 + 批量 + 处理 + 文档智能），给新模块一个可见的快捷入口：
        // 一键启用并滚动到文档卡片，避免用户以为功能没做。
        var docShortcut = Button(L10n.Tr("文档智能 ↓"), (_, _) => FocusDocumentPanel());
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 10, 10, 6),
            Children =
            {
                openButton, addButton, clearButton,
                Hint(L10n.Tr("解码缩放")), _scaleBox, _orientBox,
                new Separator(),
                Hint(L10n.Tr("导出格式")), _formatBox, Hint(L10n.Tr("质量")), _quality, _exportButton,
                new Separator(), Hint(L10n.Tr("语言")), langBox, docShortcut
            }
        };
        foreach (Control c in toolbar.Children) c.VerticalAlignment = VerticalAlignment.Center;

        // ---------- 左：文件队列 ----------
        _list = new ListBox
        {
            ItemsSource = _items,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<ImageItem>((item, _) => BuildRow(item))
        };
        _list.SelectionChanged += OnSelectionChanged;

        var left = new DockPanel { Margin = new Thickness(0) };
        var leftHeader = new TextBlock
        {
            Text = L10n.Tr("文件队列（可直接拖入）"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(12, 10, 12, 6)
        };
        DockPanel.SetDock(leftHeader, Dock.Top);
        var dropTip = new TextBlock
        {
            Text = L10n.Tr("把 HEIC / TIFF / JPEG 2000 / JBIG2 / QOI / Netpbm\n拖进窗口即可批量加入"),
            FontSize = 11,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 8)
        };
        DockPanel.SetDock(dropTip, Dock.Bottom);
        left.Children.Add(leftHeader);
        left.Children.Add(dropTip);
        left.Children.Add(new ScrollViewer { Content = _list });

        var leftPanel = new Border
        {
            Width = 276,
            CornerRadius = new CornerRadius(8),
            Background = Ui.B(Ui.Panel),
            BorderBrush = Ui.B(Ui.Border),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 4, 4, 4),
            ClipToBounds = true,
            Child = left
        };

        // ---------- 中：预览 + 框选裁剪 ----------
        _view = new Avalonia.Controls.Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _placeholder = new TextBlock
        {
            Text = L10n.Tr("打开或拖入图片开始\n\nAvalonia 自带解码器打不开 HEIC、TIFF、JPEG 2000、JBIG2、QOI、Netpbm\n这里全部由 Sdcb.SimdCodecs 解码，像素直写 WriteableBitmap"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24
        };
        _cropRectVisual = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x2D, 0x8C, 0xFF)),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(56, 0x2D, 0x8C, 0xFF)),
            IsVisible = false
        };
        _cropCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        _cropCanvas.Children.Add(_cropRectVisual);
        _cropCanvas.PointerPressed += OnCropPressed;
        _cropCanvas.PointerMoved += OnCropMoved;
        _cropCanvas.PointerReleased += OnCropReleased;

        _previewGrid = new Grid
        {
            Background = Ui.B(Ui.Preview),
            ClipToBounds = true
        };
        _previewGrid.Children.Add(_placeholder);
        _previewGrid.Children.Add(_view);
        _previewGrid.Children.Add(_cropCanvas);

        _prevFrame = Button(L10n.Tr("◀ 上一帧"), (_, _) => _ = StepFrameAsync(-1));
        _nextFrame = Button(L10n.Tr("下一帧 ▶"), (_, _) => _ = StepFrameAsync(1));
        _playButton = Button(L10n.Tr("播放"), OnPlay);
        _frameLabel = new TextBlock
        {
            Text = L10n.Tr("单帧"), VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12, MinWidth = 150
        };
        _cropBox = new CheckBox
        {
            Content = L10n.Tr("框选裁剪"), VerticalAlignment = VerticalAlignment.Center
        };
        _cropBox.IsCheckedChanged += (_, _) =>
        {
            _cropCanvas.IsHitTestVisible = _cropBox.IsChecked == true;
            _cropLabel!.Text = _cropBox.IsChecked == true
                ? L10n.Tr("在图上拖出矩形即可解码该区域") : "";
        };
        _clearCropButton = Button(L10n.Tr("清除裁剪"), (_, _) =>
        {
            _baseRegion = null;
            _cropLabel!.Text = "";
            _ = ReloadAsync();
        });
        _cropLabel = new TextBlock
        {
            Text = "", VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11, Opacity = 0.75
        };

        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playTimer.Tick += async (_, _) => await StepFrameAsync(1);

        var previewBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 6, 12, 8),
            Children =
            {
                _prevFrame, _frameLabel, _nextFrame, _playButton,
                new Separator(), _cropBox, _clearCropButton, _cropLabel
            }
        };
        foreach (Control c in previewBar.Children)
        {
            if (c is Separator || c is CheckBox) c.VerticalAlignment = VerticalAlignment.Center;
        }

        var center = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(_previewGrid, 0);
        Grid.SetRow(previewBar, 1);
        center.Children.Add(_previewGrid);
        center.Children.Add(previewBar);

        var centerCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Ui.B(Ui.Panel),
            BorderBrush = Ui.B(Ui.Border),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 4),
            ClipToBounds = true,
            Child = center
        };

        // ---------- 右：信息 + 批量转换 ----------
        _info = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 8),
            Spacing = 3
        };
        SetInfoMessage(L10n.Tr("尚未打开文件。\n\n先读文件头（尺寸 / 格式 / 帧数），再按需解码 —— 大文件不会一进门就吃满内存。"));

        _outputDir = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(_settings.OutputDir)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    "SimdCodecs-Output")
                : _settings.OutputDir,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var chooseDir = Button(L10n.Tr("浏览…"), OnChooseOutputDir);
        _allFramesBox = new CheckBox
        {
            Content = L10n.Tr("多帧文件逐帧导出（GIF / APNG / 多页 TIFF）"),
            IsChecked = false,
            FontSize = 11
        };
        _batchButton = Button(L10n.Tr("批量转换队列"), OnBatch);
        _batchButton.Classes.Add("primary");
        _progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8 };
        _batchSummary = new TextBlock
        {
            Text = L10n.Tr("就绪"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.B(Ui.TextSub),
            Opacity = 1, Margin = new Thickness(0, 4, 0, 0)
        };

        // 输出目录行：Grid 两列让文本框受约束收缩，避免长路径把行撑出面板右缘
        var outputDirRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6
        };
        Grid.SetColumn(_outputDir, 0);
        Grid.SetColumn(chooseDir, 1);
        outputDirRow.Children.Add(_outputDir);
        outputDirRow.Children.Add(chooseDir);

        var batchPanel = new StackPanel
        {
            Spacing = 7,
            Margin = new Thickness(0, 2),
            Children =
            {
                new TextBlock { Text = L10n.Tr("批量转换"), FontWeight = FontWeight.SemiBold },
                Hint(L10n.Tr("一键预设（HEIC 转码 / 扫描件拆页）")),
                _presetBox,
                Hint(L10n.Tr("输出目录")),
                outputDirRow,
                _allFramesBox,
                _batchButton,
                _progressBar,
                _batchSummary
            }
        };

        var processPanel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 2),
            Children =
            {
                new TextBlock { Text = L10n.Tr("图像处理（纯托管 · 零原生依赖）"), FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = L10n.Tr("任意尺寸缩放 / 亮度对比度 / 灰度 / 反色 / 几何水印，以及 Gray8·BGR 裸像素导出"),
                    FontSize = 10, Opacity = 0.65, TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Hint(L10n.Tr("尺寸")), _resizeW, Hint("×"), _resizeH, Hint(L10n.Tr("（0=不缩放）")) }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Hint(L10n.Tr("亮度")), _brightness }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Hint(L10n.Tr("对比")), _contrast }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12,
                    Children = { _grayBox, _invertBox }
                },
                _watermark,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Hint(L10n.Tr("裸像素布局")), _layoutBox }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children =
                    {
                        Button(L10n.Tr("应用到预览"), OnApplyProcess),
                        Button(L10n.Tr("导出裸像素…"), OnExportRaw),
                        Button(L10n.Tr("重置"), (_, _) => _ = ReloadAsync())
                    }
                },
                _processSummary
            }
        };

        var documentPanel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 2),
            Children =
            {
                new TextBlock
                {
                    Text = L10n.Tr("文档智能（扫描件增强 / 文字识别）"),
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = L10n.Tr("手机拍文档去阴影与透印，再识别文字；CPU · tiny 模型 · 单帧 ≤ 4 MP"),
                    FontSize = 10, Opacity = 0.65, TextWrapping = TextWrapping.Wrap
                },
                _docEnabledBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12,
                    Children = { _docCtcBox, _docCompareBox }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { _docEnhanceButton, _docOcrButton, _docPipelineButton }
                },
                _docOutput,
                _docSummary
            }
        };

        var rightContent = new StackPanel
        {
            Children =
            {
                _info,
                InsetCard(batchPanel),
                InsetCard(processPanel),
                InsetCard(documentPanel)
            }
        };
        var rightPanel = new Border
        {
            Width = 356,
            CornerRadius = new CornerRadius(8),
            Background = Ui.B(Ui.Panel),
            BorderBrush = Ui.B(Ui.Border),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 4, 8, 4),
            ClipToBounds = true,
            Child = _rightScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rightContent
            }
        };

        // ---------- 状态栏 ----------
        _status = new TextBlock
        {
            Margin = new Thickness(14, 6), FontSize = 12,
            Foreground = Ui.B(Ui.TextSub),
            Text = L10n.IsEnglish ? "Ready · HEIF decoder registered · 0 file(s)" : "就绪 · HEIF 解码器已注册 · 0 个文件"
        };
        var statusBar = new Border
        {
            Background = Ui.B(Ui.Bg),
            BorderBrush = Ui.B(Ui.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status
        };

        var main = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        Grid.SetRow(toolbar, 0);
        Grid.SetColumnSpan(toolbar, 3);
        Grid.SetRow(leftPanel, 1);
        Grid.SetColumn(leftPanel, 0);
        Grid.SetRow(centerCard, 1);
        Grid.SetColumn(centerCard, 1);
        Grid.SetRow(rightPanel, 1);
        Grid.SetColumn(rightPanel, 2);
        Grid.SetRow(statusBar, 2);
        Grid.SetColumnSpan(statusBar, 3);
        main.Children.Add(toolbar);
        main.Children.Add(leftPanel);
        main.Children.Add(centerCard);
        main.Children.Add(rightPanel);
        main.Children.Add(statusBar);

        Content = main;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        _exportButton.IsEnabled = false;
        _prevFrame.IsEnabled = _nextFrame.IsEnabled = _playButton.IsEnabled = false;
        _clearCropButton.IsEnabled = false;

        ApplySettingsToControls();
    }

    // ================= 设置持久化 =================

    /// <summary>启动时把持久化设置应用到控件；越界值回默认，避免设置文件把 UI 弄坏。</summary>
    private void ApplySettingsToControls()
    {
        void SafeSelect(ComboBox box, int index, int max)
        {
            if (index >= 0 && index < max) box.SelectedIndex = index;
        }
        SafeSelect(_scaleBox, _settings.ScaleIndex, 4);
        SafeSelect(_formatBox, _settings.FormatIndex, CodecService.Targets.Count());
        SafeSelect(_presetBox, _settings.PresetIndex, BatchPresets.All.Count + 1);
        SafeSelect(_layoutBox, _settings.LayoutIndex, 4);
        _orientBox.IsChecked = _settings.OrientOnLoad;
        _allFramesBox.IsChecked = _settings.AllFrames;
        _grayBox.IsChecked = _settings.Grayscale;
        _invertBox.IsChecked = _settings.Invert;
        if (_settings.Quality >= 10 && _settings.Quality <= 100) _quality.Value = _settings.Quality;
        if (_settings.Brightness >= -100 && _settings.Brightness <= 100) _brightness.Value = _settings.Brightness;
        if (_settings.Contrast >= 0.2 && _settings.Contrast <= 2.5) _contrast.Value = _settings.Contrast;
        if (_settings.ResizeW > 0) _resizeW.Text = _settings.ResizeW.ToString();
        if (_settings.ResizeH > 0) _resizeH.Text = _settings.ResizeH.ToString();
        if (!string.IsNullOrEmpty(_settings.Watermark)) _watermark.Text = _settings.Watermark;
        _docEnabledBox.IsChecked = _settings.DocumentEnabled;
        _docCtcBox.IsChecked = _settings.DocumentCtc;
        _docCompareBox.IsChecked = _settings.DocumentCompare;
        UpdateDocumentControls();
    }

    /// <summary>窗口关闭时把当前控件状态写回 %APPDATA%/SimdCodecsViewer/settings.json。</summary>
    private void PersistSettings()
    {
        try
        {
            _settings.Language = L10n.Language;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.OutputDir = _outputDir.Text ?? "";
            _settings.ScaleIndex = _scaleBox.SelectedIndex;
            _settings.OrientOnLoad = _orientBox.IsChecked == true;
            _settings.FormatIndex = _formatBox.SelectedIndex;
            _settings.Quality = _quality.Value;
            _settings.PresetIndex = _presetBox.SelectedIndex;
            _settings.AllFrames = _allFramesBox.IsChecked == true;
            _settings.ResizeW = int.TryParse(_resizeW.Text, out int w) ? w : 0;
            _settings.ResizeH = int.TryParse(_resizeH.Text, out int h) ? h : 0;
            _settings.Watermark = _watermark.Text ?? "";
            _settings.Brightness = _brightness.Value;
            _settings.Contrast = _contrast.Value;
            _settings.Grayscale = _grayBox.IsChecked == true;
            _settings.Invert = _invertBox.IsChecked == true;
            _settings.LayoutIndex = _layoutBox.SelectedIndex;
            _settings.DocumentEnabled = _docEnabledBox.IsChecked == true;
            _settings.DocumentCtc = _docCtcBox.IsChecked == true;
            _settings.DocumentCompare = _docCompareBox.IsChecked == true;
            SettingsService.Save(_settings);
        }
        catch
        {
            // 持久化失败不影响退出（磁盘满 / 只读目录等），静默即可。
        }
    }

    // ================= 文件队列 =================

    internal IReadOnlyList<ImageItem> Items => _items;
    internal IImage? PreviewSource => _view.Source;
    internal LoadedImage? Current => _current;
    internal int FrameIndex => _frameIndex;
    internal PixelRect? BaseRegion => _baseRegion;

    // 给无窗口冒烟测试用的入口，避免为了自动化而模拟鼠标事件。
    internal Task DebugLoadAsync(int index)
    {
        _list.SelectedIndex = index;
        return ReloadAsync();
    }

    internal Task DebugStepFrameAsync(int delta) => StepFrameAsync(delta);

    internal void DebugSetRegion(PixelRect? region) => _baseRegion = region;

    internal void AddFiles(IEnumerable<string> paths)
    {
        bool first = _items.Count == 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new ImageItem(path, CodecService.Inspect(path));
            _items.Add(item);
            _ = item.LoadThumbnailAsync();
        }
        _status.Text = $"队列 {_items.Count} 个文件 · HEIF 解码器已注册";
        if (first && _items.Count > 0) _list.SelectedIndex = 0;
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开图片（可多选）",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片") { Patterns = ImagePatterns },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });
        AddFiles(files.Select(f => f.TryGetLocalPath()).Where(p => p is not null)!);
    }

    private async void OnAdd(object? sender, RoutedEventArgs e) => await OpenFilesAsync();

    private async Task OpenFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "追加到队列",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("图片") { Patterns = ImagePatterns } }
        });
        AddFiles(files.Select(f => f.TryGetLocalPath()).Where(p => p is not null)!);
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        _decodeCts?.Cancel();
        _items.Clear();
        DisposePreview();
        _current = null;
        _baseRegion = null;
        _frameIndex = 0;
        _placeholder.IsVisible = true;
        SetInfoMessage("队列已清空。");
        _status.Text = "就绪 · 0 个文件";
        UpdateButtons();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Avalonia 11.3 用 DataTransfer 取代了旧的 IDataObject.Data。
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        IStorageItem[]? files = e.DataTransfer.TryGetFiles();
        if (files is not { Length: > 0 }) return;
        AddFiles(files.Select(f => f.TryGetLocalPath()).Where(p => p is not null)!);
        e.Handled = true;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        StopPlayback();
        _frameIndex = 0;
        _baseRegion = null;
        _cropLabel.Text = "";
        _ = ReloadAsync();
    }

    /// <summary>
    /// 手写属性同步，而不是反射 Binding：Avalonia 的 ReflectionBinding 在裁剪 / NativeAOT 下会报 IL2026，
    /// 纯 C# 界面没有 XAML 生成器可用，这里直接用 INotifyPropertyChanged 保持缩略图与结果刷新。
    /// </summary>
    private static Control BuildRow(ImageItem? item)
    {
        var thumb = new Avalonia.Controls.Image
        {
            Width = 68, Height = 52, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var name = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 12, MaxWidth = 158,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var summary = new TextBlock { FontSize = 10, Opacity = 0.65 };
        var result = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)) };

        if (item is not null)
        {
            thumb.Source = item.Thumbnail;
            name.Text = item.Name;
            summary.Text = item.Summary;
            result.Text = item.Result;
            ToolTip.SetTip(name, item.Path);
            item.PropertyChanged += (_, p) =>
            {
                if (p.PropertyName == nameof(ImageItem.Thumbnail)) thumb.Source = item.Thumbnail;
                else if (p.PropertyName == nameof(ImageItem.Result)) result.Text = item.Result;
            };
        }

        var texts = new StackPanel { Spacing = 1, Children = { name, summary, result } };
        return new Border
        {
            Padding = new Thickness(6),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { thumb, texts } }
        };
    }

    // ================= 解码与显示 =================

    internal async Task ReloadAsync()
    {
        if (_list.SelectedItem is not ImageItem item) return;
        if (!item.Entry.Decodable)
        {
            _status.Text = $"{item.Name}：{item.Entry.Error}";
            SetInfoMessage($"文件 {item.Path}\n\n{item.Entry.Error}", isError: true);
            return;
        }

        _decodeCts?.Cancel();
        var cts = _decodeCts = new CancellationTokenSource();
        int scale = _scaleBox.SelectedIndex switch { 0 => 1, 1 => 2, 2 => 4, _ => 8 };
        bool orient = _orientBox.IsChecked == true;
        string path = item.Path;
        int frame = _frameIndex;
        PixelRect? region = _baseRegion;
        _status.Text = $"正在解码 {item.Name}（1/{scale}{(frame > 0 ? $"，第 {frame + 1} 帧" : "")}）…";

        try
        {
            LoadedImage image = await Task.Run(
                () => CodecService.Load(path, scale, orient, region, frame, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            ApplyImage(image);
            _status.Text = $"解码完成 · {image.DecodeMs:F2} ms · 输出 {image.Width}×{image.Height}" +
                           (region is { } r ? $" · 裁剪 ({r.X},{r.Y},{r.Width},{r.Height})" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status.Text = $"解码失败：{ex.GetType().Name}: {ex.Message}";
            SetInfoMessage(ex.ToString(), isError: true);
        }
    }

    internal static WriteableBitmap ToBitmap(LoadedImage image) => BitmapFactory.Create(image);

    private void ApplyImage(LoadedImage image)
    {
        DisposePreview();
        _current = image;
        _view.Source = ToBitmap(image);
        _placeholder.IsVisible = false;
        SetInfo(BuildInfoPanel(image));
        UpdateFrameControls(image);
        UpdateButtons();
    }

    private void DisposePreview()
    {
        IImage? previous = _view.Source;
        _view.Source = null;
        (previous as IDisposable)?.Dispose();
        _current = null;
    }

    private void UpdateButtons()
    {
        bool has = _current is not null;
        _exportButton.IsEnabled = has;
        _clearCropButton.IsEnabled = has && _baseRegion is not null;
        UpdateDocumentControls();
    }

    private void UpdateDocumentControls()
    {
        bool enabled = _docEnabledBox.IsChecked == true;
        bool ready = enabled && _current is not null && !_docBusy;
        _docEnhanceButton.IsEnabled = ready;
        _docOcrButton.IsEnabled = ready;
        _docPipelineButton.IsEnabled = ready;
        if (!enabled)
            _docSummary.Text = L10n.Tr("未启用文档智能。勾选上方开关后即可使用，首次调用会加载约 31 MB 嵌入模型。");
        else if (_document is null)
            _docSummary.Text = L10n.Tr("文档智能就绪 · 纯 CPU · tiny 模型");
    }

    private void UpdateFrameControls(LoadedImage image)
    {
        int count = Math.Max(1, image.FrameCount);
        bool multi = count > 1;
        _prevFrame.IsEnabled = _nextFrame.IsEnabled = _playButton.IsEnabled = multi;
        _frameLabel.Text = multi
            ? $"第 {image.FrameIndex + 1} / {count} 帧" +
              (image.FrameDuration is { } d ? $" · {d.TotalMilliseconds:F0} ms" : " · 页面")
            : "单帧";
        if (!_playTimer.IsEnabled)
        {
            TimeSpan d2 = image.FrameDuration ?? TimeSpan.FromMilliseconds(100);
            _playTimer.Interval = d2 < TimeSpan.FromMilliseconds(20)
                ? TimeSpan.FromMilliseconds(100) : d2;
        }
    }

    private async Task StepFrameAsync(int delta)
    {
        if (_current is null || _frameLoading) return;
        int count = Math.Max(1, _current.FrameCount);
        if (count <= 1) return;
        _frameLoading = true;
        try
        {
            _frameIndex = (_frameIndex + delta + count) % count;
            await ReloadAsync();
        }
        finally { _frameLoading = false; }
    }

    private void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (_playTimer.IsEnabled) StopPlayback();
        else if (_current is { FrameCount: > 1 })
        {
            _playTimer.Start();
            _playButton.Content = "暂停";
        }
    }

    private void StopPlayback(bool resetLabel = true)
    {
        if (_playTimer.IsEnabled) _playTimer.Stop();
        if (resetLabel) _playButton.Content = "播放";
    }

    // ================= 框选裁剪 =================

    /// <summary>Image(Uniform + DownOnly) 在预览 Grid 中实际绘制的矩形，用于屏幕坐标 -> 像素坐标。</summary>
    private Rect DisplayRect()
    {
        if (_current is null) return default;
        double bw = _previewGrid.Bounds.Width, bh = _previewGrid.Bounds.Height;
        if (bw <= 0 || bh <= 0) return default;
        double s = Math.Min(1.0, Math.Min(bw / _current.Width, bh / _current.Height));
        double w = _current.Width * s, h = _current.Height * s;
        return new Rect((bw - w) / 2, (bh - h) / 2, w, h);
    }

    private void OnCropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_current is null) return;
        _selecting = true;
        _dragStart = e.GetPosition(_cropCanvas);
        e.Pointer.Capture(_cropCanvas);
        _cropRectVisual.IsVisible = true;
        Canvas.SetLeft(_cropRectVisual, _dragStart.X);
        Canvas.SetTop(_cropRectVisual, _dragStart.Y);
        _cropRectVisual.Width = 0;
        _cropRectVisual.Height = 0;
    }

    private void OnCropMoved(object? sender, PointerEventArgs e)
    {
        if (!_selecting) return;
        Point p = e.GetPosition(_cropCanvas);
        var r = new Rect(_dragStart, p);
        Canvas.SetLeft(_cropRectVisual, r.X);
        Canvas.SetTop(_cropRectVisual, r.Y);
        _cropRectVisual.Width = r.Width;
        _cropRectVisual.Height = r.Height;
    }

    private async void OnCropReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
        _cropRectVisual.IsVisible = false;

        Rect screen = new Rect(_dragStart, e.GetPosition(_cropCanvas));
        if (screen.Width < 6 || screen.Height < 6 || _current is null) return;

        Rect disp = DisplayRect();
        if (disp.Width <= 0 || disp.Height <= 0) return;
        double sx = _current.Width / disp.Width, sy = _current.Height / disp.Height;
        int x = (int)Math.Clamp(Math.Floor((screen.X - disp.X) * sx), 0, _current.Width - 1);
        int y = (int)Math.Clamp(Math.Floor((screen.Y - disp.Y) * sy), 0, _current.Height - 1);
        int w = (int)Math.Clamp(Math.Ceiling(screen.Width * sx), 1, _current.Width - x);
        int h = (int)Math.Clamp(Math.Ceiling(screen.Height * sy), 1, _current.Height - y);
        var local = new PixelRect(x, y, w, h);

        // 连续框选时把局部坐标累加到「缩放后的原图」坐标系。
        PixelRect b = _baseRegion ?? new PixelRect(0, 0, _current.Width, _current.Height);
        _baseRegion = new PixelRect(b.X + x, b.Y + y, w, h);
        _cropLabel.Text = $"裁剪区域 ({_baseRegion.Value.X},{_baseRegion.Value.Y}," +
                          $"{_baseRegion.Value.Width},{_baseRegion.Value.Height}) · 解码期只取这块像素";
        await ReloadAsync();
    }

    // ================= 导出 / 批量 =================

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        ExportTarget target = CodecService.Target(_formatBox.SelectedItem as string ?? "JPEG");
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出当前画面",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_current.Path) +
                                (_frameIndex > 0 ? $"-f{_frameIndex + 1:000}" : "") +
                                (_baseRegion is not null ? "-crop" : "") + "-out" + target.Extension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(target.Label) { Patterns = new[] { "*" + target.Extension } }
            }
        });
        if (file is null) return;
        string? output = file.TryGetLocalPath();
        if (output is null) { _status.Text = "无法取得导出路径"; return; }

        LoadedImage snapshot = _current;
        int quality = (int)_quality.Value;
        try
        {
            await Task.Run(() => CodecService.Save(snapshot, output, target, quality));
            _status.Text = $"已导出 {output}（{new FileInfo(output).Length / 1024.0:F1} KB）";
        }
        catch (Exception ex) { _status.Text = $"导出失败：{ex.GetType().Name}: {ex.Message}"; }
    }

    private async void OnChooseOutputDir(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择批量输出目录", AllowMultiple = false
        });
        string? path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) _outputDir.Text = path;
    }

    private async void OnBatch(object? sender, RoutedEventArgs e)
    {
        if (_batchCts is not null)
        {
            _batchCts.Cancel();
            return;
        }
        if (_items.Count == 0) { _status.Text = "队列是空的，先加文件"; return; }
        string dir = _outputDir.Text?.Trim() ?? "";
        if (dir.Length == 0) { _status.Text = "请先选择输出目录"; return; }

        var settings = new BatchSettings(
            dir,
            CodecService.Target(_formatBox.SelectedItem as string ?? "JPEG"),
            Quality: (int)_quality.Value,
            Scale: _scaleBox.SelectedIndex switch { 0 => 1, 1 => 2, 2 => 4, _ => 8 },
            ApplyOrientation: _orientBox.IsChecked == true,
            AllFrames: _allFramesBox.IsChecked == true);

        if (_presetBox.SelectedIndex > 0)
        {
            BatchPreset preset = BatchPresets.All[_presetBox.SelectedIndex - 1];
            settings = BatchPresets.Apply(preset, settings);
            _status.Text = $"应用预设「{preset.Label}」· 只处理 {preset.SourceHint}";
        }

        foreach (ImageItem i in _items) i.ClearResult();
        var map = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        _batchCts = new CancellationTokenSource();
        _batchButton.Content = "取消转换";
        _progressBar.Value = 0;
        _batchSummary.Text = "正在准备…";

        var progress = new Progress<BatchProgress>();
        progress.ProgressChanged += (_, p) =>
        {
            _progressBar.Maximum = Math.Max(1, p.Total);
            _progressBar.Value = p.Done;
            _status.Text = $"批量转换 {p.Done}/{p.Total} · {p.Current}";
            if (p.Last is { } r && map.TryGetValue(r.Input, out ImageItem? item))
            {
                if (r.Ok)
                    item.SetResult($"{Path.GetFileName(r.Output)} · " +
                                   $"{r.OutputBytes / 1024.0:F0} KB · 省 {r.SavedPercent:F0}%", false);
                else if (r.Skipped)
                    item.SetResult("[跳过] " + r.Error, false);
                else
                    item.SetResult("[失败] " + r.Error, true);
            }
        };

        IReadOnlyList<string> files = _items.Select(i => i.Path).ToArray();
        try
        {
            IReadOnlyList<BatchItemResult> results = await BatchService.RunAsync(
                files, settings, progress, _batchCts.Token);
            string summary = BatchService.Summarize(results);
            _batchSummary.Text = summary;
            _status.Text = (_batchCts.IsCancellationRequested ? "已取消 · " : "批量完成 · ") + summary;
        }
        catch (Exception ex)
        {
            _batchSummary.Text = "批量失败：" + ex.Message;
            _status.Text = _batchSummary.Text;
        }
        finally
        {
            _batchCts.Dispose();
            _batchCts = null;
            _batchButton.Content = "批量转换队列";
        }
    }

    // ================= 纯托管处理层 / CV 输入 =================

    private ProcessOptions ReadProcessOptions()
    {
        int rw = int.TryParse(_resizeW.Text, out int w) ? Math.Clamp(w, 0, 16_384) : 0;
        int rh = int.TryParse(_resizeH.Text, out int h) ? Math.Clamp(h, 0, 16_384) : 0;
        PixelLayout layout = _layoutBox.SelectedIndex switch
        {
            1 => PixelLayout.Gray8,
            2 => PixelLayout.Bgr24,
            3 => PixelLayout.Rgb24,
            _ => PixelLayout.Bgra32
        };
        string? mark = string.IsNullOrWhiteSpace(_watermark.Text) ? null : _watermark.Text.Trim();
        return new ProcessOptions(rw, rh, _brightness.Value, _contrast.Value,
            _grayBox.IsChecked == true, _invertBox.IsChecked == true, mark, layout);
    }

    private void OnApplyProcess(object? sender, RoutedEventArgs e)
    {
        if (_current is null) { _status.Text = "先打开一张图片再应用处理"; return; }
        try
        {
            ProcessOptions options = ReadProcessOptions();
            ProcessResult r = ImageProcessor.Apply(
                _current.Pixels, _current.Width, _current.Height, options);
            byte[] display = ImageProcessor.ToDisplayBgra(r);

            // 构造一份新的解码结果，让「导出当前」直接输出处理后的像素。
            var processed = new LoadedImage
            {
                Pixels = display,
                Width = r.Width,
                Height = r.Height,
                Stride = r.Width * 4,
                Info = _current.Info,
                Metadata = _current.Metadata,
                DecodeMs = _current.DecodeMs,
                FileBytes = _current.FileBytes,
                Path = _current.Path,
                Scale = _current.Scale,
                Oriented = _current.Oriented,
                Region = _current.Region,
                FrameIndex = _current.FrameIndex,
                FrameCount = _current.FrameCount,
                FrameDuration = _current.FrameDuration
            };
            ApplyImage(processed);
            CvAnalysis cv = ImageProcessor.AnalyzeForOcr(r);
            _processSummary.Text = $"{r.Summary}\n输出 {r.Width}×{r.Height} · {r.Layout} · " +
                                   $"stride {r.Stride}\n{cv.Summary}";
            _status.Text = "处理完成：" + r.Summary;
        }
        catch (Exception ex)
        {
            _processSummary.Text = "处理失败：" + ex.Message;
            _status.Text = $"处理失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    private async void OnExportRaw(object? sender, RoutedEventArgs e)
    {
        if (_current is null) { _status.Text = "先打开一张图片"; return; }
        try
        {
            ProcessResult r = ImageProcessor.Apply(
                _current.Pixels, _current.Width, _current.Height, ReadProcessOptions());
            CvAnalysis cv = ImageProcessor.AnalyzeForOcr(r);
            string ext = r.Layout.ToString().ToLowerInvariant() + ".raw";
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出裸像素（供 OCR / OpenCV 直接读取）",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_current.Path) + "." + ext,
                FileTypeChoices = new[] { new FilePickerFileType("Raw pixels") { Patterns = new[] { "*.raw" } } }
            });
            string? output = file?.TryGetLocalPath();
            if (output is null) return;

            await File.WriteAllBytesAsync(output, r.Pixels);
            // sidecar 描述布局，避免下游猜宽高 / 通道顺序。
            // 手写 JSON 而不是 JsonSerializer.Serialize(匿名对象)：后者依赖反射，
            // 在 NativeAOT 裁剪下会触发 IL2026 / IL3050，且这里字段固定、完全可控。
            string sidecar = output + ".json";
            string J(string s) => "\"" + System.Text.Json.JsonEncodedText.Encode(s) + "\"";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"layout\": {J(r.Layout.ToString())},");
            sb.AppendLine($"  \"width\": {r.Width},");
            sb.AppendLine($"  \"height\": {r.Height},");
            sb.AppendLine($"  \"stride\": {r.Stride},");
            sb.AppendLine($"  \"bytesPerPixel\": {r.BytesPerPixel},");
            sb.AppendLine($"  \"steps\": [{string.Join(", ", r.Applied.Select(J))}],");
            sb.AppendLine($"  \"ocr\": {{ \"threshold\": {cv.Threshold}, " +
                          $"\"inkRatio\": {cv.InkRatio.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}, " +
                          $"\"textRows\": {cv.TextRows} }}");
            sb.AppendLine("}");
            await File.WriteAllTextAsync(sidecar, sb.ToString());
            _status.Text = $"已导出 {r.Pixels.Length} B 裸像素 + 描述文件 · {r.Layout} · {r.Width}×{r.Height}";
            _processSummary.Text = $"{r.Summary}\n{cv.Summary}\n裸像素已导出：{Path.GetFileName(output)}";
        }
        catch (Exception ex) { _status.Text = $"裸像素导出失败：{ex.Message}"; }
    }

    // ================= 文档智能（增强 / OCR） =================

    /// <summary>opt-in 开关：只构造 Lazy 壳，真正点按钮时才加载约 31 MB 嵌入模型。</summary>
    private void OnDocumentToggle()
    {
        if (_docEnabledBox.IsChecked == true)
        {
            _document ??= new DocumentRuntime(DocumentOptions.ForLocal());
            _docSummary.Text = L10n.Tr("文档智能就绪 · 纯 CPU · tiny 模型");
        }
        else
        {
            _docSummary.Text = L10n.Tr("未启用文档智能。勾选上方开关后即可使用，首次调用会加载约 31 MB 嵌入模型。");
        }
        UpdateDocumentControls();
    }

    /// <summary>取当前预览帧，并在进推理前用 DocumentLimits 卡住大图 / 多帧。</summary>
    private DocumentFrame? CurrentDocumentFrame()
    {
        if (_docEnabledBox.IsChecked != true || _docBusy) return null;
        if (_current is null)
        {
            _status.Text = "先打开一张图片再使用文档智能";
            return null;
        }
        DocumentRuntime runtime = _document ??= new DocumentRuntime(DocumentOptions.ForLocal());
        DocumentLimits.Verdict verdict = runtime.Limits.Validate(
            _current.Width, _current.Height, _current.FrameCount);
        if (!verdict.Accepted)
        {
            _status.Text = verdict.Reason;
            _docSummary.Text = verdict.Reason;
            return null;
        }
        return new DocumentFrame(_current.Pixels, _current.Width, _current.Height,
            _current.Stride, _current.FrameCount);
    }

    private void SetDocBusy(bool busy, string message)
    {
        _docBusy = busy;
        _status.Text = message;
        UpdateDocumentControls();
    }

    /// <summary>
    /// 统一执行入口：所有推理都在 DocumentRuntime 的 Task.Run 上跑，不占 UI 线程；
    /// 按钮期间禁用，超时后由 WaitAsync 提前返回（CPU 内核本身不可中断）。
    /// </summary>
    private async Task<DocumentResult?> RunDocumentAsync(DocumentRequest request)
    {
        DocumentFrame? frame = CurrentDocumentFrame();
        if (frame is null) return null;
        DocumentRuntime runtime = _document!;
        SetDocBusy(true, "文档推理中…（首次调用会加载模型）");
        try
        {
            return await runtime.RunAsync(frame, request).WaitAsync(runtime.Timeout);
        }
        catch (DocumentRejectedException ex)
        {
            _docSummary.Text = ex.Message;
            _status.Text = $"文档推理被拒绝：{ex.Message}";
        }
        catch (DocumentBusyException ex)
        {
            _docSummary.Text = ex.Message;
            _status.Text = "文档推理超时：" + ex.Message;
        }
        catch (TimeoutException)
        {
            _docSummary.Text = $"超过 {runtime.Timeout.TotalSeconds:F0}s 未完成，请换更小的图片。";
            _status.Text = _docSummary.Text;
        }
        catch (Exception ex)
        {
            _docSummary.Text = $"{ex.GetType().Name}: {ex.Message}";
            _status.Text = "文档推理失败：" + _docSummary.Text;
        }
        finally
        {
            _docBusy = false;
            UpdateDocumentControls();
        }
        return null;
    }

    /// <summary>把增强后的 Bgra32 直接换成新的预览帧，「导出当前」即可落盘。</summary>
    private void ApplyEnhancedPreview(DocumentResult r)
    {
        LoadedImage? previous = _current;
        if (previous is null || r.EnhancedPixels is null) return;
        var enhanced = new LoadedImage
        {
            Pixels = r.EnhancedPixels,
            Width = r.Width,
            Height = r.Height,
            Stride = r.Stride,
            Info = previous.Info,
            Metadata = previous.Metadata,
            DecodeMs = previous.DecodeMs,
            FileBytes = previous.FileBytes,
            Path = previous.Path,
            Scale = previous.Scale,
            Oriented = previous.Oriented,
            Region = previous.Region,
            FrameIndex = previous.FrameIndex,
            FrameCount = previous.FrameCount,
            FrameDuration = previous.FrameDuration
        };
        ApplyImage(enhanced);
    }

    private async void OnDocEnhance(object? sender, RoutedEventArgs e)
    {
        DocumentResult? r = await RunDocumentAsync(new DocumentRequest(Enhance: true, Ocr: false));
        if (r?.Enhance is null) return;
        ApplyEnhancedPreview(r);
        _docSummary.Text = $"增强完成 · {r.Enhance.ElapsedMs:F0} ms · {r.Width}×{r.Height} · " +
                           $"{r.Enhance.Backend}{(r.Enhance.Degraded ? "（GPU 降级）" : "")}";
        _status.Text = "文档照片增强已应用到预览，可直接导出当前。";
    }

    private async void OnDocOcr(object? sender, RoutedEventArgs e)
    {
        bool ctc = _docCtcBox.IsChecked == true;
        DocumentResult? r = await RunDocumentAsync(
            new DocumentRequest(Enhance: false, Ocr: true, Ctc: ctc));
        if (r?.Ocr is null) return;
        _docOutput.Text = string.IsNullOrWhiteSpace(r.Ocr.Text) ? "（未检测到文本行）" : r.Ocr.Text;
        int boxes = r.Ocr.Lines.Sum(l => l.Characters?.Length ?? 0);
        _docSummary.Text = $"识别完成 · {r.Ocr.LineCount} 行 / {r.Ocr.CharCount} 字符 · " +
                           $"{r.Ocr.ElapsedMs:F0} ms · {r.Ocr.Backend}" +
                           (ctc ? $" · {boxes} 个估算字符框" : "");
        _status.Text = "文字识别完成，结果已显示在右栏（可选中复制）。";
    }

    private async void OnDocPipeline(object? sender, RoutedEventArgs e)
    {
        bool ctc = _docCtcBox.IsChecked == true;
        bool compare = _docCompareBox.IsChecked == true;
        DocumentResult? r = await RunDocumentAsync(
            new DocumentRequest(Enhance: true, Ocr: true, Compare: compare, Ctc: ctc));
        if (r?.Ocr is null) return;
        ApplyEnhancedPreview(r);
        _docOutput.Text = string.IsNullOrWhiteSpace(r.Ocr.Text) ? "（未检测到文本行）" : r.Ocr.Text;
        string before = r.OriginalOcr is { } o
            ? $"原图 {o.LineCount} 行 / {o.CharCount} 字符 → " : "";
        _docSummary.Text = $"{before}增强后 {r.Ocr.LineCount} 行 / {r.Ocr.CharCount} 字符 · " +
                           $"增强 {r.Enhance?.ElapsedMs:F0} ms + 识别 {r.Ocr.ElapsedMs:F0} ms";
        _status.Text = "增强 + 识别完成：预览已替换为增强图，文本在右栏。";
    }

    /// <summary>工具栏快捷入口：启用模块并把右栏滚到文档卡片。</summary>
    internal void FocusDocumentPanel()
    {
        _docEnabledBox.IsChecked = true;
        _rightScroll.Offset = new Vector(0,
            Math.Max(0, _rightScroll.Extent.Height - _rightScroll.Viewport.Height));
        _status.Text = "文档智能已启用 · 右栏下方可选择「增强 / 识别文字 / 增强 + 识别」";
    }

    /// <summary>GUI 冒烟断言用：文档面板控件已构造且三个动作齐备。</summary>
    internal bool DocumentControlsReady =>
        _docEnabledBox is not null && _docOutput is not null &&
        _docEnhanceButton is not null && _docOcrButton is not null && _docPipelineButton is not null;

    // ================= 小工具 =================

    private static Button Button(string text, EventHandler<RoutedEventArgs> handler)
    {
        var b = new Button { Content = text, Padding = new Thickness(11, 5) };
        b.Click += handler;
        return b;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text, VerticalAlignment = VerticalAlignment.Center,
        FontSize = 11, Opacity = 0.7
    };

    /// <summary>右栏参数面板的内衬块：让「批量转换」「图像处理」在卡片内再分层。</summary>
    private static Border InsetCard(Control content) => new()
    {
        CornerRadius = new CornerRadius(8),
        Background = Ui.B(Ui.PanelAlt),
        BorderBrush = Ui.B(Ui.Border),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(8, 4, 8, 8),
        Padding = new Thickness(10, 8),
        Child = content
    };

    /// <summary>右栏信息面板：分组 + 键值两列网格，值列自动换行。</summary>
    private static StackPanel BuildInfoPanel(LoadedImage im)
    {
        ImageInfo i = im.Info;
        ImageMetadata m = im.Metadata;
        PixelRect? r = im.Region;

        var root = new StackPanel { Spacing = 2 };

        root.Children.Add(GroupHeader("基本信息"));
        InfoRow(root, "文件", Path.GetFileName(im.Path));
        InfoRow(root, "大小", $"{im.FileBytes / 1024.0:F1} KB");
        InfoRow(root, "格式", i.Format.ToString());
        InfoRow(root, "存储尺寸", $"{i.Width} × {i.Height}");
        InfoRow(root, "输出尺寸", $"{im.Width} × {im.Height}  (1/{im.Scale})");
        InfoRow(root, "裁剪区域", r is { } rr ? $"({rr.X},{rr.Y},{rr.Width},{rr.Height}) 解码期裁剪" : "整图");
        InfoRow(root, "通道/位深", $"{i.Channels} ch · {i.BitDepth} bit · {i.SampleFormat}");
        InfoRow(root, "EXIF 方向", i.Orientation.ToString());
        InfoRow(root, "自动转正", im.Oriented ? "是" : "否");
        InfoRow(root, "帧数", $"{im.FrameCount}（当前第 {im.FrameIndex + 1} 帧）");
        InfoRow(root, "帧时长", im.FrameDuration is { } d ? $"{d.TotalMilliseconds:F0} ms" : "—");
        InfoRow(root, "色域", i.ColorEncoding.ToString());
        InfoRow(root, "DPI", i.Resolution is { } res ? res.ToString() : "—");
        InfoRow(root, "内嵌缩略图", i.HasThumbnail ? "有" : "无");

        root.Children.Add(GroupHeader("元数据（导出时原样透传）"));
        InfoRow(root, "EXIF", m.Exif.Length > 0 ? $"{m.Exif.Length} B" : "无");
        InfoRow(root, "XMP", m.Xmp.Length > 0 ? $"{m.Xmp.Length} B" : "无");
        InfoRow(root, "ICC", m.Icc.Length > 0 ? $"{m.Icc.Length} B" : "无");

        root.Children.Add(GroupHeader("解码统计"));
        InfoRow(root, "耗时", $"{im.DecodeMs:F2} ms");
        InfoRow(root, "像素内存", $"{im.Pixels.LongLength / 1024.0:F0} KB · Bgra32 → Bgra8888");

        return root;
    }

    private static TextBlock GroupHeader(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Foreground = Ui.B(Ui.Accent),
        Margin = new Thickness(0, 10, 0, 3)
    };

    private static void InfoRow(StackPanel root, string key, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("88,*") };
        var k = new TextBlock
        {
            Text = key, FontSize = 11.5, LineHeight = 17,
            Foreground = Ui.B(Ui.TextSub),
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(0, 0, 8, 0)
        };
        var v = new TextBlock
        {
            Text = value, FontSize = 11.5, LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.B(Ui.Text),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(k, 0);
        Grid.SetColumn(v, 1);
        row.Children.Add(k);
        row.Children.Add(v);
        root.Children.Add(row);
    }

    private void SetInfo(Control content)
    {
        _info.Children.Clear();
        _info.Children.Add(content);
    }

    private void SetInfoMessage(string message, bool isError = false) => SetInfo(new TextBlock
    {
        Text = message, FontSize = 11.5, LineHeight = 17,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Ui.B(isError ? Ui.Error : Ui.TextSub)
    });
}
