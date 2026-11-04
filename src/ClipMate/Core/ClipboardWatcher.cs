using System.Runtime.InteropServices;

namespace ClipMate.Core;

/// <summary>
/// 剪贴板监听：用 Win32 AddClipboardFormatListener + 消息窗口，
/// 系统剪贴板一变就回调。无需轮询、不占 CPU，与 Ditto 同款机制。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private readonly ClipboardMessageWindow _window;

    public ClipboardWatcher(Action onClipboardChanged)
    {
        _window = new ClipboardMessageWindow(onClipboardChanged);
    }

    public void Dispose() => _window.Dispose();

    private sealed class ClipboardMessageWindow : NativeWindow, IDisposable
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private readonly Action _callback;

        public ClipboardMessageWindow(Action callback)
        {
            _callback = callback;
            // 只作消息接收，不显示窗口
            CreateHandle(new CreateParams { Style = 0, ExStyle = 0 });
            AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                try { _callback(); } catch { /* 回调异常不崩监听线程 */ }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    }
}
