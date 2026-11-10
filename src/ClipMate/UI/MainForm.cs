using ClipMate.Core;

namespace ClipMate.UI;

/// <summary>
/// 主窗口：
/// 左侧 = 分组树（单击即进入，解决 Ditto 双击的痛点）
/// 右侧 = 条目列表（搜索框 + 双击粘贴）
/// 底部 = 状态栏（条数 / 占用 / 分组管理按钮）
/// </summary>
public sealed class MainForm : Form
{
    private readonly Core.ClipboardService _service;
    private HotkeyManager _hotkey;

    private SplitContainer _split = null!;
    private TreeView _groupTree = null!;
    private BufferedListView _entryList = null!;
    private TextBox _searchBox = null!;
    private StatusStrip _status = null!;
    private ToolStripStatusLabel _statusCount = null!;

    private long _currentGroup = Core.ClipGroup.UngroupedId; // 0 = 未分组
    private List<Core.ClipEntry> _entries = new();
    private bool _closingToTray;

    /// <summary>当前列表的 id -> 条目 映射，供自绘时快速取 pinned/类型等信息。</summary>
    private Dictionary<long, Core.ClipEntry> _entryMap = new();

    /// <summary>图片缩略图缓存（id -> 缩略图，null 表示该条取图失败）。</summary>
    private readonly Dictionary<long, Image?> _thumbCache = new();

    /// <summary>行内缩略图最大尺寸（配合行高 56 留出上下留白）。</summary>
    private const int ThumbMaxW = 56;
    private const int ThumbMaxH = 44;

    public MainForm(Core.ClipboardService service)
    {
        _service = service;
        _service.EntryAdded += _ => { if (!IsDisposed) BeginInvoke(RefreshAll); };

        Text = "ClipMate 剪贴板历史";
        // 窗口需容纳「左分组树 + 右列表四列」，过窄会把「大小/分组」列挤出可视区
        Width = 1080;
        Height = 600;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 450);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();

        _hotkey = new HotkeyManager(Handle, _service.Config.HotkeyShow);
        _hotkey.Register();

        RefreshAll();

        // 关闭 = 最小化到托盘
        FormClosing += OnFormClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && _service.Config.MinimizeToTray) Hide(); };
    }

    private void BuildLayout()
    {
        _searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "搜索历史…",
            Height = 28
        };
        _searchBox.TextChanged += (_, _) => RefreshEntries();

        _groupTree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle,
            // 分组是扁平列表，不需要展开按钮与连接线
            ShowLines = false,
            ShowRootLines = false,
            ShowPlusMinus = false,
            FullRowSelect = true,
            ItemHeight = 24
        };
        // 单击即进入（核心改进：不需要双击）
        _groupTree.NodeMouseClick += (_, e) =>
        {
            if (e.Node.Tag is long gid)
            {
                _currentGroup = gid;
                RefreshEntries();
            }
        };

        _entryList = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            VirtualMode = false,
            // 自绘：图片条目要在行内画缩略图（系统默认只能显示 16px 小图标）
            OwnerDraw = true,
            // 用一个 1px 宽的 image list 单纯把行高撑到 56，便于放缩略图
            SmallImageList = new ImageList { ImageSize = new Size(1, 56) }
        };
        _entryList.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _entryList.DrawItem += (_, e) => e.DrawDefault = false;   // 整行背景交给 DrawSubItem 逐格绘制
        _entryList.DrawSubItem += OnDrawSubItem;
        _entryList.Columns.Add("内容", 440);
        _entryList.Columns.Add("时间", 150);
        _entryList.Columns.Add("大小", 80);
        _entryList.Columns.Add("分组", 90);
        _entryList.DoubleClick += (_, _) => PasteSelected();
        _entryList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { PasteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
        };

        // 条目上下文菜单
        var ctx = new ContextMenuStrip();
        ctx.Items.Add("粘贴", null, (_, _) => PasteSelected());
        ctx.Items.Add("复制到剪贴板", null, (_, _) => CopySelectedToClipboard());
        ctx.Items.Add(new ToolStripSeparator());
        var pinItem = new ToolStripMenuItem("置顶/取消置顶");
        pinItem.Click += (_, _) => TogglePinSelected();
        ctx.Items.Add(pinItem);
        var moveItem = new ToolStripMenuItem("移动到分组…");
        moveItem.Click += (_, _) => MoveSelectedToGroup();
        ctx.Items.Add(moveItem);
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("删除", null, (_, _) => DeleteSelected());
        _entryList.ContextMenuStrip = ctx;

        _status = new StatusStrip();
        _statusCount = new ToolStripStatusLabel("0 条");
        _status.Items.Add(_statusCount);

        // 分组工具栏（底部）
        var groupBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
        var btnNewGroup = new Button { Text = "新建分组", AutoSize = true };
        btnNewGroup.Click += (_, _) => NewGroup();
        var btnRename = new Button { Text = "重命名", AutoSize = true };
        btnRename.Click += (_, _) => RenameGroup();
        var btnDeleteGroup = new Button { Text = "删除分组", AutoSize = true };
        btnDeleteGroup.Click += (_, _) => DeleteCurrentGroup();
        groupBar.Controls.AddRange(new Control[] { btnNewGroup, btnRename, btnDeleteGroup });

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_entryList);
        rightPanel.Controls.Add(_searchBox);

        var leftPanel = new Panel { Dock = DockStyle.Fill };
        leftPanel.Controls.Add(_groupTree);

        _split = new SplitContainer
        {
            Size = new Size(800, 400),   // 先给足初始尺寸，避免 Panel2MinSize 赋值时越界
            Dock = DockStyle.Fill,
            Panel1MinSize = 120,
            Panel2MinSize = 320
        };
        // SplitterDistance 在布局完成后设置，防止与 min-size 冲突
        Load += (_, _) => _split.SplitterDistance = 180;
        _split.Panel1.Controls.Add(leftPanel);
        _split.Panel2.Controls.Add(rightPanel);

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, AutoSize = true };
        bottomPanel.Controls.Add(groupBar);

        Controls.Add(_split);
        Controls.Add(bottomPanel);
        Controls.Add(_status);
    }

    // ---------- 刷新 ----------

    private void RefreshAll()
    {
        RefreshGroups();
        RefreshEntries();
        RefreshStats();
    }

    private void RefreshGroups()
    {
        _groupTree.BeginUpdate();
        _groupTree.Nodes.Clear();
        var selectedId = _currentGroup;

        foreach (var g in _service.Storage.GetGroups())
        {
            var node = new TreeNode(Core.ClipGroup.FormatName(g.Name)) { Tag = g.Id };
            if (g.Id == selectedId) _groupTree.SelectedNode = node;
            _groupTree.Nodes.Add(node);
        }
        _groupTree.EndUpdate();
    }

    private void RefreshEntries()
    {
        var filter = _searchBox.Text;
        _entries = _service.Storage.GetEntries(_currentGroup, filter).ToList();
        var groups = _service.Storage.GetGroups().ToDictionary(g => g.Id, g => g.Name);

        // 列表已换，旧缩略图缓存与映射一并作废，避免内存堆积和串行
        _entryMap = _entries.ToDictionary(e => e.Id);
        PurgeThumbCache();

        _entryList.BeginUpdate();
        _entryList.Items.Clear();
        foreach (var e in _entries)
        {
            var groupName = e.GroupId == Core.ClipGroup.UngroupedId
                ? "未分组"
                : (groups.TryGetValue(e.GroupId, out var n) ? n : "未分组");
            var lvi = new ListViewItem(e.Preview)
            {
                Tag = e.Id
            };
            lvi.SubItems.Add(e.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            // 图片条目在「内容」列已有缩略图，大小列不必再重复「图片」二字
            lvi.SubItems.Add(e.SizeText);
            lvi.SubItems.Add(e.Pinned ? groupName + " ★" : groupName);
            if (e.Pinned) lvi.ForeColor = Color.DarkOrange;
            _entryList.Items.Add(lvi);
        }
        _entryList.EndUpdate();
        RefreshStats();
    }

    /// <summary>只保留当前列表里还存在的 id 的缩略图，其余释放。</summary>
    private void PurgeThumbCache()
    {
        if (_thumbCache.Count == 0) return;
        var alive = _entryMap.Keys.ToHashSet();
        var dead = _thumbCache.Keys.Where(id => !alive.Contains(id)).ToList();
        foreach (var id in dead)
        {
            if (_thumbCache[id] is { } img) img.Dispose();
            _thumbCache.Remove(id);
        }
    }

    /// <summary>取（并按需生成）某条图片的缩略图；失败返回 null，调用方退化为只显示文字。</summary>
    private Image? GetThumbnail(Core.ClipEntry entry)
    {
        if (_thumbCache.TryGetValue(entry.Id, out var cached)) return cached;

        Image? thumb = null;
        try
        {
            var data = _service.Storage.GetImageData(entry.Id);
            if (data is not null)
            {
                using var ms = new MemoryStream(data);
                using var src = Image.FromStream(ms);

                // 等比缩放到不超过 ThumbMaxW x ThumbMaxH；小图不放大
                var scale = Math.Min(
                    Math.Min((double)ThumbMaxW / src.Width, (double)ThumbMaxH / src.Height), 1.0);
                var w = Math.Max(1, (int)Math.Round(src.Width * scale));
                var h = Math.Max(1, (int)Math.Round(src.Height * scale));

                var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.DrawImage(src, 0, 0, w, h);
                }
                thumb = bmp;
            }
        }
        catch
        {
            thumb = null;   // 图片损坏/格式异常：不阻断列表绘制
        }

        _thumbCache[entry.Id] = thumb;
        return thumb;
    }

    /// <summary>自绘单元格：第一列遇图片条目时，左侧画缩略图、右侧写「宽×高」。</summary>
    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is not long id) return;
        _entryMap.TryGetValue(id, out var entry);

        var selected = e.Item.Selected;
        var back = selected ? SystemColors.Highlight : _entryList.BackColor;
        using (var brush = new SolidBrush(back))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        var fore = selected
            ? SystemColors.HighlightText
            : (entry?.Pinned == true ? Color.DarkOrange : _entryList.ForeColor);

        if (e.ColumnIndex == 0 && entry?.IsImage == true)
        {
            const int pad = 4;
            var left = e.Bounds.Left + pad;
            var thumb = GetThumbnail(entry);

            if (thumb is not null)
            {
                var top = e.Bounds.Top + (e.Bounds.Height - thumb.Height) / 2;
                e.Graphics.DrawImage(thumb, left, top, thumb.Width, thumb.Height);

                // 细边框，让白色截图在浅色背景上也有轮廓
                using var border = new Pen(Color.FromArgb(70, 0, 0, 0));
                e.Graphics.DrawRectangle(border, left, top, thumb.Width - 1, thumb.Height - 1);

                left += thumb.Width + 8;
            }

            var textRect = new Rectangle(
                left, e.Bounds.Top,
                Math.Max(0, e.Bounds.Right - left - 2), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, entry.Preview, _entryList.Font, textRect, fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        else
        {
            var rect = new Rectangle(
                e.Bounds.Left + 3, e.Bounds.Top,
                Math.Max(0, e.Bounds.Width - 6), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, _entryList.Font, rect, fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private void RefreshStats()
    {
        var (count, bytes) = _service.Storage.GetStats();
        _statusCount.Text = $"{count} 条 · {FormatBytes(bytes)}";
    }

    private static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{b / 1024.0 / 1024.0:0.##} MB",
        _ => $"{b / 1024.0 / 1024.0 / 1024.0:0.##} GB"
    };

    // ---------- 操作 ----------

    private Core.ClipEntry? SelectedEntry
    {
        get
        {
            if (_entryList.SelectedItems.Count == 0) return null;
            var id = (long)_entryList.SelectedItems[0].Tag!;
            return _entries.FirstOrDefault(e => e.Id == id);
        }
    }

    private void PasteSelected()
    {
        var e = SelectedEntry;
        if (e is null) return;

        _service.Paste(e);   // 写剪贴板 + 更新时间/删除，不新增重复记录

        // 无条件刷新：Paste 已把该条 created_at 更新为当前时间，
        // 列表须重建才能让它上浮到顶部。若先 Hide 再刷新，下次唤出仍是旧顺序。
        RefreshEntries();

        if (_service.Config.CloseAfterPaste)
        {
            // 粘贴后自动关闭唤出窗口
            Hide();
        }
    }

    private void CopySelectedToClipboard() => PasteSelected();

    private void DeleteSelected()
    {
        var e = SelectedEntry;
        if (e is null) return;
        _service.Storage.Delete(e.Id);
        RefreshAll();
        _service.MaybeCleanup();
    }

    private void TogglePinSelected()
    {
        var e = SelectedEntry;
        if (e is null) return;
        _service.Storage.SetPinned(e.Id, !e.Pinned);
        RefreshEntries();
    }

    private void MoveSelectedToGroup()
    {
        var e = SelectedEntry;
        if (e is null) return;

        var groups = _service.Storage.GetGroups().Where(g => g.Id != Core.ClipGroup.UngroupedId).ToList();
        if (groups.Count == 0) { MessageBox.Show("还没有分组，请先新建分组。", "提示"); return; }

        using var dlg = new Form { Text = "移动到分组", Width = 320, Height = 160, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var combo = new ComboBox { Left = 20, Top = 20, Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(groups.Select(g => g.Name).ToArray());
        combo.SelectedIndex = 0;
        var ok = new Button { Text = "确定", Left = 120, Top = 60, Width = 80, DialogResult = DialogResult.OK };
        dlg.Controls.Add(combo); dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;
        if (dlg.ShowDialog(this) == DialogResult.OK && combo.SelectedIndex >= 0)
        {
            _service.Storage.MoveEntry(e.Id, groups[combo.SelectedIndex].Id);
            RefreshAll();
        }
    }

    private void NewGroup()
    {
        var name = Prompt("新建分组", "分组名称：");
        if (string.IsNullOrWhiteSpace(name)) return;
        _service.Storage.AddGroup(name);
        RefreshGroups();
    }

    private void RenameGroup()
    {
        var node = _groupTree.SelectedNode;
        if (node?.Tag is not long gid || gid == Core.ClipGroup.UngroupedId) return;
        var name = Prompt("重命名分组", "新名称：", node.Text);
        if (string.IsNullOrWhiteSpace(name)) return;
        _service.Storage.RenameGroup(gid, name);
        RefreshGroups();
    }

    private void DeleteCurrentGroup()
    {
        var node = _groupTree.SelectedNode;
        if (node?.Tag is not long gid || gid == Core.ClipGroup.UngroupedId) return;
        if (MessageBox.Show($"删除分组“{node.Text}”？组内条目将移回“未分组”。", "确认",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _service.Storage.DeleteGroup(gid);
        _currentGroup = Core.ClipGroup.UngroupedId;
        RefreshAll();
    }

    private string? Prompt(string title, string label, string? defaultValue = null)
    {
        using var dlg = new Form { Text = title, Width = 360, Height = 160, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var lbl = new Label { Text = label, Left = 20, Top = 20, AutoSize = true };
        var txt = new TextBox { Left = 20, Top = 48, Width = 300, Text = defaultValue ?? "" };
        var ok = new Button { Text = "确定", Left = 140, Top = 84, Width = 80, DialogResult = DialogResult.OK };
        dlg.Controls.AddRange(new Control[] { lbl, txt, ok });
        dlg.AcceptButton = ok;
        return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text : null;
    }

    // ---------- 托盘 / 生命周期 ----------

    private NotifyIcon? _tray;
    private ToolStripMenuItem? _traySettings;

    public void AttachTray(NotifyIcon tray, ToolStripMenuItem settingsItem)
    {
        _tray = tray;
        _traySettings = settingsItem;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_service.Config.MinimizeToTray && !_closingToTray && e.CloseReason == CloseReason.UserClosing)
        {
            // 关闭按钮 = 最小化到托盘，不算退出
            Core.ClipDiag.Log("ui: 点了关闭按钮 -> 最小化到托盘（进程继续运行）");
            e.Cancel = true;
            Hide();
            return;
        }

        // 真正要退出时记录原因，便于日后排查"程序怎么没了"
        Core.ClipDiag.Log($"ui: FormClosing 真正退出 reason={e.CloseReason}");

        _hotkey.Dispose();

        foreach (var img in _thumbCache.Values)
        {
            img?.Dispose();
        }
        _thumbCache.Clear();
    }

    public void ReallyClose()
    {
        Core.ClipDiag.Log("ui: 用户从托盘菜单选择「退出」");
        _closingToTray = true;
        Close();
    }

    /// <summary>供托盘“设置”菜单调用。保存后立即用新热键重新注册。</summary>
    public void ShowSettings()
    {
        using var dlg = new SettingsForm(_service);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _hotkey.Dispose();
            _hotkey = new HotkeyManager(Handle, _service.Config.HotkeyShow);
            _hotkey.Register();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (HotkeyManager.IsHotkeyMessage(m.Msg))
        {
            if (Visible) Hide();
            else { Show(); WindowState = FormWindowState.Normal; Activate(); }
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// 开启双缓冲的 ListView。
/// 自绘（OwnerDraw）绘制缩略图时，默认会逐格刷新导致明显闪烁；
/// ListView 的 DoubleBuffered 是 protected，只能通过子类打开。
/// </summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
    }
}
