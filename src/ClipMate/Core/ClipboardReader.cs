using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClipMate.Core;

/// <summary>
/// 剪贴板读取器：把系统剪贴板内容转成统一描述。
/// 只读不写；图片默认可被上层策略跳过。
///
/// 图片格式兼容性说明：
/// 不同截图工具写剪贴板的方式不一样 —— 系统截图（Win+Shift+S）写标准 CF_DIB；
/// 企业微信 / 微信截图常写自定义 "PNG" 格式，部分工具只写裸 DIB 或 Bitmap。
/// 标准 <see cref="Clipboard.ContainsImage"/> 只认 CF_DIB/Bitmap，
/// 因此这里按优先级逐个兜底，否则会出现"截图后条目没进列表"。
/// </summary>
public sealed class ClipboardReader
{
    /// <summary>读取到的剪贴板快照。</summary>
    public sealed record Snapshot
    {
        public bool HasText { get; init; }
        public string? Text { get; init; }
        public bool HasImage { get; init; }
        public byte[]? ImageBytes { get; init; }
        public string? ImagePreview { get; init; }
        public bool HasFiles { get; init; }
        public IReadOnlyList<string>? Files { get; init; }

        /// <summary>命中图片时的来源格式名，便于排查"某工具截图读不到"。</summary>
        public string? ImageSourceFormat { get; init; }
    }

    /// <summary>截图工具常用的自定义 PNG 格式名（大小写敏感，按实际注册名匹配）。</summary>
    private static readonly string[] PngFormatCandidates =
    {
        "PNG", "image/png", "PNG Image", "Portable Network Graphics"
    };

    /// <summary>在 STA 线程上读取剪贴板（WinForms 的 Clipboard 需要 STA）。</summary>
    public static Snapshot? Read()
    {
        // 剪贴板是全局独占资源：别的进程正在写时会 OpenClipboard 失败。
        // 截图工具刚写完的瞬间最容易撞上，重试几次即可。
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return ReadCore();   // 读到内容或确认是空剪贴板都直接返回
            }
            catch (ExternalException)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
        return null;
    }

    private static Snapshot? ReadCore()
    {
        IDataObject? data;
        try
        {
            data = Clipboard.GetDataObject();
        }
        catch (ExternalException)
        {
            throw;   // 交给上层重试
        }
        if (data is null) return null;

        var snap = new Snapshot();

        // ---------- 文本 ----------
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                var t = data.GetData(DataFormats.UnicodeText) as string;
                if (!string.IsNullOrEmpty(t))
                {
                    snap = snap with { HasText = true, Text = t };
                }
            }
        }
        catch (ExternalException) { throw; }

        // ---------- 图片（多格式兜底）----------
        var (img, fmt) = TryGetImage(data);
        if (img is not null)
        {
            using (img)
            {
                snap = snap with
                {
                    HasImage = true,
                    ImagePreview = $"{img.Width}×{img.Height}",
                    ImageBytes = EncodePng(img),
                    ImageSourceFormat = fmt
                };
            }
        }
        else if (data.GetDataPresent(DataFormats.FileDrop))
        {
            // 只在没有图片时才把文件列表当内容，避免"复制图片文件"变成纯路径文本
            if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                snap = snap with { HasFiles = true, Files = files };
            }
        }

        if (!snap.HasText && !snap.HasImage && !snap.HasFiles) return null;
        return snap;
    }

    /// <summary>按优先级尝试从剪贴板取出图片，返回图片与命中的格式名。</summary>
    private static (Image? img, string? fmt) TryGetImage(IDataObject data)
    {
        // 1) 标准位图（CF_DIB / CF_BITMAP）：系统截图、大多数工具走这条路
        try
        {
            if (Clipboard.ContainsImage())
            {
                var i = Clipboard.GetImage();
                if (i is not null) return (i, "CF_DIB");
            }
        }
        catch (ExternalException) { throw; }
        catch { /* 单个格式失败不影响继续兜底 */ }

        // 2) 自定义 PNG 格式：企业微信 / 微信截图常见
        foreach (var name in PngFormatCandidates)
        {
            try
            {
                if (!data.GetDataPresent(name)) continue;
                var bytes = ExtractBytes(data.GetData(name));
                if (bytes is { Length: > 8 })
                {
                    var i = Image.FromStream(new MemoryStream(bytes));
                    return (i, name);
                }
            }
            catch { /* 下一个候选 */ }
        }

        // 3) DataFormats.Bitmap（部分工具只登记这一个）
        try
        {
            if (data.GetDataPresent(DataFormats.Bitmap)
                && data.GetData(DataFormats.Bitmap) is Image i)
            {
                return (i, "Bitmap");
            }
        }
        catch { }

        // 4) 裸 DIB（缺 BITMAPFILEHEADER，需补齐后解析）
        try
        {
            if (data.GetDataPresent(DataFormats.Dib))
            {
                var dib = ExtractBytes(data.GetData(DataFormats.Dib));
                if (dib is { Length: > 40 })
                {
                    var bmp = DibToBitmap(dib);
                    if (bmp is not null) return (bmp, "DIB-raw");
                }
            }
        }
        catch { }

        return (null, null);
    }

    /// <summary>剪贴板里的图片数据可能是 Stream / MemoryStream / byte[]，统一取字节。</summary>
    private static byte[]? ExtractBytes(object? raw)
    {
        switch (raw)
        {
            case byte[] b:
                return b;
            case MemoryStream ms:
                return ms.ToArray();
            case Stream s:
                using (var copy = new MemoryStream())
                {
                    s.CopyTo(copy);
                    return copy.ToArray();
                }
            default:
                return null;
        }
    }

    /// <summary>给裸 DIB 补 14 字节 BITMAPFILEHEADER，使其能被 <see cref="Bitmap"/> 解析。</summary>
    private static Bitmap? DibToBitmap(byte[] dib)
    {
        try
        {
            const int fileHeaderSize = 14;
            // DIB 头部第 0..3 字节是 biSize
            var biSize = BitConverter.ToInt32(dib, 0);
            if (biSize < 12) return null;

            // bfOffBits = 14 + biSize + 调色板/掩码长度
            int offBits = fileHeaderSize + biSize;
            if (biSize == 40)   // BITMAPINFOHEADER 可能有调色板
            {
                var bitCount = BitConverter.ToInt16(dib, 14);
                var clrUsed = BitConverter.ToInt32(dib, 32);
                if (bitCount <= 8)
                {
                    var colors = clrUsed > 0 ? clrUsed : (1 << bitCount);
                    offBits += (int)colors * 4;
                }
            }

            var total = fileHeaderSize + dib.Length;
            var buf = new byte[total];
            buf[0] = 0x42;  // 'B'
            buf[1] = 0x4D;  // 'M'
            BitConverter.GetBytes(total).CopyTo(buf, 2);
            BitConverter.GetBytes(Math.Min(offBits, total)).CopyTo(buf, 10);
            Buffer.BlockCopy(dib, 0, buf, fileHeaderSize, dib.Length);

            using var ms = new MemoryStream(buf);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncodePng(Image img)
    {
        // 统一转 PNG 存入，避免 Ditto 那种未压缩 DIB 位图把磁盘撑爆
        using var ms = new MemoryStream();
        img.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>把内容写回剪贴板（用于"粘贴该条"）。</summary>
    public static void WriteText(string text)
    {
        try { Clipboard.SetText(text, TextDataFormat.UnicodeText); }
        catch (ExternalException) { /* 剪贴板被占用，忽略 */ }
    }

    public static void WriteImage(byte[] pngBytes)
    {
        try
        {
            using var ms = new MemoryStream(pngBytes);
            using var img = Image.FromStream(ms);
            Clipboard.SetImage(img);
        }
        catch { /* 忽略 */ }
    }
}
