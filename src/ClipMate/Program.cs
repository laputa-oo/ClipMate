namespace ClipMate;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 剪贴板 API 与 WinForms 都需要 STA
        ApplicationConfiguration.Initialize();

        // 单实例：重复启动时唤起已有实例
        if (!Core.AppLifecycle.TryAcquireSingleInstance())
        {
            MessageBox.Show("ClipMate 已在运行，请查看系统托盘图标。", "ClipMate",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("发生未处理的异常：" + e.Exception.Message, "ClipMate",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        var service = new Core.ClipboardService();

        var mainForm = new UI.MainForm(service);

        // 托盘图标
        var tray = new NotifyIcon
        {
            Text = "ClipMate 剪贴板历史",
            Visible = true
        };
        tray.Icon = LoadIcon();

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => { mainForm.Show(); mainForm.WindowState = FormWindowState.Normal; mainForm.Activate(); };
        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => mainForm.ShowSettings();
        var cleanupItem = new ToolStripMenuItem("立即清理");
        cleanupItem.Click += (_, _) =>
        {
            var r = service.TryCleanup();
            MessageBox.Show($"清理完成：删除 {r.deleted} 条。\n数据库 {r.before:N0} B → {r.after:N0} B",
                "清理结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => mainForm.ReallyClose();
        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(cleanupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { mainForm.Show(); mainForm.WindowState = FormWindowState.Normal; mainForm.Activate(); };

        mainForm.AttachTray(tray, settingsItem);

        // 启动监听
        service.StartWatching();

        Application.Run(mainForm);

        tray.Visible = false;
        tray.Dispose();
        service.Dispose();
        Core.AppLifecycle.ReleaseSingleInstance();
    }

    private static Icon LoadIcon()
    {
        try
        {
            // 从 exe 本身提取图标（ApplicationIcon 已嵌入），免额外资源文件
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                ?? SystemIcons.Application;
        }
        catch { /* 回退到系统图标 */ }
        return SystemIcons.Application;
    }
}
