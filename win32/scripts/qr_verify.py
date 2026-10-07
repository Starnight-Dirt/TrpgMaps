"""独立校验 QrCodeGenerator 的输出：完整解码二维码，核对文本是否与预期一致。

只依赖标准库（zlib），实现内容：
  1. 读 PNG -> 灰度行 -> 按模块采样
  2. 读格式信息并做 BCH 校正，还原纠错等级与掩码
  3. 依 ISO/IEC 18004 还原功能图形占位、去掩码、zigzag 取出码字
  4. 按 RS 分块校验并纠错，还原出文本
用法：python qr_verify.py <png> <expected-text>
"""
import sys
import zlib

PNG_SIG = b"\x89PNG\r\n\x1a\n"

# ---------- GF(256) ----------
EXP = [0] * 512
LOG = [0] * 256
_x = 1
for _i in range(255):
    EXP[_i] = _x
    LOG[_x] = _i
    _x <<= 1
    if _x >= 256:
        _x ^= 0x11D
for _i in range(255, 512):
    EXP[_i] = EXP[_i - 255]


def gf_mul(a, b):
    if a == 0 or b == 0:
        return 0
    return EXP[LOG[a] + LOG[b]]


def gf_pow(e):
    return EXP[e]


# ---------- 版本参数（与 C# 端一致，等级顺序 L,M,Q,H）----------
SPECS = {
    1: [(26, 19, 7, 1, 0), (26, 16, 10, 1, 0), (26, 13, 13, 1, 0), (26, 9, 17, 1, 0)],
    2: [(44, 34, 10, 1, 0), (44, 28, 16, 1, 0), (44, 22, 22, 1, 0), (44, 16, 28, 1, 0)],
    3: [(70, 55, 15, 1, 0), (70, 44, 26, 1, 0), (70, 34, 18, 2, 0), (70, 26, 22, 2, 0)],
    4: [(100, 80, 20, 1, 0), (100, 64, 18, 2, 0), (100, 48, 26, 2, 0), (100, 36, 16, 4, 0)],
    5: [(134, 108, 26, 1, 0), (134, 86, 24, 2, 0), (134, 62, 18, 2, 2), (134, 46, 22, 2, 2)],
    6: [(172, 136, 18, 2, 0), (172, 108, 16, 4, 0), (172, 76, 24, 4, 0), (172, 60, 28, 4, 0)],
    7: [(196, 156, 20, 2, 0), (196, 124, 18, 4, 0), (196, 88, 18, 2, 4), (196, 66, 26, 4, 1)],
    8: [(242, 194, 24, 2, 0), (242, 154, 22, 4, 0), (242, 110, 22, 2, 4), (242, 86, 26, 4, 2)],
    9: [(292, 232, 30, 2, 0), (292, 182, 14, 3, 1), (292, 132, 20, 4, 4), (292, 100, 18, 4, 4)],
    10: [(346, 274, 18, 2, 2), (346, 216, 30, 4, 1), (346, 154, 28, 6, 2), (346, 122, 24, 6, 2)],
}
ALIGN = {1: [], 2: [6, 18], 3: [6, 22], 4: [6, 26], 5: [6, 30], 6: [6, 34],
         7: [6, 22, 38], 8: [6, 24, 42], 9: [6, 26, 46], 10: [6, 28, 50]}
LEVEL_BITS = {1: 0, 0: 1, 3: 2, 2: 3}   # 格式位 -> 等级索引
LEVEL_BITS_INV = {0: 1, 1: 0, 2: 3, 3: 2}


def read_png_gray(path):
    data = open(path, "rb").read()
    assert data[:8] == PNG_SIG, "not a PNG"
    pos, width, height, bitdepth, colortype, idat = 8, None, None, None, None, bytearray()
    while pos < len(data):
        length = int.from_bytes(data[pos:pos + 4], "big")
        ctype = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        if ctype == b"IHDR":
            width = int.from_bytes(chunk[0:4], "big")
            height = int.from_bytes(chunk[4:8], "big")
            bitdepth, colortype = chunk[8], chunk[9]
            assert chunk[12] == 0, "interlaced PNG unsupported"
        elif ctype == b"IDAT":
            idat += chunk
        elif ctype == b"IEND":
            break
        pos += 12 + length
    assert bitdepth == 8 and colortype == 0, "expected 8-bit grayscale"
    raw = zlib.decompress(bytes(idat))
    stride = width + 1
    rows = []
    for y in range(height):
        s = y * stride
        assert raw[s] == 0, f"row {y} filter {raw[s]} unsupported"
        rows.append(raw[s + 1:s + 1 + width])
    return width, height, rows


def to_matrix(path, quiet=2, scale=8):
    width, height, rows = read_png_gray(path)
    assert width % scale == 0 and height % scale == 0, "size not a multiple of scale"
    modules = width // scale - quiet * 2
    m = []
    for my in range(modules):
        row = []
        for mx in range(modules):
            row.append(1 if rows[(my + quiet) * scale][(mx + quiet) * scale] < 128 else 0)
        m.append(row)
    return m


def function_mask(version):
    """标出功能图形（定位/定时/校正/格式/版本信息）的位置。"""
    size = 17 + 4 * version
    f = [[False] * size for _ in range(size)]

    def mark_rect(x0, y0, w, h):
        for y in range(y0, y0 + h):
            for x in range(x0, x0 + w):
                if 0 <= x < size and 0 <= y < size:
                    f[y][x] = True

    # 三处定位图形 + 分隔符
    mark_rect(0, 0, 9, 9)
    mark_rect(size - 8, 0, 8, 9)
    mark_rect(0, size - 8, 9, 8)
    # 校正图形（含 5x5 中心 + 空白边 -> 取 5x5 足够，边界由分隔符覆盖）
    pos = ALIGN[version]
    for cy in pos:
        for cx in pos:
            if (cx == 6 and cy == 6) or (cx == 6 and cy == size - 7) or (cx == size - 7 and cy == 6):
                continue
            mark_rect(cx - 2, cy - 2, 5, 5)
    # 定时图形
    for i in range(8, size - 8):
        f[6][i] = True
        f[i][6] = True
    # 固定深色模块 + 格式信息
    for i in range(9):
        if i != 6:
            f[8][i] = True
            f[i][8] = True
    for i in range(8):
        f[8][size - 1 - i] = True
        f[size - 1 - i][8] = True
    f[size - 8][8] = True
    # 版本信息（v>=7）
    if version >= 7:
        for y in range(6):
            for x in range(size - 11, size - 8):
                f[y][x] = True
                f[x][y] = True
    return f


def mask_condition(mask, x, y):
    return [
        (x + y) % 2 == 0,
        y % 2 == 0,
        x % 3 == 0,
        (x + y) % 3 == 0,
        (y // 2 + x // 3) % 2 == 0,
        x * y % 2 + x * y % 3 == 0,
        (x * y % 2 + x * y % 3) % 2 == 0,
        ((x + y) % 2 + x * y % 3) % 2 == 0,
    ][mask]


def bch_format_ok(bits15):
    """检查 15 位格式信息是否满足 BCH(15,5)，返回 (data5, 是否有效)。"""
    d = bits15
    for i in range(4, -1, -1):
        if (d >> (i + 10)) & 1:
            d ^= 0b10100110111 << i
    return (bits15 >> 10) & 0x1F, d == 0


def read_format(m):
    """按 ISO/IEC 18004 的位置读取第一份格式信息。

    位序：i=0 是最高位 bit14（纠错等级），i=14 是最低位。
    """
    bits = 0
    for i in range(15):
        if i < 6:
            bit = m[8][i]
        elif i == 6:
            bit = m[8][7]
        elif i == 7:
            bit = m[8][8]
        elif i == 8:
            bit = m[7][8]
        else:
            bit = m[14 - i][8]
        bits |= bit << (14 - i)
    return bits ^ 0b101010000010010


def read_codewords(m, version, mask):
    size = len(m)
    f = function_mask(version)
    bits = []
    right = size - 1
    while right >= 1:
        if right == 6:
            right = 5
        for vert in range(size):
            for j in range(2):
                x = right - j
                upward = ((right + 1) & 2) == 0
                y = size - 1 - vert if upward else vert
                if f[y][x]:
                    continue
                bit = m[y][x]
                if mask_condition(mask, x, y):
                    bit ^= 1
                bits.append(bit)
        right -= 2
    # 每 8 位一个码字
    words = []
    for i in range(0, len(bits) - 7, 8):
        v = 0
        for b in bits[i:i + 8]:
            v = (v << 1) | b
        words.append(v)
    return words


def build_generator(degree):
    poly = [1]
    for i in range(degree):
        root = gf_pow(i)
        nxt = [0] * (len(poly) + 1)
        for j, c in enumerate(poly):
            nxt[j] ^= c
            nxt[j + 1] ^= gf_mul(c, root)
        poly = nxt
    return poly[1:]


def rs_syndromes_ok(block, ecc_count):
    """用生成多项式做多项式除法，余数为 0 说明校验位正确。"""
    gen = build_generator(ecc_count)
    rem = [0] * ecc_count
    for b in block:
        factor = b ^ rem[0]
        rem = rem[1:] + [0]
        if factor:
            for i in range(ecc_count):
                rem[i] ^= gf_mul(gen[i], factor)
    return rem


def deinterleave(words, spec):
    total, data_cw, ecc, g1, g2 = spec
    blocks_n = g1 + g2
    g1_len = data_cw // blocks_n
    g2_len = g1_len + 1
    g2_count = data_cw % blocks_n

    data_blocks = [[] for _ in range(blocks_n)]
    idx = 0
    max_len = g2_len if g2_count else g1_len
    for i in range(max_len):
        for b in range(blocks_n):
            # 前 (blocks_n - g2_count) 块是短块
            is_long = b >= blocks_n - g2_count
            blen = g2_len if is_long else g1_len
            if i < blen:
                data_blocks[b].append(words[idx])
                idx += 1

    ecc_blocks = [[] for _ in range(blocks_n)]
    for i in range(ecc):
        for b in range(blocks_n):
            ecc_blocks[b].append(words[idx])
            idx += 1

    return data_blocks, ecc_blocks, idx, total


def main():
    path, expected = sys.argv[1], sys.argv[2]
    m = to_matrix(path)
    size = len(m)
    if (size - 17) % 4 != 0:
        print(f"FAIL: 模块数 {size} 不是合法二维码尺寸")
        return 1
    version = (size - 17) // 4

    fmt = read_format(m)
    data5, ok = bch_format_ok(fmt)
    level_bits = (data5 >> 3) & 0x3
    mask = data5 & 0x7
    level = LEVEL_BITS[level_bits]

    # 逐位打印格式信息读取结果，便于对照标准位序
    raw_bits = 0
    for i in range(15):
        if i < 6:
            bit = m[8][i]
        elif i == 6:
            bit = m[8][7]
        elif i == 7:
            bit = m[8][8]
        elif i == 8:
            bit = m[7][8]
        else:
            bit = m[14 - i][8]
        raw_bits |= bit << (14 - i)
    print(f"格式原始位=0x{raw_bits:04X} 去掩码=0x{fmt:04X} data5={data5:05b} level_bits={level_bits:02b} mask={mask}")
    print(f"版本={version} 尺寸={size}x{size} 掩码={mask} 纠错等级={['L','M','Q','H'][level]} 格式BCH={'有效' if ok else '无效'}")

    spec = SPECS[version][level]
    words = read_codewords(m, version, mask)
    data_blocks, ecc_blocks, used, total = deinterleave(words, spec)
    print(f"码字总数读入={len(words)} 需要={total} 使用={used} 数据码字={spec[1]} 纠错/块={spec[2]} 块数={spec[3]}+{spec[4]}")

    all_ok = True
    for i, (d, e) in enumerate(zip(data_blocks, ecc_blocks)):
        rem = rs_syndromes_ok(d + e, spec[2])
        good = all(v == 0 for v in rem)
        if not good:
            all_ok = False
        print(f"  块{i}: 数据={len(d)} 纠错={len(e)} RS校验={'通过' if good else '失败'}")

    bits = []
    for d in data_blocks:
        for v in d:
            for k in range(7, -1, -1):
                bits.append((v >> k) & 1)

    def take(n):
        v = 0
        for _ in range(n):
            v = (v << 1) | bits.pop(0)
        return v

    mode = take(4)
    if mode != 4:
        print(f"FAIL: 模式指示符={mode}，期望 4（字节模式）")
        return 1
    count = take(8 if version <= 9 else 16)
    payload = bytes(take(8) for _ in range(count))
    text = payload.decode("utf-8")
    print(f"模式=字节 字符数={count} 文本={text!r}")

    ok_text = text == expected
    print("")
    print(f"RS 校验：{'全部通过' if all_ok else '存在失败'}")
    print(f"文本比对：{'一致' if ok_text else '不一致'}（期望 {expected!r}）")
    if all_ok and ok_text and ok:
        print("RESULT: PASS")
        return 0
    print("RESULT: FAIL")
    return 1


if __name__ == "__main__":
    sys.exit(main())
