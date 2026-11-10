namespace ClipMate.Core;

/// <summary>
/// 应用核心协调器：把剪贴板监听、存储、淘汰策略串成一个整体。
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private readonly Storage _storage;
    private ClipboardWatcher? _watcher;

    /// <summary>
    /// 自己写剪贴板后的一小段"静默期"。
    /// Clipboard.SetImage/SetText 内部可能分多次提交（清空→写数据），
    /// 每次都会触发一条通知，全部用时间窗一次性吞掉最可靠。
    ///
    /// 注意：这里**只**用时间窗判断，不再用"记住上次写入内容再比对"的旧方案。
    /// 旧方案会留下 _lastWrittenWasImage 之类的残留标记：一旦通知被时间窗提前拦截，
    /// 标记就没人清理，之后真实的复制/截图会被误判成"自己写的"而整条丢弃
    /// （企业微信截图不入库就是这个原因）。
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
        ClipDiag.Log(
            $"service: 开始监听 | 版本={typeof(ClipboardService).Assembly.GetName().Version} " +
            $"| SaveImages={Config.SaveImages} | 图片上限={Config.MaxImageSizeMB}MB " +
            $"| 去重={Config.Deduplicate} | 热键={Config.HotkeyShow}");

        _watcher?.Dispose();
        _watcher = new ClipboardWatcher(OnClipboardChanged);

        if (Config.CleanupOnStartup)
        {
            TryCleanup();
        }
    }

    private void OnClipboardChanged()
    {
        ClipDiag.Log("notify: 收到 WM_CLIPBOARDUPDATE");

        // 自己刚写完剪贴板，这一波通知全部跳过（不分几条）
        if (DateTimeOffset.UtcNow < _suppressUntil)
        {
            ClipDiag.Log($"  -> 处于自我写入静默期，跳过（剩余 {(int)(_suppressUntil - DateTimeOffset.UtcNow).TotalMilliseconds}ms）");
            return;
        }

        // 必须回到 UI(STA) 线程读剪贴板，这里由调用方保证：监听窗口本身就在 UI 线程创建。
        var snap = ClipboardReader.Read();
        if (snap is null)
        {
            ClipDiag.Log("  -> 读取结果为空（详见上一行 formats）");
            return;
        }

        ClipEntry? entry = null;

        if (snap.HasText && !string.IsNullOrEmpty(snap.Text))
        {
            // 去重
            if (Config.Deduplicate && IsDuplicateText(snap.Text)) return;
            entry = _storage.AddText(snap.Text);
        }
        else if (snap.HasImage && snap.ImageBytes is not null)
        {
            if (!Config.SaveImages)
            {
                ClipDiag.Log("  -> 图片被跳过：设置里「保存图片」未开启");
                return;
            }
            if (Config.MaxImageSizeMB > 0 &&
                snap.ImageBytes.Length > (long)Config.MaxImageSizeMB * 1024 * 1024)
            {
                ClipDiag.Log($"  -> 图片被跳过：{snap.ImageBytes.Length / 1024}KB 超过单张上限 {Config.MaxImageSizeMB}MB");
                return;
            }
            // 去重：复制一次图片常会触发多次剪贴板通知（先写 DIB 再写 PNG 等），
            // 若不去重会在列表里冒出多条一模一样的图片记录。
            if (Config.Deduplicate && IsDuplicateImage(_storage, snap.ImageBytes, snap.ImagePreview))
            {
                ClipDiag.Log("  -> 图片被判重复，跳过新增");
                return;
            }
            entry = _storage.AddImage(snap.ImageBytes, snap.ImagePreview ?? "图片");
            ClipDiag.Log($"  -> 已保存图片条目 #{entry.Id} {snap.ImagePreview} ({snap.ImageSourceFormat})");
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
    /// 判定顺序（先精确、后兜底），避免误杀"用户连续截同一区域两次"：
    ///   1. 尺寸（"宽×高"）不同 → 一定是另一张图；
    ///   2. 字节长度相同：比 SHA256，完全相同才算重复，不同则明确是另一张图（直接返回 false）；
    ///   3. 字节长度不同：可能是同一图片被不同编码器重编码，
    ///      仅在 1.5 秒内（同一次复制产生的多条通知）才判重复。
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

        var prev = storage.GetImageData(last.Id);

        // 长度相同 → 精确比内容，能明确区分"同一张"还是"另一张"
        if (prev is not null && prev.Length == data.Length)
        {
            var same = System.Security.Cryptography.SHA256.HashData(prev)
                .AsSpan()
                .SequenceEqual(System.Security.Cryptography.SHA256.HashData(data));
            return same;
        }

        // 长度不同：同一图片被重编码时会出现这种情况。
        // 只在极短窗口内（同一次复制操作的多条通知，通常间隔几十毫秒）才判重复。
        return DateTimeOffset.UtcNow - last.CreatedAt < TimeSpan.FromSeconds(1.5);
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
            ClipboardReader.WriteImage(data);
        }
        else
        {
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
