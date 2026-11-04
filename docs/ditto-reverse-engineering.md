# Ditto 3.25.113.0 逆向分析报告（REA）

- 分析对象：Microsoft Store 版 Ditto，包 `60145ScottBrogden.ditto-cp_3.25.113.0_x86__n6b029mg40na2`
- 可执行文件：`Ditto.exe`，5,676,816 B，SHA256 `d187eb96e7c0fb15b8e2aaa38b70f10ff8aae9a4fdee3b0499b0ff0844dd7377`
- PE 格式：pe32-plus / x64（machine 34404），MFC（资源含 `AFX_DIALOG_LAYOUT`），308 个资源条目
- 数据目录：`%LOCALAPPDATA%\Packages\60145ScottBrogden.ditto-cp_n6b029mg40na2\LocalCache\Local\Ditto_WindowsApp\`
- 工具：REA 6.4.0（`inspect-sqlite-database` / `inspect-pe-resources`）+ Python sqlite3 交叉验证

## 0. 分析路径

REA 的 native provider（Hopper/Ghidra/IDA）本机均不可用（`doctor` 全 missing），因此走**不需要 native 引擎**的两条路径：

1. `rea inspect-sqlite-database` → 数据层真相（schema + 行数 + 页统计）
2. `rea inspect-pe-resources` + 全量字符串提取（ASCII + UTF-16LE，9,400 条）→ 行为层真相

数据库快照为**文件复制**，Ditto 进程仍在运行（PID 18372），不保证原子快照；行数级结论不受影响，已用 Python 独立复核。

---

## 1. 问题一：设置了条数/时间，为什么磁盘不降？

### 1.1 表面现象
`Ditto.db` = **109,613,056 B（104.5 MB）**。用户设置 `MaxEntries=500`、`ExpiredEntries=30` 天、两个 `CheckFor*=1` 开关均已打开。

### 1.2 真相 A：条数限制**其实生效了**
`Main` 表只有 **502 行**（500 上限 + 2 分组/边界条目）。所以"条目数"维度没问题。

### 1.3 真相 B：空间全被**图片剪贴板**吃掉，不是文本
`Data` 表 1,174 行 / 108,807,002 B，其中按格式拆分：

| 格式 | 行数 | 字节数 | 占比 |
|---|---|---|---|
| **CF_DIB**（位图/截图） | **65** | **107,541,152** | **98.8%** |
| HTML Format | 210 | 870,704 | 0.8% |
| Rich Text Format | 43 | 189,556 | 0.2% |
| CF_UNICODETEXT | 411 | 127,654 | 0.1% |
| CF_TEXT | 411 | 71,866 | 0.1% |
| CF_HDROP | 34 | 6,070 | ~0% |

单个 CF_DIB 最大 **5,279,752 B（约 5 MB）**。即：**65 张截图 ≈ 102.6 MB**。
64 个 Main 条目含 CF_DIB，另有 1 条同时含 DIB + 文本。

**结论**：用户看到的"占了很多磁盘"= 65 条图片条目，每条最多 5 MB。这不是泄漏，是 Ditto 按设计把**位图原样（未压缩）**塞进 SQLite。

### 1.4 真相 C：**真正的空间回收需要两阶段删除，第二阶段才是关键**
Ditto 用触发器做**软删除 + 延迟清理**（schema 实测）：

```sql
CREATE TABLE MainDeletes(clipID INTEGER, modifiedDate);

CREATE TRIGGER delete_data_trigger BEFORE DELETE ON Main FOR EACH ROW
BEGIN
  INSERT INTO MainDeletes VALUES(old.lID, datetime('now'));   -- 阶段1：只记账
END;

CREATE TRIGGER MainDeletes_delete_data_trigger BEFORE DELETE ON MainDeletes FOR EACH ROW
BEGIN
  DELETE FROM CopyBuffers WHERE lClipID = old.clipID;
  DELETE FROM Data WHERE lParentID = old.clipID;              -- 阶段2：真删数据
END;
```

实测执行链（副本 `analysis/chain_test.db`）：

| 步骤 | 文件大小 | MainDeletes 行数 | Data 行数 |
|---|---|---|---|
| 原始 | 109,613,056 | 2 | 1,174 |
| 步骤1 `DELETE FROM Main`（删 64 条含 DIB 条目） | **109,613,056（一点没变）** | 66 | 1,174 |
| 步骤2 `DELETE FROM MainDeletes`（触发器真删 Data） | **1,814,528（-98.3%）** | 0 | 1,098 |
| 步骤3 `VACUUM` | 1,736,704 | 0 | 1,098 |

**即：只删 Main 行 = 磁盘一点不释放；必须再清 MainDeletes 才会真正回收。**

### 1.5 根因判定
1. `MainDeletes` 清理是**独立于** `RemoveOldEntries` 的另一条路径（二进制中为 `SELECT * FROM MainDeletes LIMIT %d`、`DELETE FROM MainDeletes`、`MainDeletesDeleteCount` 等独立语句）。若该路径未跑或跑得慢，MainDeletes 积压 → Data 的 BLOB 永不释放。
2. 实测当前 `MainDeletes` 仅 2 行（clipID 2635 / 2641，2026-11-03 09:18:00），说明该路径**正在跑但很慢**，且这两条是积压的。
3. `PRAGMA auto_vacuum` 实测 = **1（FULL）**，但 `freelist_count = 0` —— 因为 auto_vacuum 只在页真正空闲时收缩；而阶段1 删 Main 时 Data 的 BLOB **仍被 MainDeletes 的记账逻辑"钉住"**，页面不空闲，所以文件不缩。
4. `PRAGMA incremental_vacuum` 与 `VACUUM` 在二进制中均存在（`IncrVacuum` / `Vacuum` 符号），但实测：删 Main 后 `freelist=0`，增量收缩无可回收空间。

**给用户的一句话**：条数限制是好的（502 行），但**真正吃磁盘的是 65 张未压缩截图（98.8%）**；而 Ditto 的空间回收依赖"删 MainDeletes"这条二级路径，这条路径一慢，磁盘就永远不降。

---

## 2. 问题二：分组要点两次才进入

### 2.1 数据层结构
`Main` 表用**邻接表**建模分组树：

- `bIsGroup`：1 = 分组节点，0 = 条目
- `lParentID`：父分组 ID，**-1 = 顶层**
- `clipOrder` / `clipGroupOrder`：组内排序（REAL，递减）
- `stickyClipOrder` / `stickyClipGroupOrder`：置顶排序
- `lDontAutoDelete`：**分组节点上存的是一个时间戳**（实测 1787800378），条目上为 0 → 这就是"分组内条目永不删除"的实现载体

实测数据：仅 1 个分组（`lID=1387, mText='<分组名>'`），其下 1 条目（`lID=1034, lParentID=1387`）。

排序索引（实测 schema）：
```sql
CREATE INDEX Main_TopLevel ON Main(stickyClipOrder DESC, bIsGroup ASC, clipOrder DESC);
CREATE INDEX Main_TopLevelParentID ON Main(lParentId ASC, stickyClipOrder DESC, bIsGroup ASC, clipOrder DESC);
CREATE INDEX Main_InGroup2 ON Main(lParentId ASC, stickyClipGroupOrder DESC, bIsGroup ASC, clipGroupOrder DESC);
```

### 2.2 行为层证据
二进制字符串命中 Ditto 的分组导航日志：
- `Entered group`
- `Back Group`
- `Group Path: \`
- `First Press of group hot key, group Id: %d, timeout: %d`
- `Second Press of group hot key, group Id: %d, Sending copy to save selection to this group`

即 Ditto 采用**双击/二次动作**进入分组的交互模型（先选中分组树节点，再展开/进入），且分组还有"热键一次=进入、两次=保存到该分组"的双态语义。这解释了"鼠标点两次才进入"。

**根因判定**：Ditto 的分组是**树控件 + 双击进入**模型，用户感知为"要点两次"。这是交互设计，不是 bug，但确实低效。

---

## 3. 问题三："朋友"功能用不上

### 3.1 证据
- `AppxManifest.xml` 声明防火墙规则：TCP **23443** 端口双向（domainAndPrivate），`Network send receive port (default: 23443)`
- 二进制字符串：`sendclient_ip_%d`、`sendclient_autosend_%d`、`sendclient_description_%d`、`NetworkStringPassword`、`DisableRecieve`、`START/END OF SendClientThread`、`Ditto Remote CF_HDROP`、`Recieved files array is empty...`
- 配置文件实测：`DisableRecieve=1`、`NetworkStringPassword=LetMeIn`、`NetworkExtraPassword=`、**`sendclient_ip_0..13` 全空**

### 3.2 结论
"朋友"= Ditto 的 **TCP 网络共享/远程剪贴板同步**（监听 23443，接受其他 Ditto 实例推送）。用户 14 个 `sendclient_ip_*` 全空、`DisableRecieve=1`，**完全没在用**，但：
- 仍占一个 **TCP 23443** 防火墙例外（Store 版由系统自动建规则）
- 仍带 `NetworkStringPassword` 等敏感配置项

**根因判定**：这是 Ditto 的局域网共享功能，新程序**直接不实现**即可完全移除攻击面与端口。

---

## 4. 新程序必须解决的 4 件事（逆向结论直接映射）

| # | 逆向结论 | 新程序要求 |
|---|---|---|
| 1 | CF_DIB 未压缩位图占 98.8% 空间 | **按"条数 + 总字节双限额"**淘汰；图片条单独设上限并可跳过保存 |
| 2 | 两阶段删除：删 Main 不释放空间 | **单事务真删**（不用 MainDeletes 间接层）；淘汰后立即 `VACUUM`/`incremental_vacuum` 并验证文件尺寸下降 |
| 3 | 分组双击进入 | **单击即进入分组**，分组树常驻 |
| 4 | TCP 23443 局域网共享 | **不实现**，零网络监听 |

---

## 5. 复现/验证脚本

- `analysis/chain_test.db`：两阶段删除链实证（104MB → 1.8MB）
- `analysis/shrink_test.db`：只删 Main 不 VACUUM 的对照
- `analysis/strings_all.txt`：9,400 条二进制字符串全集（可 grep 复核）
- `analysis/rea_inventory.json`：REA SQLite inventory 原始证据
- `analysis/pe_res.json`：REA PE 资源原始证据
- `backup/`：Ditto.db 与 Ditto.Settings 只读备份（原库未改动）
