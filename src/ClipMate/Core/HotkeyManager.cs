using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClipMate.Core;

/// <summary>
/// 全局热键：RegisterHotKey，无需焦点即可唤起主窗口。
/// 默认 Ctrl+Shift+V。
///
/// 提供两个互逆的方法：
///   FormatKey(Keys modifiers, Keys key) -> "Ctrl+`"
///   Parse("Ctrl+`")                     -> (mod, vk)
/// 保证捕获框显示的字符串能无损还原成虚拟键码。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private readonly IntPtr _hwnd;
    private readonly int _id;
    private uint _modifiers;
    private uint _key;

    private bool _registered;

    public HotkeyManager(IntPtr hwnd, string hotkey, int id = 0xBEEF)
    {
        _hwnd = hwnd;
        _id = id;
        (_modifiers, _key) = Parse(hotkey);
    }

    public void Register()
    {
        if (_key == 0) return;
        Unregister();
        _registered = RegisterHotKey(_hwnd, _id, _modifiers, _key);
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, _id);
            _registered = false;
        }
    }

    public static bool IsHotkeyMessage(int msg) => msg == WM_HOTKEY;

    public void Dispose() => Unregister();

    // ---------------- 修饰键位掩码（Win32 MOD_*） ----------------
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>把 "Ctrl+Shift+V" 解析成 (修饰键掩码, 虚拟键码)。</summary>
    public static (uint mod, uint key) Parse(string hotkey)
    {
        uint mod = 0;
        uint key = 0;
        foreach (var part in (hotkey ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToUpperInvariant())
            {
                case "CTRL": mod |= MOD_CONTROL; break;
                case "ALT": mod |= MOD_ALT; break;
                case "SHIFT": mod |= MOD_SHIFT; break;
                case "WIN": mod |= MOD_WIN; break;
                default:
                    key = StringToVk(part.Trim());
                    break;
            }
        }
        return (mod, key);
    }

    /// <summary>
    /// 把捕获框拿到的 Keys 转成显示字符串，例如 Ctrl+`、Ctrl+Shift+V。
    /// 与 <see cref="Parse"/> 互逆，确保设置里显示什么，启动就能还原什么。
    /// </summary>
    public static string FormatKey(Keys modifiers, Keys key)
    {
        var parts = new List<string>();
        if ((modifiers & Keys.Control) == Keys.Control) parts.Add("Ctrl");
        if ((modifiers & Keys.Alt) == Keys.Alt) parts.Add("Alt");
        if ((modifiers & Keys.Shift) == Keys.Shift) parts.Add("Shift");
        if ((modifiers & Keys.LWin) == Keys.LWin || (modifiers & Keys.RWin) == Keys.RWin) parts.Add("Win");
        parts.Add(KeyToString(key));
        return string.Join("+", parts);
    }

    /// <summary>单个虚拟键 -> 显示名。覆盖字母、数字、F1-F12、OEM 符号键与常用功能键。</summary>
    private static string KeyToString(Keys key)
    {
        if (key >= Keys.A && key <= Keys.Z) return key.ToString();
        if (key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + (key - Keys.D0))).ToString();
        if (key >= Keys.F1 && key <= Keys.F12) return "F" + (key - Keys.F1 + 1);
        return key switch
        {
            Keys.Oem3 => "`",                 // 反引号 / 波浪号，VK_OEM_3 = 0xC0
            Keys.OemMinus => "-",
            Keys.Oemplus => "=",
            Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]",
            Keys.OemPipe => "\\",
            Keys.OemSemicolon => ";",
            Keys.OemQuotes => "'",
            Keys.Oemcomma => ",",
            Keys.OemPeriod => ".",
            Keys.OemQuestion => "/",
            Keys.Space => "Space",
            Keys.Tab => "Tab",
            Keys.Enter => "Enter",
            Keys.Back => "Backspace",
            Keys.Delete => "Delete",
            Keys.Insert => "Insert",
            Keys.Home => "Home",
            Keys.End => "End",
            Keys.PageUp => "PageUp",
            Keys.PageDown => "PageDown",
            Keys.Up => "Up",
            Keys.Down => "Down",
            Keys.Left => "Left",
            Keys.Right => "Right",
            _ => key.ToString()
        };
    }

    /// <summary>显示名 -> 虚拟键码（KeyToString 的逆）。</summary>
    private static uint StringToVk(string s)
    {
        if (s.Length == 1)
        {
            var c = s[0];
            if (c >= 'A' && c <= 'Z') return (uint)c;
            if (c >= 'a' && c <= 'z') return (uint)(c - 'a' + 'A');
            if (c >= '0' && c <= '9') return (uint)c;
            return c switch
            {
                '`' => 0xC0,   // VK_OEM_3
                '~' => 0xC0,
                '-' => 0xBD,   // VK_OEM_MINUS
                '=' => 0xBB,   // VK_OEM_PLUS
                '[' => 0xDB,   // VK_OEM_4
                ']' => 0xDD,   // VK_OEM_6
                '\\' => 0xDC,  // VK_OEM_5
                ';' => 0xBA,   // VK_OEM_1
                '\'' => 0xDE,  // VK_OEM_7
                ',' => 0xBC,   // VK_OEM_COMMA
                '.' => 0xBE,   // VK_OEM_PERIOD
                '/' => 0xBF,   // VK_OEM_2
                ' ' => 0x20,
                _ => 0
            };
        }
        return s.ToUpperInvariant() switch
        {
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            "BACKSPACE" => 0x08,
            "DELETE" => 0x2E,
            "INSERT" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            _ => 0
        };
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
