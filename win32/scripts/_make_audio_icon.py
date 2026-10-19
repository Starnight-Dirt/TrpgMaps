"""生成侧栏「音频」图标 audio.png（28x28，白色音符，透明底）。

和 _make_draw_icon.py 同一套做法：纯标准库（zlib + struct）+ 4 倍超采样，
避免为一张图标引入 Pillow。运行目录必须是 win7版本\。
"""
import struct
import zlib

SIZE = 28
SS = 4
N = SIZE * SS


def blend(dst, src):
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


def ellipse(canvas, cx, cy, rx, ry, rgba):
    for y in range(int(cy - ry) - 1, int(cy + ry) + 2):
        for x in range(int(cx - rx) - 1, int(cx + rx) + 2):
            dx = (x + 0.5 - cx) / rx
            dy = (y + 0.5 - cy) / ry
            if dx * dx + dy * dy <= 1.0:
                put(canvas, x, y, rgba)


def segment(canvas, x0, y0, x1, y1, width, rgba):
    dx = x1 - x0
    dy = y1 - y0
    length = max(1.0, (dx * dx + dy * dy) ** 0.5)
    steps = int(length * 2) + 1
    for i in range(steps + 1):
        t = i / steps
        ellipse(canvas, x0 + dx * t, y0 + dy * t, width / 2.0, width / 2.0, rgba)


def arc(canvas, cx, cy, radius, a0, a1, width, rgba):
    """一段圆弧（声波用）。角度制，0 度指向右方。"""
    steps = max(6, int(abs(a1 - a0) * radius / 3.0))
    for i in range(steps + 1):
        a = a0 + (a1 - a0) * i / steps
        rad = a * 3.14159265358979 / 180.0
        ellipse(canvas, cx + radius * __import__("math").cos(rad),
                cy + radius * __import__("math").sin(rad),
                width / 2.0, width / 2.0, rgba)


def main():
    canvas = [[TRANSPARENT] * N for _ in range(N)]

    note = (255, 250, 250, 250)
    soft = (150, 255, 255, 255)

    # ---- 八分音符：符头（椭圆）+ 符杆 + 符尾 ----
    ellipse(canvas, 9.2 * SS, 19.4 * SS, 4.4 * SS, 3.3 * SS, note)      # 符头
    segment(canvas, 12.9 * SS, 19.4 * SS, 12.9 * SS, 6.6 * SS, 2.6 * SS, note)   # 符杆
    segment(canvas, 12.9 * SS, 6.6 * SS, 19.4 * SS, 9.6 * SS, 2.4 * SS, note)    # 符尾上缘
    segment(canvas, 19.4 * SS, 9.6 * SS, 19.4 * SS, 14.2 * SS, 2.2 * SS, note)   # 符尾下垂

    # ---- 两道声波弧线（"在响"的意思），半透明一点，免得抢主体 ----
    arc(canvas, 8.0 * SS, 14.0 * SS, 9.4 * SS, -52, 52, 1.5 * SS, soft)
    arc(canvas, 8.0 * SS, 14.0 * SS, 12.2 * SS, -46, 46, 1.3 * SS, soft)

    out = bytearray()
    for y in range(SIZE):
        out.append(0)
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

    with open("wwwroot/images/audio.png", "wb") as f:
        f.write(png)
    print("wrote wwwroot/images/audio.png, %d bytes" % len(png))


def _chunk(tag, data):
    return (struct.pack(">I", len(data)) + tag + data +
            struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff))


if __name__ == "__main__":
    main()
