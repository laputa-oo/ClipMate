namespace ClipMate.Core;

/// <summary>
/// 剪贴板诊断日志。
/// 场景：某些截图工具（如企业微信）把图片以非常规格式写进剪贴板，
/// 程序认不出时表现为"截图完成了但列表里没有条目"。
/// 这里把每次剪贴板变化时登记的所有格式名列下来，便于定位到底是哪种格式。
///
/// 隐私约束：只记录格式名、布尔结果和图片尺寸，**绝不记录剪贴板正文**。
/// 文件超过上限自动清空，避免无限增长。
/// </summary>
internal static class ClipDiag
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string LogPath { get; } = Path.Combine(AppConfig.DataDirectory, "clipboard.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.DataDirectory);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    fi.Delete();
                }
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断失败绝不能影响主流程
        }
    }

    /// <summary>安全地取格式名列表（个别 IDataObject 实现取格式时会抛异常）。</summary>
    public static string FormatList(IDataObject? data)
    {
        try
        {
            var formats = data?.GetFormats();
            return formats is { Length: > 0 } ? string.Join(" | ", formats) : "(none)";
        }
        catch (Exception ex)
        {
            return "(GetFormats 异常: " + ex.GetType().Name + ")";
        }
    }
}
