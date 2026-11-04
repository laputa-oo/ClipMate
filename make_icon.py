#!/usr/bin/env python3
"""生成 ClipMate 图标：蓝色对角渐变圆角方块 + 白色剪贴板（含顶部夹子、内阴影）。
纯标准库，超采样抗锯齿。输出 assets/ClipMate.ico 与 ClipMate.png。
"""
import struct, zlib, math, os

def lerp(a, b, t):
    return a + (b - a) * t

def rrect_sdf(px, py, cx, cy, hw, hh, r):
    """圆角矩形有符号距离场。<0 在内部。中心(cx,cy)，半宽高 hw,hh，圆角半径 r。"""
    qx = abs(px - cx) - (hw - r)
    qy = abs(py - cy) - (hh - r)
    ax = max(qx, 0.0)
    ay = max(qy, 0.0)
    outside = math.hypot(ax, ay)
    inside = min(max(qx, qy), 0.0)
    return outside + inside - r

def clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v

def render(size):
    """返回 size*size 的 RGBA bytes。用 SDF 距离做 1px 抗锯齿，无需超采样。"""
    top = (80, 150, 235)    # #5096EB
    bot = (34, 78, 168)     # #224EA8
    corner = size * 0.205

    # 剪贴板主体
    clip_cx = size * 0.50
    clip_cy = size * 0.52
    clip_hw = size * 0.245
    clip_hh = size * 0.315
    clip_r = size * 0.075

    # 顶部夹子（tab）
    tab_cx = size * 0.50
    tab_cy = size * 0.155
    tab_hw = size * 0.115
    tab_hh = size * 0.075
    tab_r = size * 0.035

    # 阴影
    sh_cx = clip_cx + size * 0.018
    sh_cy = clip_cy + size * 0.030

    raw = bytearray()
    for y in range(size):
        fy = y + 0.5
        row = bytearray()
        for x in range(size):
            fx = x + 0.5

            # 背景圆角外 → 透明
            bg_sdf = rrect_sdf(fx, fy, size * 0.5, size * 0.5, size * 0.5, size * 0.5, corner)
            bg_alpha = clamp(0.5 - bg_sdf, 0.0, 1.0)
            if bg_alpha <= 0.0:
                row += b'\x00\x00\x00\x00'
                continue

            # 背景渐变（对角）
            t = clamp((fx + fy) / (2 * size), 0.0, 1.0)
            r = lerp(top[0], bot[0], t)
            g = lerp(top[1], bot[1], t)
            b = lerp(top[2], bot[2], t)

            # 先判定剪贴板主体与夹子（最高优先级）
            clip_sdf = rrect_sdf(fx, fy, clip_cx, clip_cy, clip_hw, clip_hh, clip_r)
            tab_sdf = rrect_sdf(fx, fy, tab_cx, tab_cy, tab_hw, tab_hh, tab_r)
            body = min(clip_sdf, tab_sdf)
            body_a = clamp(0.5 - body, 0.0, 1.0)
            if body_a > 0.0:
                # 白色剪贴板，边缘抗锯齿混合
                r = lerp(r, 255, body_a)
                g = lerp(g, 255, body_a)
                b = lerp(b, 255, body_a)
            else:
                # 其次判定阴影（主体右下方偏移，主体已覆盖大部分阴影）
                sh_sdf = rrect_sdf(fx, fy, sh_cx, sh_cy, clip_hw, clip_hh, clip_r)
                sh_a = clamp(0.5 - sh_sdf, 0.0, 1.0)
                if sh_a > 0.0:
                    r = lerp(r, 25, sh_a)
                    g = lerp(g, 55, sh_a)
                    b = lerp(b, 130, sh_a)

            row += bytes((int(r), int(g), int(b), int(bg_alpha * 255)))
        raw += row
    return raw

def make_png(size, raw):
    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    stride = size * 4
    scan = b''
    for y in range(size):
        scan += b'\x00' + bytes(raw[y * stride:(y + 1) * stride])
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) + chunk(b'IDAT', zlib.compress(scan, 9)) + chunk(b'IEND', b'')

def build_ico(pngs):
    entries = []
    offset = 6 + 16 * len(pngs)
    imgs = b''
    for s, data in pngs:
        b = s if s < 256 else 0
        entries.append(struct.pack('<BBBBHHII', b, b, 0, 0, 1, 32, len(data), offset))
        imgs += data
        offset += len(data)
    return struct.pack('<HHH', 0, 1, len(pngs)) + b''.join(entries) + imgs

if __name__ == '__main__':
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'assets')
    os.makedirs(out_dir, exist_ok=True)

    sizes = [16, 24, 32, 48, 64, 128, 256]
    pngs = []
    for s in sizes:
        raw = render(s)
        png = make_png(s, raw)
        pngs.append((s, png))

    with open(os.path.join(out_dir, 'ClipMate.ico'), 'wb') as f:
        f.write(build_ico(pngs))
    with open(os.path.join(out_dir, 'ClipMate.png'), 'wb') as f:
        f.write(pngs[-1][1])  # 256

    print('OK: ClipMate.ico + ClipMate.png, sizes', sizes)
    print('ico bytes:', os.path.getsize(os.path.join(out_dir, 'ClipMate.ico')))
    print('png bytes:', os.path.getsize(os.path.join(out_dir, 'ClipMate.png')))
