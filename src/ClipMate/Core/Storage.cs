using Microsoft.Data.Sqlite;

namespace ClipMate.Core;

/// <summary>
/// SQLite 存储层。
///
/// 与 Ditto 的关键差异（来自逆向结论）：
/// 1) Ditto 用「MainDeletes 记账表 + 两阶段触发器」做软删除，删 Main 行时磁盘不释放，
///    必须再删 MainDeletes 才会真删 Data —— 一旦第二阶段变慢，磁盘永不下降。
///    本类【单事务内直接 DELETE 主表 + 数据表】，删除即释放。
/// 2) 淘汰后立即执行 PRAGMA incremental_vacuum / VACUUM 并复核文件尺寸。
/// 3) 图片（CF_DIB）默认不入库，从源头掐掉 98.8% 的空间黑洞。
/// </summary>
public sealed class Storage : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _dbPath;

    public Storage(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            // 关键：避免持有长时间写锁，让淘汰清理能与其他读操作并存
            Pooling = false
        }.ToString();

        _conn = new SqliteConnection(cs);
        _conn.Open();
        InitSchema();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void InitSchema()
    {
        // WAL 模式：读写并发更好，且避免频繁全库写放大
        Exec("PRAGMA journal_mode = WAL;");
        Exec("PRAGMA synchronous = NORMAL;");
        // 注意：SQLite 的 auto_vacuum 在 WAL 模式下不生效（仅 DELETE journal 支持），
        // 因此空间回收统一走 ReclaimSpace() 里的 VACUUM，而不是 incremental_vacuum。

        Exec("""
            CREATE TABLE IF NOT EXISTS clips (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at  INTEGER NOT NULL,          -- Unix 秒
                group_id    INTEGER NOT NULL DEFAULT -1,
                pinned      INTEGER NOT NULL DEFAULT 0,
                kind        TEXT    NOT NULL DEFAULT 'Text',
                preview     TEXT    NOT NULL DEFAULT '',
                content     TEXT    NOT NULL DEFAULT '',
                data        BLOB,                      -- 图片原始字节（仅 kind=Image）
                size_bytes  INTEGER NOT NULL DEFAULT 0
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_clips_created ON clips(created_at DESC);");
        Exec("CREATE INDEX IF NOT EXISTS idx_clips_group ON clips(group_id, created_at DESC);");
        Exec("CREATE INDEX IF NOT EXISTS idx_clips_kind ON clips(kind);");

        Exec("""
            CREATE TABLE IF NOT EXISTS groups (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT    NOT NULL,
                created_at  INTEGER NOT NULL,
                sort_order  INTEGER NOT NULL DEFAULT 0,
                pinned      INTEGER NOT NULL DEFAULT 1
            );
            """);
    }

    // ---------- 条目写入 ----------

    public ClipEntry AddText(string text, long groupId = -1, bool pinned = false)
    {
        var now = DateTimeOffset.UtcNow;
        var size = System.Text.Encoding.UTF8.GetByteCount(text);
        var preview = MakePreview(text);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO clips (created_at, group_id, pinned, kind, preview, content, data, size_bytes)
            VALUES ($created, $group, $pinned, 'Text', $preview, $content, NULL, $size);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$group", groupId);
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$preview", preview);
        cmd.Parameters.AddWithValue("$content", text);
        cmd.Parameters.AddWithValue("$size", size);
        var id = (long)cmd.ExecuteScalar()!;

        return new ClipEntry
        {
            Id = id,
            CreatedAt = now,
            Content = text,
            PreviewText = preview,
            GroupId = groupId,
            Pinned = pinned,
            SizeBytes = size,
            Kind = EntryKind.Text
        };
    }

    public ClipEntry AddImage(byte[] imageBytes, string preview, long groupId = -1, bool pinned = false)
    {
        var now = DateTimeOffset.UtcNow;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO clips (created_at, group_id, pinned, kind, preview, content, data, size_bytes)
            VALUES ($created, $group, $pinned, 'Image', $preview, '', $data, $size);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$group", groupId);
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$preview", preview);
        cmd.Parameters.AddWithValue("$data", imageBytes);
        cmd.Parameters.AddWithValue("$size", imageBytes.Length);
        var id = (long)cmd.ExecuteScalar()!;

        return new ClipEntry
        {
            Id = id,
            CreatedAt = now,
            PreviewText = preview,
            GroupId = groupId,
            Pinned = pinned,
            SizeBytes = imageBytes.Length,
            Kind = EntryKind.Image
        };
    }

    // ---------- 查询 ----------

    public IReadOnlyList<ClipEntry> GetEntries(long groupId = -1, string? filter = null, int limit = 200)
    {
        var list = new List<ClipEntry>();
        var sql = "SELECT id, created_at, group_id, pinned, kind, preview, content, size_bytes FROM clips";
        var conds = new List<string>();
        var args = new List<SqliteParameter>();

        if (groupId != -2) // -2 表示"全部"，-1 表示"未分组"
        {
            conds.Add("group_id = $g");
            args.Add(new SqliteParameter("$g", groupId));
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            conds.Add("(content LIKE $f ESCAPE '\\' OR preview LIKE $f ESCAPE '\\')");
            var like = "%" + filter.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            args.Add(new SqliteParameter("$f", like));
        }
        if (conds.Count > 0) sql += " WHERE " + string.Join(" AND ", conds);
        sql += " ORDER BY pinned DESC, created_at DESC, id DESC LIMIT $limit;";
        args.Add(new SqliteParameter("$limit", limit));

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(args);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var kind = rd.GetString(4);
            list.Add(new ClipEntry
            {
                Id = rd.GetInt64(0),
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(1)),
                GroupId = rd.GetInt64(2),
                Pinned = rd.GetInt32(3) != 0,
                Kind = kind,
                PreviewText = rd.IsDBNull(5) ? "" : rd.GetString(5),
                Content = kind == EntryKind.Image ? "" : (rd.IsDBNull(6) ? "" : rd.GetString(6)),
                SizeBytes = rd.GetInt32(7)
            });
        }
        return list;
    }

    public ClipEntry? GetEntry(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, created_at, group_id, pinned, kind, preview, content, size_bytes FROM clips WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        var kind = rd.GetString(4);
        return new ClipEntry
        {
            Id = rd.GetInt64(0),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(1)),
            GroupId = rd.GetInt64(2),
            Pinned = rd.GetInt32(3) != 0,
            Kind = kind,
            PreviewText = rd.IsDBNull(5) ? "" : rd.GetString(5),
            Content = kind == EntryKind.Image ? "" : (rd.IsDBNull(6) ? "" : rd.GetString(6)),
            SizeBytes = rd.GetInt32(7)
        };
    }

    public byte[]? GetImageData(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM clips WHERE id = $id AND kind = 'Image';";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as byte[];
    }

    // ---------- 删除 ----------

    public void Delete(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM clips WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 更新某条记录的 created_at 为当前时间（"最近使用"上浮）。
    /// 粘贴历史条目时调用，避免在列表顶部新增重复记录。
    /// </summary>
    public void Touch(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE clips SET created_at = $t WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteGroup(long groupId)
    {
        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE clips SET group_id = -1 WHERE group_id = $g;";
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM groups WHERE id = $g;";
            cmd.Parameters.AddWithValue("$g", groupId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ---------- 分组 ----------

    public IReadOnlyList<ClipGroup> GetGroups()
    {
        var list = new List<ClipGroup>
        {
            new ClipGroup { Id = ClipGroup.UngroupedId, Name = "未分组", SortOrder = int.MinValue }
        };
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, created_at, sort_order, pinned FROM groups ORDER BY sort_order, name;";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new ClipGroup
            {
                Id = rd.GetInt64(0),
                Name = rd.GetString(1),
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(rd.GetInt64(2)),
                SortOrder = rd.GetInt32(3),
                Pinned = rd.GetInt32(4) != 0
            });
        }
        return list;
    }

    public long AddGroup(string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO groups (name, created_at, sort_order, pinned) VALUES ($n, $t, $s, 1); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$s", GetMaxSortOrder() + 1);
        return (long)cmd.ExecuteScalar()!;
    }

    private int GetMaxSortOrder()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sort_order), 0) FROM groups;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void RenameGroup(long id, string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE groups SET name = $n WHERE id = $id;";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MoveEntry(long entryId, long groupId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE clips SET group_id = $g WHERE id = $id;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    public void SetPinned(long entryId, bool pinned)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE clips SET pinned = $p WHERE id = $id;";
        cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    // ---------- 淘汰与空间回收 ----------

    /// <summary>
    /// 条数 / 时间 / 总字节三重淘汰。
    /// 受保护（pinned 或 在 pinned 分组内）的条目不参与淘汰。
    /// </summary>
    public (int deleted, long bytesBefore, long bytesAfter) Cleanup(AppConfig cfg)
    {
        long before = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
        int deleted = 0;

        using var tx = _conn.BeginTransaction();

        // 保护集：pinned 条目 + pinned 分组内的条目。
        // 淘汰只作用于"未保护"条目；受保护条目（置顶/分组内）永远保留。
        var protectedWhere = """
            pinned = 1 OR group_id IN (SELECT id FROM groups WHERE pinned = 1)
            """;

        // 1) 按时间过期（对应 Ditto 的 ExpiredEntries）
        if (cfg.RetainDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-cfg.RetainDays).ToUnixTimeSeconds();
            deleted += ExecDelete(tx, $"created_at < $c AND NOT ({protectedWhere})",
                new SqliteParameter("$c", cutoff));
        }

        // 2) 按条数（对应 Ditto 的 MaxEntries）——只从"未保护"条目里淘汰
        if (cfg.MaxEntries > 0)
        {
            deleted += ExecDelete(tx, $"""
                NOT ({protectedWhere}) AND id NOT IN (
                    SELECT id FROM clips WHERE NOT ({protectedWhere})
                    ORDER BY created_at DESC, id DESC LIMIT $max
                )
                """,
                new SqliteParameter("$max", cfg.MaxEntries));
        }

        // 3) 按总字节（Ditto 完全没有这一维，是磁盘暴涨的真凶）——只从"未保护"条目里淘汰
        if (cfg.MaxTotalSizeMB > 0)
        {
            var maxBytes = (long)cfg.MaxTotalSizeMB * 1024 * 1024;
            deleted += ExecDelete(tx, $"""
                NOT ({protectedWhere}) AND id IN (
                    SELECT id FROM (
                        SELECT id, SUM(size_bytes) OVER (
                            ORDER BY created_at DESC, id DESC
                        ) AS running
                        FROM clips WHERE NOT ({protectedWhere})
                    ) WHERE running > $maxBytes
                )
                """,
                new SqliteParameter("$maxBytes", maxBytes));
        }

        tx.Commit();

        // 主动回收页空间（Ditto 缺失的一环）
        ReclaimSpace();

        long after = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
        return (deleted, before, after);
    }

    private int ExecDelete(SqliteTransaction tx, string where, params SqliteParameter[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM clips WHERE {where};";
        cmd.Parameters.AddRange(args);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 主动回收空间，使物理文件真正缩小。
    /// 逆向实证：Ditto 的 auto_vacuum 因两阶段删除而无法收缩；
    /// 这里单事务真删 + WAL checkpoint 截断 + VACUUM 重建，把文件压回真实占用。
    /// VACUUM 有开销，调用方（CleanupService）已做 30 秒节流。
    /// </summary>
    public void ReclaimSpace()
    {
        try
        {
            // 1) 先把已有 WAL 检查点落盘，避免残留 WAL 文件占空间
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            // 2) 重建数据库文件，回收已删除行占用的页（WAL 模式下 auto_vacuum 无效，必须用 VACUUM）
            Exec("VACUUM;");
            // 3) VACUUM 的结果写进了 WAL，必须再 checkpoint 一次才能真正落到主库文件缩小
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch
        {
            // 回收失败不影响数据正确性，下次清理再试
        }
    }

    // ---------- 统计 ----------

    public (long count, long totalBytes) GetStats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0) FROM clips;";
        using var rd = cmd.ExecuteReader();
        rd.Read();
        return (rd.GetInt64(0), rd.GetInt64(1));
    }

    private static string MakePreview(string text)
    {
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > 160 ? t[..160] : t;
    }

    public void Dispose()
    {
        try
        {
            ReclaimSpace();
            _conn.Close();
            _conn.Dispose();
        }
        catch { /* ignore */ }
    }
}
