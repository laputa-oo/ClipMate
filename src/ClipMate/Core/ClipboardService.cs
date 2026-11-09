namespace ClipMate.Core;

/// <summary>
/// 应用核心协调器：把剪贴板监听、存储、淘汰策略串成一个整体。
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private readonly Storage _storage;
    private ClipboardWatcher? _watcher;

    // 程序自己写剪贴板时（粘贴历史条目）记录的内容，用于在监听回调里识别并跳过，避免重复捕获
    private string? _lastWrittenText;
    private bool _lastWrittenWasImage;

    /// <summary>
    /// 自己写剪贴板后的一小段"静默期"。
    /// Clipboard.SetImage/SetText 内部可能分多次提交（清空→写数据），
    /// 每次都会触发一条通知，只靠"跳过下一次"会漏掉后续通知而多记一条。
    /// </summary>
    private DateTimeOffset _suppressUntil = DateTimeOffset.MinValue;

    public AppConfig Config { get; }
    public Storage Storage => _storage;

    /// <summary>新条目入库时触发（供 UI 刷新）。</summary>
    public event Action<ClipEntry>? EntryAdded;

    public ClipboardService()
    {
        Config = AppConfig.Load();
        _storage = new Storage(AppConfig.DatabasePath);
    }

    /// <summary>启动监听。注意：Clipboard API 需要 STA 线程。</summary>
    public void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = new ClipboardWatcher(OnClipboardChanged);

        if (Config.CleanupOnStartup)
        {
            TryCleanup();
        }
    }

    private void OnClipboardChanged()
    {
        // 自己刚写完剪贴板，这一波通知全部跳过（不分几条）
        if (DateTimeOffset.UtcNow < _suppressUntil) return;

        // 必须回到 UI(STA) 线程读剪贴板，这里由调用方保证：监听窗口本身就在 UI 线程创建。
        var snap = ClipboardReader.Read();
        if (snap is null) return;

        // 识别"自己刚粘贴的内容"：跳过，不新增重复记录，只更新时间由 Paste() 负责。
        if (_lastWrittenText is not null && snap.HasText && snap.Text == _lastWrittenText)
        {
            _lastWrittenText = null;
            return;
        }
        if (_lastWrittenWasImage && snap.HasImage)
        {
            _lastWrittenWasImage = false;
            return;
        }
        // 未匹配到自己的写入（可能已被后续复制覆盖），清除残留标记
        _lastWrittenText = null;
        _lastWrittenWasImage = false;

        ClipEntry? entry = null;

        if (snap.HasText && !string.IsNullOrEmpty(snap.Text))
        {
            // 去重
            if (Config.Deduplicate && IsDuplicateText(snap.Text)) return;
            entry = _storage.AddText(snap.Text);
        }
        else if (snap.HasImage && snap.ImageBytes is not null)
        {
            if (!Config.SaveImages) return;                       // 默认跳过图片（逆向结论）
            if (Config.MaxImageSizeMB > 0 &&
                snap.ImageBytes.Length > (long)Config.MaxImageSizeMB * 1024 * 1024) return;
            // 去重：复制一次图片常会触发多次剪贴板通知（先写 DIB 再写 PNG 等），
            // 若不去重会在列表里冒出多条一模一样的图片记录。
            if (Config.Deduplicate && IsDuplicateImage(_storage, snap.ImageBytes, snap.ImagePreview)) return;
            entry = _storage.AddImage(snap.ImageBytes, snap.ImagePreview ?? "图片");
        }
        else if (snap.HasFiles && snap.Files is { Count: > 0 })
        {
            var text = string.Join(Environment.NewLine, snap.Files);
            entry = _storage.AddText(text);
        }

        if (entry is not null)
        {
            EntryAdded?.Invoke(entry);
            MaybeCleanup();
        }
    }

    private bool IsDuplicateText(string text)
    {
        // 查最近一条文本内容是否相同（查全部，不分分组）
        var latest = _storage.GetEntries(groupId: -2, limit: 1);
        if (latest.Count == 0) return false;
        var last = latest[0];
        if (last.IsImage) return false;
        return last.Content == text;
    }

    /// <summary>
    /// 判断刚读到的图片是否与最近一条图片重复。
    /// 复制一次图片时，源程序往往分多次写剪贴板（先 DIB、后 PNG 等），
    /// 每次都会触发一条 WM_CLIPBOARDUPDATE，若不去重就会出现多条相同记录。
    ///
    /// 判定策略（按代价从低到高）：
    ///   1. 尺寸（"宽×高"）必须相同；
    ///   2. PNG 字节长度相同 → 判重复；
    ///   3. 尺寸相同且距上条不足 3 秒 → 判重复（覆盖同一图片被重新编码、字节不一致的情况）；
    ///   4. 否则比 SHA256。
    ///
    /// 声明为 public static 是为了让自测无需构造整个 Service 就能验证（不涉及外部状态）。
    /// </summary>
    public static bool IsDuplicateImage(Storage storage, byte[] data, string? preview)
    {
        var latest = storage.GetEntries(groupId: -2, limit: 1);
        if (latest.Count == 0) return false;
        var last = latest[0];
        if (!last.IsImage) return false;

        // 尺寸不同一定是另一张图
        if (!string.IsNullOrEmpty(preview) && last.PreviewText != preview) return false;

        // 字节长度相同基本可认定同一张图
        if (last.SizeBytes == data.Length) return true;

        // 同一图片被不同编码器重编码时字节会变，用时间窗口兜底
        if (DateTimeOffset.UtcNow - last.CreatedAt < TimeSpan.FromSeconds(3)) return true;

        // 最后比内容哈希
        var prev = storage.GetImageData(last.Id);
        if (prev is null || prev.Length != data.Length) return false;
        return System.Security.Cryptography.SHA256.HashData(prev)
            .AsSpan()
            .SequenceEqual(System.Security.Cryptography.SHA256.HashData(data));
    }

    /// <summary>
    /// 粘贴历史条目：把内容写回剪贴板，同时更新原记录时间（上浮到顶部），
    /// 而非新增一条重复记录。若配置 RemoveAfterPaste 则改为删除。
    /// </summary>
    public void Paste(ClipEntry entry)
    {
        // 写剪贴板会产生多条通知，开一个 800ms 静默期把这些噪声全吞掉
        _suppressUntil = DateTimeOffset.UtcNow.AddMilliseconds(800);

        if (entry.IsImage)
        {
            var data = _storage.GetImageData(entry.Id);
            if (data is null) return;
            _lastWrittenText = null;
            _lastWrittenWasImage = true;
            ClipboardReader.WriteImage(data);
        }
        else
        {
            _lastWrittenText = entry.Content;
            _lastWrittenWasImage = false;
            ClipboardReader.WriteText(entry.Content);
        }

        if (Config.RemoveAfterPaste)
        {
            _storage.Delete(entry.Id);
        }
        else
        {
            _storage.Touch(entry.Id);   // 更新时间，等价于"把原记录上浮"
        }
    }

    /// <summary>节流清理：距上次清理不足 30 秒则跳过，避免频繁 VACUUM。</summary>
    public void MaybeCleanup()
    {
        if (Config.LastCleanupAt > DateTimeOffset.UtcNow.AddSeconds(-30)) return;
        TryCleanup();
    }

    public (int deleted, long before, long after) TryCleanup()
    {
        var result = _storage.Cleanup(Config);
        Config.LastCleanupAt = DateTimeOffset.UtcNow;
        Config.Save();
        return result;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _storage.Dispose();
    }
}
