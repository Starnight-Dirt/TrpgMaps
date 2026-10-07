"""临时诊断：打印自检渲染图上的像素，用来定位断言采样点撞到了什么。"""
import struct
import sys
import zlib


def load_png(path):
    with open(path, 'rb') as f:
        data = f.read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n'
    pos = 8
    width = height = None
    bit_depth = color_type = None
    idat = b''
    while pos < len(data):
        length = struct.unpack('>I', data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        if tag == b'IHDR':
            width, height, bit_depth, color_type = struct.unpack('>IIBB', chunk[:10])
        elif tag == b'IDAT':
            idat += chunk
        pos += 12 + length

    raw = zlib.decompress(idat)
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color_type]
    if bit_depth != 8:
        raise SystemExit('only 8-bit supported, got %d' % bit_depth)

    stride = width * channels
    out = bytearray(height * stride)
    prev = bytearray(stride)
    p = 0
    for y in range(height):
        ft = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if ft == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ft == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                pa = abs(b - c)
                pb = abs(a - c)
                pc = abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return width, height, channels, out


def main():
    path = sys.argv[1]
    w, h, ch, px = load_png(path)
    print('size %dx%d channels=%d' % (w, h, ch))

    points = [(int(a.split(',')[0]), int(a.split(',')[1])) for a in sys.argv[2:]]
    for (x, y) in points:
        i = (y * w + x) * ch
        print('(%4d,%4d) = %s' % (x, y, tuple(px[i:i + 4])))

    # 找一下非深色且偏黄的像素都分布在哪（读数方块 / 候选点）
    buckets = {}
    for y in range(0, h, 3):
        for x in range(0, w, 3):
            i = (y * w + x) * ch
            r, g, b = px[i], px[i + 1], px[i + 2]
            if r > 150 and g > 120 and b < 200:
                key = (x // 100 * 100, y // 100 * 100)
                buckets[key] = buckets.get(key, 0) + 1
    top = sorted(buckets.items(), key=lambda kv: -kv[1])[:14]
    print('黄色像素最密的格子 (100px 网格):', top)


if __name__ == '__main__':
    main()
