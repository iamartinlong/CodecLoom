using System.Buffers;
using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;

namespace SimdCodecs.Tests;

/// <summary>
/// 测试样本生成。刻意全部在内存里合成，不依赖外部图片文件；真实 HEIC / TIFF 样本是可选增强，
/// 找不到时相关测试会输出跳过说明而不是误判失败（与 --selftest / --apitest 的约定一致）。
/// </summary>
internal static class TestData
{
    public static byte[] Gradient(int w, int h)
    {
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(255 - x * 255 / w);
                px[i + 1] = (byte)(y * 255 / h);
                px[i + 2] = (byte)((x + y) * 255 / (w + h));
                px[i + 3] = 255;
            }
        return px;
    }

    public static byte[] Photo(int w, int h)
    {
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(128 + 100 * Math.Sin(x / 11.0) * Math.Cos(y / 17.0));
                px[i + 1] = (byte)(128 + 90 * Math.Sin((x + y) / 23.0));
                px[i + 2] = (byte)(128 + 80 * Math.Cos(x / 7.0 + y / 13.0));
                px[i + 3] = 255;
            }
        return px;
    }

    /// <summary>白底 + 三条横黑块，模拟三行文本，用于 Otsu 与行投影。</summary>
    public static byte[] Document(int w, int h)
    {
        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = px[i + 1] = px[i + 2] = 250;
            px[i + 3] = 255;
        }
        foreach (int top in new[] { h / 8, h * 3 / 8, h * 5 / 8 })
            for (int y = top; y < top + Math.Max(4, h / 16); y++)
                for (int x = w / 8; x < w * 7 / 8; x++)
                {
                    int i = (y * w + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = 12;
                }
        return px;
    }

    public static byte[] Solid(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a;
        }
        return px;
    }

    /// <summary>CRC 正确的 PNG，只有 IHDR + IEND，却声明 40000×40000，用来验证图片炸弹闸门。</summary>
    public static byte[] PngBomb(int w = 40_000, int h = 40_000)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes.Concat(data).ToArray()));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public static byte[] AnimatedGif(int frames = 3)
    {
        var writer = new ArrayBufferWriter<byte>();
        IImageEncoderSession session = ImageCodecs.CreateEncoder(ImageFormat.Gif, writer);
        const int fw = 64, fh = 48;
        for (int f = 0; f < frames; f++)
        {
            byte[] fp = Solid(fw, fh, (byte)(30 + f * 70), (byte)(60 + f * 50),
                (byte)(200 - f * 60));
            IImageFrameEncoder frame = session.AddFrame(fw, fh, PixelFormat.Bgra32,
                duration: TimeSpan.FromMilliseconds(120));
            frame.WriteRows(fp, fh);
        }
        session.Commit();
        return writer.WrittenSpan.ToArray();
    }

    public static MultipartFormDataContent Form(byte[] file, string name,
        params (string Key, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(file);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(bytes, "file", name);
        foreach ((string key, string value) in fields)
            content.Add(new StringContent(value), key);
        return content;
    }

    public static MultipartFormDataContent FormMany(
        IEnumerable<(byte[] Data, string Name)> files, params (string Key, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        foreach ((byte[] data, string name) in files)
        {
            var bytes = new ByteArrayContent(data);
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(bytes, "files", name);
        }
        foreach ((string key, string value) in fields)
            content.Add(new StringContent(value), key);
        return content;
    }

    /// <summary>真实样本不在发布产物里：从当前目录或程序集目录向上找源码树。</summary>
    public static string? Find(string name)
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
