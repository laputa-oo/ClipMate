using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipMate.Core;

/// <summary>
/// 应用配置。对应逆向结论：
/// 1) 条数与字节【双限额】（Ditto 只有条数，被 65 张未压缩截图撑到 104MB）
/// 2) 图片默认不保存（Ditto 的 CF_DIB 占 98.8% 空间）
/// 3) 保留天数（Ditto 的 ExpiredEntries）
/// </summary>
public sealed class AppConfig
{
    /// <summary>历史条数上限（0 表示不限制条数）。</summary>
    public int MaxEntries { get; set; } = 500;

    /// <summary>
    /// 历史总字节上限（MB）。这是 Ditto 完全没有的维度，也是磁盘暴涨的真凶。
    /// 设为 0 表示不限制。默认 64MB 对纯文本来说绰绰有余。
    /// </summary>
    public int MaxTotalSizeMB { get; set; } = 64;

    /// <summary>保留天数（0 表示不按时间过期）。</summary>
    public int RetainDays { get; set; } = 30;

    /// <summary>是否保存图片（CF_DIB/位图）。默认 false —— 直接掐掉 Ditto 98.8% 的空间黑洞。</summary>
    public bool SaveImages { get; set; } = false;

    /// <summary>允许保存的图片总字节上限（MB）。仅当 <see cref="SaveImages"/> 为 true 时生效。</summary>
    public int MaxImageSizeMB { get; set; } = 16;

    /// <summary>单条内容字节上限（KB）。超限直接不保存，避免一条巨型内容占满磁盘。</summary>
    public int MaxSingleEntryKB { get; set; } = 512;

    /// <summary>启动时自动运行（写开机启动项）。</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>主窗口显示快捷键，格式 "Ctrl+Shift+V"。</summary>
    public string HotkeyShow { get; set; } = "Ctrl+Shift+V";

    /// <summary>关闭窗口时最小化到托盘（而不是退出）。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>粘贴后是否从历史中移除该条（一次性剪贴板场景）。</summary>
    public bool RemoveAfterPaste { get; set; } = false;

    /// <summary>双击粘贴后是否自动关闭唤出窗口。默认 true。</summary>
    public bool CloseAfterPaste { get; set; } = true;

    /// <summary>去重：与上一条内容相同则不新增。</summary>
    public bool Deduplicate { get; set; } = true;

    /// <summary>是否在启动时执行一次清理。</summary>
    public bool CleanupOnStartup { get; set; } = true;

    /// <summary>数据库路径（默认在 %LOCALAPPDATA%\ClipMate\clips.db）。</summary>
    public string? DbPath { get; set; }

    /// <summary>上次清理时间，用于避免频繁触发 VACUUM。</summary>
    public DateTimeOffset LastCleanupAt { get; set; } = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>数据目录 %LOCALAPPDATA%\ClipMate（配置、数据库、诊断日志都在这里）。</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipMate");

    private static string ConfigDir => DataDirectory;

    private static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    public static string DatabasePath
    {
        get
        {
            var cfg = Load();
            return !string.IsNullOrWhiteSpace(cfg.DbPath)
                ? cfg.DbPath!
                : Path.Combine(ConfigDir, "clips.db");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg is not null) return cfg;
            }
        }
        catch
        {
            // 配置损坏不应该让程序起不来：退回默认值并留档
            try
            {
                File.WriteAllText(ConfigFile + ".broken." +
                    DateTime.Now.ToString("yyyyMMddHHmmss"), File.ReadAllText(ConfigFile));
            }
            catch { /* 留档失败也不影响启动 */ }
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var tmp = ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, ConfigFile, overwrite: true);
        }
        catch
        {
            // 落盘失败不影响使用内存中的配置
        }
    }
}
