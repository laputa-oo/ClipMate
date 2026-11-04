using Microsoft.Win32;

namespace ClipMate.Core;

/// <summary>
/// 单实例 + 开机自启。
/// </summary>
public static class AppLifecycle
{
    private const string MutexName = "ClipMate.SingleInstance.8D2E4A1C";
    private static Mutex? _mutex;

    public static bool TryAcquireSingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        return createdNew;
    }

    public static void ReleaseSingleInstance()
    {
        try { _mutex?.ReleaseMutex(); } catch { /* 已释放 */ }
        _mutex?.Dispose();
        _mutex = null;
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("ClipMate") is string;
        }
        catch { return false; }
    }

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;

            if (enabled)
                key.SetValue("ClipMate", Application.ExecutablePath);
            else
                key.DeleteValue("ClipMate", throwOnMissingValue: false);
        }
        catch { /* 权限不足时静默 */ }
    }
}
