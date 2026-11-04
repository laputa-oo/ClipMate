using System.Runtime.InteropServices;
using System.Text;

namespace ClipMate.Core;

/// <summary>
/// 剪贴板读取器：把系统剪贴板内容转成统一描述。
/// 只读不写；图片默认可被上层策略跳过。
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
    }

    /// <summary>在 STA 线程上读取剪贴板（WinForms 的 Clipboard 需要 STA）。</summary>
    public static Snapshot? Read()
    {
        if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)
            && !Clipboard.ContainsImage()
            && !Clipboard.ContainsFileDropList())
        {
            return null;
        }

        var snap = new Snapshot();

        if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
        {
            var t = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (!string.IsNullOrEmpty(t))
            {
                snap = snap with { HasText = true, Text = t };
            }
        }

        if (Clipboard.ContainsImage())
        {
            using var img = Clipboard.GetImage();
            if (img is not null)
            {
                snap = snap with
                {
                    HasImage = true,
                    ImagePreview = $"{img.Width}×{img.Height}",
                    ImageBytes = EncodePng(img)
                };
            }
        }
        else if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files is { Count: > 0 })
            {
                snap = snap with
                {
                    HasFiles = true,
                    Files = files.Cast<string>().ToList()
                };
            }
        }

        if (!snap.HasText && !snap.HasImage && !snap.HasFiles) return null;
        return snap;
    }

    private static byte[] EncodePng(System.Drawing.Image img)
    {
        // 统一转 PNG 存入，避免 Ditto 那种未压缩 DIB 位图把磁盘撑爆
        using var ms = new MemoryStream();
        img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
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
            using var img = System.Drawing.Image.FromStream(ms);
            Clipboard.SetImage(img);
        }
        catch { /* 忽略 */ }
    }
}
