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
    private ListView _entryList = null!;
    private TextBox _searchBox = null!;
    private StatusStrip _status = null!;
    private ToolStripStatusLabel _statusCount = null!;

    private long _currentGroup = Core.ClipGroup.UngroupedId; // 0 = 未分组
    private List<Core.ClipEntry> _entries = new();
    private bool _closingToTray;

    public MainForm(Core.ClipboardService service)
    {
        _service = service;
        _service.EntryAdded += _ => { if (!IsDisposed) BeginInvoke(RefreshAll); };

        Text = "ClipMate 剪贴板历史";
        Width = 860;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(680, 400);
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
            BorderStyle = BorderStyle.FixedSingle
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

        _entryList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            VirtualMode = false
        };
        _entryList.Columns.Add("内容", 520);
        _entryList.Columns.Add("时间", 140);
        _entryList.Columns.Add("大小", 90);
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
        Load += (_, _) => _split.SplitterDistance = 200;
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
            lvi.SubItems.Add(e.IsImage ? "图片 " + e.SizeText : e.SizeText);
            lvi.SubItems.Add(e.Pinned ? groupName + " ★" : groupName);
            if (e.Pinned) lvi.ForeColor = Color.DarkOrange;
            _entryList.Items.Add(lvi);
        }
        _entryList.EndUpdate();
        RefreshStats();
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

        if (_service.Config.CloseAfterPaste)
        {
            // 粘贴后自动关闭唤出窗口
            Hide();
        }
        else
        {
            RefreshEntries();   // 更新时间后刷新，让原记录上浮
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
            e.Cancel = true;
            Hide();
            return;
        }
        _hotkey.Dispose();
    }

    public void ReallyClose()
    {
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
