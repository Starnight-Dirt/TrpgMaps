"""生成侧栏「绘图」图标 draw.png（28x28，白色笔画 + 半透明网格，透明底）。

用纯标准库（zlib + struct）写 PNG，避免为一张图标引入 Pillow。
做法：4 倍超采样再降采样，得到干净的边缘。
"""
import struct
import zlib

SIZE = 28
SS = 4          # 超采样倍数
N = SIZE * SS


def blend(dst, src):
    """src over dst，两者都是 (a, r, g, b) 且已预乘 alpha。"""
    sa = src[0]
    if sa == 0:
        return dst
    if sa == 255:
        return src
    ia = 255 - sa
    return (
        min(255, sa + dst[0] * ia // 255),
        min(255, src[1] + dst[1] * ia // 255),
        min(255, src[2] + dst[2] * ia // 255),
        min(255, src[3] + dst[3] * ia // 255),
    )


def put(canvas, x, y, rgba):
    if 0 <= x < N and 0 <= y < N:
        canvas[y][x] = blend(canvas[y][x], rgba)


TRANSPARENT = (0, 0, 0, 0)


def disc(canvas, cx, cy, radius, rgba):
    r2 = radius * radius
    for y in range(int(cy - radius) - 1, int(cy + radius) + 2):
        for x in range(int(cx - radius) - 1, int(cx + radius) + 2):
            dx = x + 0.5 - cx
            dy = y + 0.5 - cy
            if dx * dx + dy * dy <= r2:
                put(canvas, x, y, rgba)


def segment(canvas, x0, y0, x1, y1, width, rgba):
    """粗线段（胶囊形）：沿线撒圆点。"""
    dx = x1 - x0
    dy = y1 - y0
    length = max(1.0, (dx * dx + dy * dy) ** 0.5)
    steps = int(length * 2) + 1
    for i in range(steps + 1):
        t = i / steps
        disc(canvas, x0 + dx * t, y0 + dy * t, width / 2.0, rgba)


def rect(canvas, x0, y0, x1, y1, rgba):
    for y in range(int(y0), int(y1) + 1):
        for x in range(int(x0), int(x1) + 1):
            put(canvas, x, y, rgba)


def main():
    canvas = [[TRANSPARENT] * N for _ in range(N)]

    # ---- 底衬：一张细网格（"地图"的意思），半透明白 ----
    grid = (70, 255, 255, 255)
    for i in range(3):
        v = int((7 + i * 7) * SS)
        for t in range(int(5 * SS), int(23 * SS) + 1):
            put(canvas, v, t, grid)
            put(canvas, t, v, grid)

    # ---- 画笔：从右下到左上的一根斜杆 + 一个尖头 ----
    body = (255, 250, 250, 250)
    tip = (255, 180, 190, 255)

    segment(canvas, 9 * SS, 19 * SS, 19 * SS, 9 * SS, 5.0 * SS, body)   # 笔杆
    segment(canvas, 6 * SS, 22 * SS, 10 * SS, 18 * SS, 4.4 * SS, tip)   # 笔尖（略偏暖）
    disc(canvas, 19.6 * SS, 8.4 * SS, 2.6 * SS, body)                   # 笔尾圆头

    # ---- 降采样 ----
    out = bytearray()
    for y in range(SIZE):
        out.append(0)  # filter type 0
        for x in range(SIZE):
            ar = gr = gg = gb = 0
            for sy in range(SS):
                row = canvas[y * SS + sy]
                for sx in range(SS):
                    px = row[x * SS + sx]
                    ar += px[0]
                    gr += px[1]
                    gg += px[2]
                    gb += px[3]
            n = SS * SS
            out += bytes((ar // n, gr // n, gg // n, gb // n))

    raw = bytes(out)
    png = b"\x89PNG\r\n\x1a\n"
    png += _chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += _chunk(b"IDAT", zlib.compress(raw, 9))
    png += _chunk(b"IEND", b"")

    with open("wwwroot/images/draw.png", "wb") as f:
        f.write(png)
    print("wrote wwwroot/images/draw.png, %d bytes" % len(png))


def _chunk(tag, data):
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))


if __name__ == "__main__":
    main()
