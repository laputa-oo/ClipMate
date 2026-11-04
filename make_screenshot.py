#!/usr/bin/env python3
"""截取 ClipMate 主窗口，保存到 docs/screenshot.png。
纯标准库：ctypes 调 Win32 GDI 抓取，自写 PNG 编码。
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct
import subprocess
import sys
import time
import zlib

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
user32.SetProcessDPIAware()

WINDOW_TITLE = "ClipMate 剪贴板历史"
SRCCOPY = 0x00CC0020


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
        ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


def find_window(title):
    for _ in range(40):
        hwnd = user32.FindWindowW(None, title)
        if hwnd:
            return hwnd
        time.sleep(0.5)
    return 0


class POINT(ctypes.Structure):
    _fields_ = [("x", wt.LONG), ("y", wt.LONG)]


PW_RENDERFULLCONTENT = 0x00000002


def capture(hwnd):
    """用 PrintWindow 抓取窗口自身内容（不依赖前台/不被遮挡），再裁出客户区。
    SetForegroundWindow 在后台进程中会被 Windows 拒绝，故不能用屏幕 BitBlt。
    """
    wrect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(wrect))
    ww = wrect.right - wrect.left
    wh = wrect.bottom - wrect.top

    crect = wt.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(crect))
    cw = crect.right - crect.left
    ch = crect.bottom - crect.top
    if ww <= 0 or wh <= 0 or cw <= 0 or ch <= 0:
        raise RuntimeError(f"窗口尺寸异常: window={ww}x{wh} client={cw}x{ch}")

    # 客户区相对窗口左上角的偏移
    origin = POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(origin))
    off_x = origin.x - wrect.left
    off_y = origin.y - wrect.top

    hdc_screen = user32.GetDC(0)
    mdc = gdi32.CreateCompatibleDC(hdc_screen)
    bmp = gdi32.CreateCompatibleBitmap(hdc_screen, ww, wh)
    old = gdi32.SelectObject(mdc, bmp)

    ok = user32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT)
    if not ok:
        user32.PrintWindow(hwnd, mdc, 0)

    # 取整幅窗口像素，再裁剪客户区
    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = ww
    bi.biHeight = -wh
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0

    full = ctypes.create_string_buffer(ww * wh * 4)
    gdi32.GetDIBits(mdc, bmp, 0, wh, full, ctypes.byref(bi), 0)
    src = full.raw

    # 裁出客户区
    out = bytearray(cw * ch * 4)
    for y in range(ch):
        sy = y + off_y
        if sy < 0 or sy >= wh:
            continue
        s = (sy * ww + off_x) * 4
        d = y * cw * 4
        out[d:d + cw * 4] = src[s:s + cw * 4]

    gdi32.SelectObject(mdc, old)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mdc)
    user32.ReleaseDC(0, hdc_screen)
    return cw, ch, bytes(out)


def bgra_to_rgba(w, h, raw):
    out = bytearray(w * h * 3 + h)   # RGB + 每行 filter 字节
    pos = 0
    for y in range(h):
        out[pos] = 0
        pos += 1
        row = raw[y * w * 4:(y + 1) * w * 4]
        for x in range(w):
            b, g, r = row[x * 4], row[x * 4 + 1], row[x * 4 + 2]
            out[pos] = r
            out[pos + 1] = g
            out[pos + 2] = b
            pos += 3
    return bytes(out)


def write_png(path, w, h, rgb_scanlines):
    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data
                + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff))
    ihdr = struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)  # 8-bit RGB
    png = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr)
           + chunk(b'IDAT', zlib.compress(rgb_scanlines, 9)) + chunk(b'IEND', b''))
    with open(path, 'wb') as f:
        f.write(png)


def is_meaningful(w, h, raw):
    """判断截图是否已渲染出真实界面：统计非纯白/纯黑像素占比。"""
    total = w * h
    if total == 0:
        return False
    interesting = 0
    step = max(1, total // 20000)   # 抽样，避免全量遍历太慢
    idx = 0
    for i in range(0, total, step):
        b = raw[i * 4]
        g = raw[i * 4 + 1]
        r = raw[i * 4 + 2]
        # 非白(>245) 且 非黑(<10) 视为有内容
        if not (r > 245 and g > 245 and b > 245) and not (r < 10 and g < 10 and b < 10):
            interesting += 1
    sampled = len(range(0, total, step))
    return interesting > sampled * 0.02   # 超过 2% 有内容像素


def main():
    exe = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                       'publish', 'portable', 'ClipMate.exe')
    if not os.path.exists(exe):
        print(f"未找到 {exe}")
        return 1

    proc = subprocess.Popen([exe])
    try:
        hwnd = find_window(WINDOW_TITLE)
        if not hwnd:
            print("未找到窗口（可能已最小化到托盘）")
            return 1
        user32.ShowWindow(hwnd, 5)          # SW_SHOW
        user32.SetForegroundWindow(hwnd)

        result = None
        for attempt in range(1, 8):
            time.sleep(1.5)
            user32.InvalidateRect(hwnd, None, True)
            user32.UpdateWindow(hwnd)
            time.sleep(0.4)
            w, h, raw = capture(hwnd)
            ok = is_meaningful(w, h, raw)
            print(f"  第 {attempt} 次捕获 {w}x{h} -> {'有效' if ok else '空白，重试'}")
            if ok:
                result = (w, h, raw)
                break

        if result is None:
            print("多次捕获均未得到有效画面")
            return 1

        w, h, raw = result
        rgb = bgra_to_rgba(w, h, raw)
        out = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           'docs', 'screenshot.png')
        write_png(out, w, h, rgb)
        print(f"已保存 {out} ({os.path.getsize(out)} bytes)")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except Exception:
            proc.kill()
    return 0


if __name__ == '__main__':
    sys.exit(main())
