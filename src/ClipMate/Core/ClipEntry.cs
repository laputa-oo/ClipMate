namespace ClipMate.Core;

/// <summary>
/// 剪贴板条目。
/// 字段命名刻意与 Ditto 的 Main 表保持语义对应，便于对照逆向结论：
///   Ditto.Main.lID            -> Id
///   Ditto.Main.lDate          -> CreatedAt (Unix 秒)
///   Ditto.Main.mText          -> PreviewText
///   Ditto.Main.lParentID      -> GroupId (-1 = 无分组)
///   Ditto.Main.lDontAutoDelete-> Pinned (受保护，永不自动淘汰)
///   Ditto.Main.bIsGroup       -> (分组在 Group 表单独建模)
/// </summary>
public sealed class ClipEntry
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string PreviewText { get; set; } = string.Empty;

    /// <summary>完整内容（文本条目的正文）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>所属分组 ID，-1 表示未分组。</summary>
    public long GroupId { get; set; } = -1;

    /// <summary>是否置顶/受保护（对应 Ditto 的 lDontAutoDelete 与 sticky）。</summary>
    public bool Pinned { get; set; }

    /// <summary>内容字节数。</summary>
    public int SizeBytes { get; set; }

    /// <summary>内容类型：Text / Image / Files。</summary>
    public string Kind { get; set; } = EntryKind.Text;

    /// <summary>是否为图片条目。</summary>
    public bool IsImage => string.Equals(Kind, EntryKind.Image, StringComparison.Ordinal);

    public string Preview
    {
        get
        {
            if (IsImage) return "[图片] " + PreviewText;
            var t = Content.Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 160 ? t[..160] + "…" : t;
        }
    }

    public string SizeText => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes / 1024.0 / 1024.0:0.##} MB"
    };
}

public static class EntryKind
{
    public const string Text = "Text";
    public const string Image = "Image";
    public const string Files = "Files";
}

/// <summary>
/// 分组。逆向结论：Ditto 的分组是邻接表 + 双击进入，且组内条目永不删除。
/// 新程序改为单击进入，并把"永不删除"收敛为更明确的 Pinned 语义。
/// </summary>
public sealed class ClipGroup
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int SortOrder { get; set; }

    /// <summary>该分组内的条目受保护，不参与自动淘汰。</summary>
    public bool Pinned { get; set; } = true;

    /// <summary>未分组这一虚拟分组的固定 ID。与 Ditto 的 lParentID=-1（顶层）保持一致。</summary>
    public const long UngroupedId = -1;

    public static string FormatName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "未分组" : name;
}
