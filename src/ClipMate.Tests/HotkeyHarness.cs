using ClipMate.Core;
using System.Windows.Forms;

// 验证热键解析：FormatKey 与 Parse 必须互逆，覆盖反引号等符号键
class HotkeyHarness
{
    static int Main()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
            if (!ok) failures++;
        }

        Console.WriteLine("[1] 字符串解析（用户手输/历史配置）");
        var cases = new (string text, uint expectVk)[]
        {
            ("Ctrl+`", 0xC0),       // 反引号 VK_OEM_3 —— 本次 bug 的核心
            ("Ctrl+Shift+V", 0x56),
            ("Ctrl+`", 0xC0),
            ("Alt+-", 0xBD),
            ("Ctrl+=", 0xBB),
            ("Ctrl+[", 0xDB),
            ("Ctrl+]", 0xDD),
            ("Ctrl+\\", 0xDC),
            ("Ctrl+;", 0xBA),
            ("Ctrl+'", 0xDE),
            ("Ctrl+,", 0xBC),
            ("Ctrl+.", 0xBE),
            ("Ctrl+/", 0xBF),
            ("Ctrl+F5", 0x74),
            ("Ctrl+Space", 0x20),
            ("Ctrl+A", 0x41),
            ("Ctrl+5", 0x35),
        };
        foreach (var (text, expectVk) in cases)
        {
            var (mod, key) = HotkeyManager.Parse(text);
            Check($"Parse(\"{text}\") -> vk=0x{key:X2} (期望 0x{expectVk:X2})",
                key == expectVk && mod != 0);
        }

        Console.WriteLine("[2] FormatKey ↔ Parse 往返（捕获框实际路径）");
        var keyCases = new (Keys mod, Keys key)[]
        {
            (Keys.Control, Keys.Oem3),         // Ctrl+`
            (Keys.Control | Keys.Shift, Keys.V),
            (Keys.Alt, Keys.OemMinus),
            (Keys.Control, Keys.OemOpenBrackets),
            (Keys.Control | Keys.Alt, Keys.Delete),
        };
        foreach (var (m, k) in keyCases)
        {
            var formatted = HotkeyManager.FormatKey(m, k);
            var (mod2, key2) = HotkeyManager.Parse(formatted);
            Check($"往返 \"{formatted}\" -> vk=0x{key2:X2} (期望 0x{(uint)k:X2})",
                key2 == (uint)k);
        }

        Console.WriteLine("[3] 空字符串 = 禁用热键");
        var (modE, keyE) = HotkeyManager.Parse("");
        Check("空字符串 key=0（不注册）", keyE == 0);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过 ✅" : $"{failures} 项失败 ❌");
        return failures;
    }
}
