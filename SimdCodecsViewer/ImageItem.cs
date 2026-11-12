using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace SimdCodecsViewer;

/// <summary>文件队列的一行：文件名 + 头部信息 + 异步缩略图 + 批量处理结果。</summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    private WriteableBitmap? _thumbnail;
    private string _result = "";
    private bool _failed;

    public ImageItem(string path, ImageEntry entry)
    {
        Path = path;
        Entry = entry;
    }

    public string Path { get; }
    public ImageEntry Entry { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Summary => Entry.Summary;

    public WriteableBitmap? Thumbnail
    {
        get => _thumbnail;
        private set { _thumbnail = value; OnPropertyChanged(); }
    }

    public string Result
    {
        get => _result;
        private set { _result = value; OnPropertyChanged(); }
    }

    public bool Failed
    {
        get => _failed;
        private set { _failed = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>缩略图解码在线程池，WriteableBitmap 的创建也要避开 UI 线程占用。</summary>
    public async Task LoadThumbnailAsync(CancellationToken ct = default)
    {
        try
        {
            (byte[] pixels, int w, int h, int stride) = await Task.Run(
                () => CodecService.LoadThumbnail(Path, 88, ct), ct);
            ct.ThrowIfCancellationRequested();
            Thumbnail = BitmapFactory.Create(pixels, w, h, stride);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Result = "缩略图失败：" + ex.GetType().Name;
            Failed = true;
        }
    }

    public void SetResult(string text, bool failed)
    {
        Result = text;
        Failed = failed;
    }

    public void ClearResult() => SetResult("", false);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
