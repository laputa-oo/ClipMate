namespace ClipMate.UI;

/// <summary>
/// 设置窗口：条数 / 总字节 / 保留天数 / 图片策略 / 热键 / 自启。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly Core.ClipboardService _service;

    private NumericUpDown _maxEntries = null!;
    private NumericUpDown _maxSizeMB = null!;
    private NumericUpDown _retainDays = null!;
    private CheckBox _saveImages = null!;
    private NumericUpDown _maxImageMB = null!;
    private NumericUpDown _maxSingleKB = null!;
    private TextBox _hotkey = null!;
    private CheckBox _autoStart = null!;
    private CheckBox _minimizeToTray = null!;
    private CheckBox _removeAfterPaste = null!;
    private CheckBox _deduplicate = null!;
    private CheckBox _closeAfterPaste = null!;

    public SettingsForm(Core.ClipboardService service)
    {
        _service = service;
        Text = "ClipMate 设置";
        Width = 480;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        LoadValues();
    }

    private void BuildLayout()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 12,
            AutoSize = true
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _maxEntries = MakeNumber(0, 100000, 500);
        _maxSizeMB = MakeNumber(0, 10240, 64);
        _retainDays = MakeNumber(0, 3650, 30);
        _saveImages = new CheckBox { Text = "保存图片（默认关闭，图片是磁盘暴涨的元凶）", AutoSize = true };
        _maxImageMB = MakeNumber(0, 10240, 16);
        _maxSingleKB = MakeNumber(1, 102400, 512);
        _hotkey = new TextBox
        {
            Width = 140,
            ReadOnly = true,
            // 提示用户：点击后直接按组合键
            PlaceholderText = "点击后按组合键，Esc 清空"
        };
        _hotkey.KeyDown += OnHotkeyKeyDown;
        _autoStart = new CheckBox { Text = "开机自启动", AutoSize = true };
        _minimizeToTray = new CheckBox { Text = "关闭窗口时最小化到托盘", AutoSize = true };
        _removeAfterPaste = new CheckBox { Text = "粘贴后移除该条", AutoSize = true };
        _deduplicate = new CheckBox { Text = "去重（内容相同不重复保存）", AutoSize = true };
        _closeAfterPaste = new CheckBox { Text = "双击粘贴后自动关闭窗口", AutoSize = true };

        _saveImages.CheckedChanged += (_, _) => _maxImageMB.Enabled = _saveImages.Checked;

        int r = 0;
        void Add(string label, Control c)
        {
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            panel.Controls.Add(c, 1, r);
            r++;
        }

        Add("历史条数上限（0=不限）", _maxEntries);
        Add("历史总大小上限 MB（0=不限）", _maxSizeMB);
        Add("保留天数（0=不限）", _retainDays);
        Add("单条最大 KB", _maxSingleKB);
        panel.Controls.Add(_saveImages, 1, r); panel.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r); r++;
        Add("图片总大小上限 MB", _maxImageMB);
        Add("显示热键", _hotkey);
        panel.Controls.Add(_autoStart, 1, r); panel.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r); r++;
        panel.Controls.Add(_minimizeToTray, 1, r); panel.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r); r++;
        panel.Controls.Add(_removeAfterPaste, 1, r); panel.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r); r++;
        panel.Controls.Add(_closeAfterPaste, 1, r); panel.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r); r++;
        panel.Controls.Add(_deduplicate, 1, r); panel.Controls.Add(new Label { Text = "", AutoSize = true }, 0, r); r++;

        var ok = new Button { Text = "保存", Width = 100, Height = 32, Anchor = AnchorStyles.Right, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Width = 100, Height = 32, Anchor = AnchorStyles.Right, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12), AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(panel);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static NumericUpDown MakeNumber(decimal min, decimal max, decimal val) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = val,
        Width = 140,
        ThousandsSeparator = true
    };

    /// <summary>
    /// 热键捕获：点击输入框后直接按组合键（如 Ctrl+`），自动显示为 "Ctrl+`"；
    /// 按 Esc 清空（= 禁用热键）。
    /// </summary>
    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        // Esc 清空热键
        if (e.KeyCode == Keys.Escape)
        {
            _hotkey.Text = "";
            return;
        }

        // 忽略纯修饰键的单独按下（还没按到主键）
        if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey
            or Keys.LWin or Keys.RWin)
        {
            return;
        }

        // 用修饰键 + 主键生成显示字符串（与 HotkeyManager.Parse 互逆）
        _hotkey.Text = Core.HotkeyManager.FormatKey(e.Modifiers, e.KeyCode);
    }

    private void LoadValues()
    {
        var c = _service.Config;
        _maxEntries.Value = c.MaxEntries;
        _maxSizeMB.Value = c.MaxTotalSizeMB;
        _retainDays.Value = c.RetainDays;
        _maxSingleKB.Value = c.MaxSingleEntryKB;
        _saveImages.Checked = c.SaveImages;
        _maxImageMB.Value = c.MaxImageSizeMB;
        _maxImageMB.Enabled = c.SaveImages;
        _hotkey.Text = c.HotkeyShow;
        _autoStart.Checked = c.AutoStart;
        _minimizeToTray.Checked = c.MinimizeToTray;
        _removeAfterPaste.Checked = c.RemoveAfterPaste;
        _closeAfterPaste.Checked = c.CloseAfterPaste;
        _deduplicate.Checked = c.Deduplicate;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK) { base.OnFormClosing(e); return; }

        var c = _service.Config;
        c.MaxEntries = (int)_maxEntries.Value;
        c.MaxTotalSizeMB = (int)_maxSizeMB.Value;
        c.RetainDays = (int)_retainDays.Value;
        c.MaxSingleEntryKB = (int)_maxSingleKB.Value;
        c.SaveImages = _saveImages.Checked;
        c.MaxImageSizeMB = (int)_maxImageMB.Value;
        c.HotkeyShow = _hotkey.Text.Trim();
        c.AutoStart = _autoStart.Checked;
        c.MinimizeToTray = _minimizeToTray.Checked;
        c.RemoveAfterPaste = _removeAfterPaste.Checked;
        c.CloseAfterPaste = _closeAfterPaste.Checked;
        c.Deduplicate = _deduplicate.Checked;
        c.Save();

        Core.AppLifecycle.SetAutoStart(c.AutoStart);

        base.OnFormClosing(e);
    }
}
