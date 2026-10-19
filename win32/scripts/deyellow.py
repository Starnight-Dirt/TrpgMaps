#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
把人像里"被绿幕兑成暗橄榄绿"的火焰/光效重新染回暖色（红橙）。

为什么单独写一个脚本，而不并进 matting.py
------------------------------------------
因为这条规则**只适用于暖色魔法效果**（术士手里那圈火）。
放到通用流程里会误伤绿色系角色（德鲁伊的枝叶就是暗橄榄绿），
所以做成"按需、按图"的后处理 —— 谁需要就对谁跑。

判据（三条同时满足）：
  · 色相 40~115°（黄绿 ~ 绿）
  · 饱和度 > 50
  · **亮度上限**（默认 185）—— 火焰真正的亮黄核心（V>200）保留，
    只把那些"暗橄榄绿"的薄雾换成暖色。这一步是它不会把火画坏的关键。

用法：
    python deyellow.py <png...> [--value-max 185] [--hue 12] [--dry-run]
"""

import argparse
import sys

import numpy as np
from PIL import Image


def rgb_to_hsv(rgb):
    r, g, b = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    mx = rgb.max(axis=2)
    mn = rgb.min(axis=2)
    d = mx - mn
    h = np.zeros_like(mx)
    m = d > 1e-6
    i = m & (mx == r)
    h[i] = (60.0 * ((g[i] - b[i]) / d[i])) % 360.0
    i = m & (mx == g) & (mx != r)
    h[i] = 60.0 * ((b[i] - r[i]) / d[i]) + 120.0
    i = m & (mx == b) & (mx != r) & (mx != g)
    h[i] = 60.0 * ((r[i] - g[i]) / d[i]) + 240.0
    s = np.where(mx > 1e-6, d / np.maximum(mx, 1e-6), 0.0)
    return h, s, mx


def hsv_to_rgb(h, s, v):
    h = h % 360.0
    c = v * s
    x = c * (1 - np.abs((h / 60.0) % 2 - 1))
    m = v - c
    z = np.zeros_like(h)
    r = np.select([h < 60, h < 120, h < 180, h < 240, h < 300, True],
                  [c, x, z, z, x, c])
    g = np.select([h < 60, h < 120, h < 180, h < 240, h < 300, True],
                  [x, c, c, x, z, z])
    b = np.select([h < 60, h < 120, h < 180, h < 240, h < 300, True],
                  [z, z, x, c, c, x])
    return np.dstack([r + m, g + m, b + m]) * 255.0


def fix(path, hue_target=12.0, value_max=185.0, sat_min=0.20, dry=False):
    im = Image.open(path).convert('RGBA')
    a = np.asarray(im).astype(np.float32)
    rgb = a[:, :, :3]

    h, s, v = rgb_to_hsv(rgb)
    hit = (h > 40.0) & (h < 115.0) & (s > sat_min) & (v < value_max)

    if not hit.any():
        return 0

    out = hsv_to_rgb(np.full_like(h, hue_target), s, v / 255.0)
    rgb2 = rgb.copy()
    rgb2[hit] = out[hit]
    if not dry:
        Image.fromarray(np.dstack([np.clip(rgb2, 0, 255).astype(np.uint8),
                                   a[:, :, 3].astype(np.uint8)]), 'RGBA').save(path)
    return int(hit.sum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('files', nargs='+')
    ap.add_argument('--hue', type=float, default=12.0, help='目标色相（12 = 红橙）')
    ap.add_argument('--value-max', type=float, default=185.0, help='只处理比它暗的')
    ap.add_argument('--sat-min', type=float, default=0.20)
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    for f in args.files:
        n = fix(f, args.hue, args.value_max, args.sat_min, args.dry_run)
        print('  %-28s 改色像素 %6d' % (f.split('/')[-1], n))
    return 0


if __name__ == '__main__':
    sys.exit(main())
