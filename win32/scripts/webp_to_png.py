#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
把带透明通道的 WebP 角色图转成**程序能正确显示的 PNG**。

为什么必须转
------------
本项目的 WebP 解码器是纯托管的，只实现了**有损 RGB（VP8）**。
带透明的 WebP 把 alpha 放在单独的 `ALPH` 块里，而且是**无损（VP8L）压缩**的 ——
那需要一整套 VP8L 解码器（Huffman + LZ77 + 预测/调色板变换），本项目没有。
结果就是：alpha 被忽略、全图变成不透明，边缘那圈半透明亮像素变成"白色撕裂"。

两件关键处理
------------
1. **alpha 补色（alpha bleed）**：把透明区域的 RGB 用最近的实心像素颜色填满。
   否则缩小/贴图时，插值会把"透明区里残留的背景色"拉进边缘 —— 白边/绿边就是这么来的。
   这一步在**原尺寸**上做，再做缩放，缩小时样本才全是角色色。
2. 缩小到 `--size`（默认 512）。地图上一格约 95px，512 已经 5 倍余量；
   1024 的 PNG 单张 1.2MB，26 张 28MB，没必要。

用法：
    python webp_to_png.py <输入目录> <输出目录> [--size 512] [--keep-size]
"""

import argparse
import glob
import os
import sys

import numpy as np
from PIL import Image


def alpha_bleed(rgb, alpha, iters=16):
    """把实心像素的颜色一圈圈推进透明区（只改 RGB，不动 alpha）。"""
    filled = alpha > 8
    if not filled.any():
        return rgb
    out = rgb.copy()
    for _ in range(iters):
        acc = np.zeros_like(out)
        cnt = np.zeros(alpha.shape, dtype=np.float32)
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            f = np.roll(filled, (dy, dx), axis=(0, 1))
            c = np.roll(out, (dy, dx), axis=(0, 1))
            acc += c * f[:, :, None]
            cnt += f
        new = (~filled) & (cnt > 0)
        if not new.any():
            break
        out[new] = acc[new] / cnt[new][:, None]
        filled |= new
    return out


def convert(src, dst, size, keep_size=False):
    im = Image.open(src)
    a = np.asarray(im.convert('RGBA')).astype(np.float32)
    rgb, alpha = a[:, :, :3], a[:, :, 3]

    rgb = alpha_bleed(rgb, alpha)

    out = Image.fromarray(np.dstack([np.clip(rgb, 0, 255).astype(np.uint8),
                                     alpha.astype(np.uint8)]), 'RGBA')
    if not keep_size and size > 0 and out.size[0] != size:
        out = out.resize((size, size), Image.LANCZOS)
    out.save(dst, optimize=True)
    return out.size


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('input')
    ap.add_argument('output')
    ap.add_argument('--size', type=int, default=512)
    ap.add_argument('--keep-size', action='store_true')
    args = ap.parse_args()

    os.makedirs(args.output, exist_ok=True)
    files = sorted(glob.glob(os.path.join(args.input, '*.webp')))
    if not files:
        print('没有找到 webp')
        return 1

    total = 0
    for f in files:
        name = os.path.splitext(os.path.basename(f))[0] + '.png'
        dst = os.path.join(args.output, name)
        size = convert(f, dst, args.size, args.keep_size)
        kb = os.path.getsize(dst) // 1024
        total += kb
        print('  %-24s %s  %5d KB' % (name[:22], '%dx%d' % size, kb))
    print('\n合计 %d 张 / %d KB' % (len(files), total))
    return 0


if __name__ == '__main__':
    sys.exit(main())
