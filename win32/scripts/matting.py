#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
纯色背景角色立绘 -> 透明底 PNG（色键 + 边缘去色 + 去水印），零依赖外部服务。

为什么这么做
------------
这批图是 AI 生成的「单一纯色背景」立绘（每个职业一种背景色）。这类图是**色键**
（chroma key / 蓝幕）的标准场景：本地算一遍一秒一张，不联网、不花钱、参数可复现。

四件事
------
1. **抠背景**：算每个像素离**逐像素背景色**的距离，软阈值 -> alpha 线性过渡（天然抗锯齿）。
   背景色不是取一个常数，而是先挑"肯定是背景"的像素做一张平滑的**背景场**
   （AI 图的"纯色"实测有 10~13 的缓慢渐变/暗角，用一个常数会在深色底上留下淡色幽灵）。

2. **去彩色描边**（`--decontam`）：这是这类图的通病 —— 边缘 1~2 像素其实是
   `C = a·角色 + (1-a)·背景` 的混合，但它们的 RGB 早就离背景很远了，于是被判成
   alpha≈1，颜色却还是背景色，最终表现为一圈**彩色描边**（橙底→橙边，浅青底→青边）。
   解法：把角色本体的颜色**向外扩散**几圈盖掉它。
   ⚠️ 不用反预乘 `(C-(1-a)B)/a` —— a 很小时除法会把噪声放大成偏色（实测把橙边算成紫边）。

3. **去水印**：右下角"豆包AI生成"是**固定位置**的白色文字 + 一圈灰色投影。
   在一个固定相对框里删掉**低饱和**像素（白字与灰影）——角色本体饱和度高，不会被误伤。
   ⚠️ 不要用"白字掩膜的外接框"去扩：角色身上的白色高光（翅膀/肚皮）会把框撑到画面中部，
      结果整块角色被删。

4. **可选特征检查**（`--check`）：报告"被前景包围的透明像素"（= 角色身上被误抠出的洞）。

用法
----
    python matting.py <输入目录> [输出目录] [选项]

    --lo / --hi          背景距离软阈值（默认按背景噪声自动推）
    --key line|dist      line = 色线投影求 alpha（默认）；dist = 只按颜色距离
    --decontam           去色强度 0~1（默认 1.0；0 = 不去色）
    --alpha-cut          低于它的 alpha 直接置 0（默认 0.06）
    --feather            alpha 羽化半径 px（默认 0.5）
    --no-wm              不去水印
    --check              额外输出"洞"与"边缘残留色"两项检查
    --only 关键字        只处理文件名含该串的图
    --dry-run            只统计不写文件

依赖：numpy + Pillow（本机隔离环境里都有）。不依赖 scipy / OpenCV。
"""

import argparse
import glob
import os
import sys

import numpy as np
from PIL import Image, ImageFilter


# --------------------------------------------------------------------------- #
# 基础工具
# --------------------------------------------------------------------------- #

def estimate_background(rgb):
    """背景色 = 最外一圈像素的中位数（比只取四角稳，且对边框噪点不敏感）。"""
    ring = np.concatenate([
        rgb[0:4].reshape(-1, 3), rgb[-4:].reshape(-1, 3),
        rgb[:, 0:4].reshape(-1, 3), rgb[:, -4:].reshape(-1, 3),
    ])
    return np.median(ring, axis=0)


def estimate_noise(rgb, bg):
    """背景噪声：最外一圈"离背景色有多远"的标准差。用来定 lo/hi。"""
    ring = np.concatenate([
        rgb[0:4].reshape(-1, 3), rgb[-4:].reshape(-1, 3),
        rgb[:, 0:4].reshape(-1, 3), rgb[:, -4:].reshape(-1, 3),
    ])
    d = np.abs(ring - bg).max(axis=1)
    return float(d.std()), float(np.percentile(d, 99))


def auto_thresholds(rgb, bg):
    """
    按背景噪声推 lo/hi。

    经验值（26 张实测）：背景标准差 1.7~6.9。
      lo = 3.5σ   —— 低于它的肯定是背景（含压缩噪点）
      hi = lo+20  —— 高于它的肯定是角色
    为什么 hi 不放大：浅色背景上"米白袖子"离背景只有 36，hi 放到 55 会把袖子
    做成半透明（这是第一版翻车的原因之一）。
    """
    std, p99 = estimate_noise(rgb, bg)
    # lo 取"噪声上界 + 4"：AI 图脚下那层**软投影**只有 16~25 的差距，
    # lo 太低就会把它抠成半透明灰云（实测 dist=16 的阴影被赋成 alpha=0.52）。
    lo = max(14.0, round(std * 3.5 + 4.0, 1))
    # hi 只比 lo 高 16：浅色背景上"米白袖子"离背景只有 36，hi 放宽会让袖子半透明。
    hi = lo + 16.0
    return lo, hi, std


def _dilate4(mask):
    """4 邻域膨胀一格（numpy 平移 OR；不引 scipy）。"""
    out = mask.copy()
    out[1:, :] |= mask[:-1, :]
    out[:-1, :] |= mask[1:, :]
    out[:, 1:] |= mask[:, :-1]
    out[:, :-1] |= mask[:, 1:]
    return out


def reachable_from_border(walkable):
    """从图像边界出发、只在 walkable 上能走到的区域（= 外部背景）。"""
    h, w = walkable.shape
    cur = np.zeros_like(walkable)
    cur[0, :] = walkable[0, :]
    cur[-1, :] = walkable[-1, :]
    cur[:, 0] = walkable[:, 0]
    cur[:, -1] = walkable[:, -1]
    for _ in range(max(h, w) + 4):
        nxt = _dilate4(cur) & walkable
        if np.array_equal(nxt, cur):
            break
        cur = nxt
    return cur


# --------------------------------------------------------------------------- #
# 主流程
# --------------------------------------------------------------------------- #

def build_matte(path, lo=None, hi=None, feather=0.5, decontam=1.0,
                decontam_min_a=0.30, alpha_cut=0.20, wm=True, check=False,
                key='line', glow=True):
    src = Image.open(path)
    arr = np.asarray(src.convert('RGBA'), dtype=np.float32)
    rgb = arr[:, :, :3].copy()
    h, w = rgb.shape[:2]

    bg = estimate_background(rgb)
    if lo is None or hi is None:
        a_lo, a_hi, noise = auto_thresholds(rgb, bg)
        lo = a_lo if lo is None else lo
        hi = a_hi if hi is None else hi
    else:
        noise = estimate_noise(rgb, bg)[0]

    # ---- 1. 色键：离背景多远 -> alpha（初值）----
    #   逐像素背景色（对付 AI 图的背景渐变/暗角）
    field = _bg_field(rgb, bg, 10.0)
    #   先把图**轻微去噪**再算距离：AI 图背景自带颗粒噪点，逐像素比距离会把这些噪点
    #   算成 alpha 0.1~0.5，透明区就变成"灰麻点"。0.8px 高斯足够压掉颗粒，又几乎不动边缘。
    den = np.asarray(Image.fromarray(rgb.astype(np.uint8), 'RGB')
                     .filter(ImageFilter.GaussianBlur(0.8)), dtype=np.float32)
    dist = np.abs(den - field).max(axis=2)
    alpha = np.clip((dist - lo) / max(1e-6, hi - lo), 0.0, 1.0)

    # ---- 1b. 色线投影：软边处用"投影"求 alpha，消掉彩色描边的根 ----
    if key == 'line':
        alpha = _line_key(rgb, field, alpha)

    # ---- 2. 极低 alpha 直接收干净 ----
    # 这些像素几乎看不见，却携带最多的噪声；留着只会变成一圈脏边。
    if alpha_cut > 0:
        alpha[alpha < alpha_cut] = 0.0

    # ---- 3. 去水印（右下角白色文字）----
    wm_px = 0
    wm_erase = None
    if wm:
        bg_lum = float(np.median(field.mean(axis=2)))
        lum = rgb.mean(axis=2)
        sat = rgb.max(axis=2) - rgb.min(axis=2)
        # 水印 = 右下角的白色文字（"豆包AI生成"），位置是模板固定的
        #   （实测 1536² 图上落在 x 0.82w~1.0w、y 0.945h~0.985h）。
        # 判据只用"比背景亮"：它在橙底上会被染成浅橙，单看颜色判不出来；
        # 而角色本体（绿/棕/紫）比背景暗，不会被这条判据碰到（德鲁伊那只小精灵的脚就是绿的）。
        lum_s = rgb.mean(axis=2)
        rect = np.zeros((h, w), dtype=bool)
        rect[int(h * 0.943):, int(w * 0.815):] = True
        bright = rect & (lum_s > bg_lum + 8.0)
        if bright.sum() > 80:
            kill = _dilate4(_dilate4(_dilate4(bright))) & rect   # 连字的抗锯齿/灰影一起
            wm_px = int((kill & (alpha > 0)).sum())
            alpha[kill] = 0.0
            wm_erase = kill

    # ---- 3b. 清掉远离主体的孤立碎屑 ----
    alpha = _drop_islands(alpha)

    # ---- 3c. 光晕 -> 半透明白；地面投影 -> 半透明黑 ----
    #    这两块要是当成实心前景，贴到地图上就是"一圈色饼 + 一块灰斑"。
    if glow:
        alpha = _gate_near_body(alpha, dist, gap=6, strong=80.0)
        alpha, hue_drop = _drop_bg_hue(rgb, field, alpha)
        alpha = _fill_holes(alpha)          # 浅色部位被误判成透明 -> 补回来
        light_pct = dark_pct = 0.0
    else:
        light_pct = dark_pct = 0.0

    # 水印必须**最后**再压一次：上面那步会把"亮度偏高的平滑区域"当光晕重新赋 alpha，
    # 水印字正好符合（白字、平滑），不压就会被算回来 —— 表现就是"删了但还剩碎点字"。
    if wm_erase is not None:
        alpha[wm_erase] = 0.0

    # ---- 4. 边缘去色（消掉背景色描边）----
    #   把角色本体的颜色往外扩几圈，盖掉"半透明边缘里混着的背景色"。
    #   ⚠️ 不用反预乘：a 小时的除法会把噪声放大成偏色（实测把橙边算成紫边）。
    if decontam > 0:
        bled, changed = _bleed_edge_color(rgb, alpha, outward=4, erode=1)
        # 只改"被扩散到的那一圈"，而且只改看得见的像素；内部色源本身不动
        band = (changed & (alpha > 0.02))[:, :, None]
        k = decontam
        rgb = np.where(band, rgb * (1.0 - k) + bled * k, rgb)

    # ---- 5. 羽化（只动 alpha，不动颜色）----
    if feather > 0:
        a_img = Image.fromarray((alpha * 255).astype(np.uint8), 'L')
        a_img = a_img.filter(ImageFilter.GaussianBlur(feather))
        alpha = np.asarray(a_img, dtype=np.float32) / 255.0
        alpha[alpha < 0.02] = 0.0

    # 绿幕重绘的收尾：压掉渗进边缘的绿（只影响"绿得反常"的像素）。
    # 火焰这类被绿兑成"暗橄榄绿"的薄雾，这条压不掉 —— 那属于暖色魔法效果，
    # 单独用 scripts/deyellow.py 定向处理（只对该类角色跑，避免误伤绿色系角色）。
    if key == 'line':
        rgb, _ = _despill_green(rgb)

    rgba = np.dstack([np.clip(rgb, 0, 255).astype(np.uint8),
                      np.clip(alpha * 255, 0, 255).astype(np.uint8)])
    out = Image.fromarray(rgba, 'RGBA')

    info = {
        'bg': bg, 'lo': lo, 'hi': hi, 'noise': noise,
        'transparent': float((alpha < 0.02).mean()),
        'wm_px': wm_px, 'light': light_pct, 'dark': dark_pct,
    }

    # ---- 6. 可选检查：洞 / 边缘残留色 ----
    if check:
        holes = _count_holes(alpha)
        info['holes'] = holes
        info['fringe'] = _fringe_score(rgb, alpha, bg)
    return out, info


def _count_holes(alpha, min_area=150):
    """
    被前景包围的透明像素（= 角色身上被误抠出来的洞）。

    真背景一定能走到图像边界，所以"走不到边界的透明像素"就是洞。
    ⚠️ 在**下采样网格**上做（4×）—— 全尺寸做洪水填充要迭代上千轮，
       单张就要两分钟；下采样后 100ms 级别，而"洞"都是成片的，4× 完全够。
    """
    step = max(1, int(round(max(alpha.shape) / 384.0)))
    small = (alpha < 0.1)[::step, ::step]
    outside = reachable_from_border(small)
    holes = small & (~outside)
    if not holes.any():
        return 0

    n = 0
    rest = holes.copy()
    while rest.any():
        ys, xs = np.nonzero(rest)
        seed = np.zeros_like(rest)
        seed[ys[0], xs[0]] = True
        cur = seed
        while True:
            nxt = _dilate4(cur) & holes
            if np.array_equal(nxt, cur):
                break
            cur = nxt
        if cur.sum() * step * step >= min_area:
            n += 1
        rest &= ~cur
    return n


def _glow_and_shadow(rgb, field, alpha, ring=4, smooth_tol=7.0):
    """
    把"主体之外、平滑、靠近背景色但更亮/更暗"的像素分解成**光**和**影**。

    为什么必须单独处理
    ------------------
    角色周围那圈光晕、脚下那片投影，在**色键**看来都"离背景很远" ⇒ 被判成 alpha=1、
    颜色原样保留 ⇒ 光晕变成一块**实心色饼**、投影变成一块**实心灰斑**。
    这两块就是贴到地图上最刺眼的东西。

    物理上它们是"在背景上叠加的光"和"被压暗的背景"：
      · 光：`lum = bg_lum + a·(255−bg_lum)`  →  `a = (lum−bg_lum)/(255−bg_lum)`，颜色→白
      · 影：`lum = bg_lum·(1−a)`             →  `a = 1 − lum/bg_lum`，        颜色→黑
    按这个反解，光晕就是**半透明白**、投影就是**半透明黑**，换到任何底图上都成立。

    ⚠️ 用**亮度**算 a，不要用三通道均值 —— 发光像素常在某个通道比背景低
       （暖金光晕的蓝通道），均值会被负值带偏，结果 a 算成 0 或乱跳。

    三条保险，避免误伤角色：
      · 只处理**主体之外**（主体腐蚀 2px，再向外 ring 圈内仍按"边缘"走颜色投影）；
      · 只处理**平滑**区域（5×5 局部标准差 < smooth_tol）—— 剑刃、发丝这类硬边排除；
      · 只处理**低饱和**区域 —— 术士的红色火焰、魔契师的紫色能量饱和度高，不会被当成光/影。
    """
    lum = rgb.mean(axis=2)
    bg_lum = float(field.mean(axis=2).mean())
    sat = rgb.max(axis=2) - rgb.min(axis=2)
    bg_sat = float((field.max(axis=2) - field.min(axis=2)).mean())

    m1 = _box_blur(lum, 2)
    m2 = _box_blur(lum * lum, 2)
    std = np.sqrt(np.clip(m2 - m1 * m1, 0.0, None))
    smooth = std < smooth_tol

    solid = alpha > 0.9
    core = _erode4(_erode4(solid))
    near = core
    for _ in range(int(ring)):
        near = _dilate4(near)
    outside = (~near) & smooth

    # ---- 光：比背景亮、且没有更"鲜艳" ----
    a_light = np.clip((lum - bg_lum) / max(1.0, 255.0 - bg_lum), 0.0, 1.0)
    m_light = outside & (lum > bg_lum + 6.0) & (sat <= max(bg_sat, 25.0) + 12.0) & (a_light > 0.07)

    # ---- 影：比背景暗、且是灰的（不是彩色能量） ----
    a_dark = np.clip(1.0 - lum / max(1.0, bg_lum), 0.0, 1.0) * 0.8
    m_dark = (outside & (lum < bg_lum - 6.0) & (sat <= 60.0) & (a_dark > 0.10)
              & (~m_light))

    alpha = alpha.copy()
    alpha[m_light] = a_light[m_light]
    alpha[m_dark] = a_dark[m_dark]

    rgb = rgb.copy()
    # 光 → 白（留 25% 原色相，免得金色光晕全变死白）
    mx = np.maximum(rgb.max(axis=2, keepdims=True), 1.0)
    tinted = rgb / mx * 255.0
    white = np.clip(0.75 * 255.0 + 0.25 * tinted, 0.0, 255.0)
    rgb[m_light] = white[m_light]
    rgb[m_dark] = 0.0

    return rgb, alpha, float(m_light.mean() * 100), float(m_dark.mean() * 100), (m_light | m_dark)


def _bg_field(rgb, bg0, lo, radius=96):
    """
    逐像素的背景色（"背景平滑场"）。

    为什么要这个：AI 生成的"纯色"背景实测有 10~13 的**缓慢渐变/暗角**
    （同一张图里不同位置差到 (184,221,227) vs (198,232,238)）。
    只用一个全局背景色去比，远离取样处的背景就会被判成"有一点 alpha"，
    在深色底上表现为一层**淡色幽灵**。

    做法：先挑出"肯定是背景"的像素（离全局色 < lo），只对它们做半径 96 的
    均值 -> 得到一张平滑的背景色图；角色占据的区域由周围背景插值出来。
    """
    d = np.abs(rgb - bg0).max(axis=2)
    w = (d < lo).astype(np.float32)
    den = _box_blur(w, radius)
    num = np.dstack([_box_blur(rgb[:, :, i] * w, radius) for i in range(3)])
    ok = den > 0.05
    field = np.empty_like(rgb)
    field[:] = bg0
    field[ok] = num[ok] / den[ok][:, None]
    return field


def _line_key(rgb, bg, alpha0, radius=4, res_tol=26.0):
    """
    "色线"（color-line）抠图：用**投影**求 alpha，而不是用颜色距离。

    为什么必须这么做
    ----------------
    AI 生成的图边缘是**软边**：紧贴轮廓的 1~2 像素其实是「背景色 + 角色色」的混合，
    但它们的 RGB 距离早就超过阈值了 —— 于是被赋成 alpha≈1，颜色却还是橙的，
    最后就变成一圈**彩色描边**（只改 alpha 救不了，因为 alpha 已经是 1）。

    正确的模型是每个像素都落在「背景色 B」到「角色色 F」这条线段上：
        C = a·F + (1-a)·B
    所以 a = <C-B, F-B> / |F-B|²（把 C-B 投影到 B→F 方向上）。

    F 用**局部**估计：对 alpha≈1 的像素做半径 4 的均值（不能全局用一个色，
    因为角色身上颜色很多）。若邻域里没有实心像素（纯背景），就退回距离法。

    最后做**残差校验**：C 若明显不在这条线上（大概是角色自己的亮部/暗部），
    就退回距离法的结果 —— 避免把正常像素算坏。
    """
    h, w = alpha0.shape
    core = (alpha0 > 0.995) & (alpha0 > 0)

    # 局部前景色（只在 core 上平均）
    wgt = core.astype(np.float32)
    num = rgb * wgt[:, :, None]
    n3 = np.dstack([_box_blur(num[:, :, i], radius) for i in range(3)])
    d1 = _box_blur(wgt, radius)
    ok = d1 > 0.05
    F = np.zeros_like(rgb)
    F[ok] = n3[ok] / d1[ok][:, None]

    v = F - bg                                   # 每个像素的 B→F 向量
    cb = rgb - bg
    num2 = np.sum(cb * v, axis=2)
    den2 = np.sum(v * v, axis=2)
    a1 = np.clip(num2 / np.maximum(den2, 1e-3), 0.0, 1.0)

    # 残差：C 是否真的落在 B—F 线段上
    recon = a1[:, :, None] * F + (1.0 - a1[:, :, None]) * bg
    res = np.abs(rgb - recon).max(axis=2)
    trust = ok & (den2 > 400.0) & (res < res_tol)

    return np.where(trust, a1, alpha0)


def _despill_green(rgb, tol=12.0):
    """
    去绿溢色（spill suppression）—— 影视绿幕抠像的必备一步。

    模型在绿幕上重绘时，会给角色边缘和光晕**带上一层绿色环境光**，
    表现为"轮廓一圈发绿"。这里把绿色通道压到不超过红/蓝通道的最大值：
    中性色和别的颜色不受影响，只有"绿得反常"的像素被拉回来。
    """
    r, g, b = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    mx_rb = np.maximum(r, b)
    spill = g - mx_rb
    m = spill > 4.0
    if not m.any():
        return rgb, 0
    out = rgb.copy()
    out[:, :, 1] = np.where(m, mx_rb + tol, g)
    return out, int(m.sum())


def _hue(rgb):
    """返回色相（度）与饱和度（0~255）。numpy 向量化，够用。"""
    mx = rgb.max(axis=2)
    mn = rgb.min(axis=2)
    d = mx - mn
    h = np.zeros_like(mx)
    m = d > 1e-3
    r, g, b = rgb[:, :, 0], rgb[:, :, 1], rgb[:, :, 2]
    idx = m & (mx == r)
    h[idx] = (60.0 * ((g[idx] - b[idx]) / d[idx]) + 360.0) % 360.0
    idx = m & (mx == g) & (mx != r)
    h[idx] = 60.0 * ((b[idx] - r[idx]) / d[idx]) + 120.0
    idx = m & (mx == b) & (mx != r) & (mx != g)
    h[idx] = 60.0 * ((r[idx] - g[idx]) / d[idx]) + 240.0
    return h, d


def _drop_bg_hue(rgb, field, alpha, tol=26.0, sat_min=34.0, ring=1):
    """
    剔掉"色相与背景一致"的像素 —— 这是对付**彩色描边/色晕/地面投影**最准的一条判据。

    背景色渗进边缘、AI 画的光晕、脚下的投影，本质都是"背景色被提亮或压暗"，
    所以它们的**色相和背景完全一样**，只是亮度/饱和度不同。
    而角色本体（银甲、蓝布、金饰、绿袍）色相都跟背景差得远。

    ⚠️ 只在**主体之外**（腐蚀 2px 再外扩 2px 以内仍算主体）生效，
       否则会把角色身上同色系的部分也削掉。
    """
    h_rgb, sat = _hue(rgb)
    h_bg, _ = _hue(field)
    diff = np.abs(h_rgb - h_bg)
    diff = np.minimum(diff, 360.0 - diff)          # 环形差

    # 🔴 **只处理轮廓外侧**。曾经试过"整张图都删同色相"，结果德鲁伊的绿叶/绿袍
    #    被当成背景，身上全是麻点（用户看到的"相近色彩点被扣除"就是它）。
    #    角色内部再像背景也不动 —— 宁可留一点点色晕，也不能啃角色。
    body = alpha > 0.85
    keep_zone = _erode4(_erode4(body))
    for _ in range(int(ring)):
        keep_zone = _dilate4(keep_zone)

    bad = (~keep_zone) & (sat > sat_min) & (diff < tol) & (alpha > 0.02)
    a = alpha.copy()
    a[bad] = 0.0
    return a, int(bad.sum())


def _fill_holes(alpha, gap=6):
    """
    把"被身体围住的透明小洞"补上。

    典型场景：浅色背景上的**白色部位**（小精灵的肚皮、白披风的褶皱）离背景太近，
    色键判成透明 —— 身上就出现一个个洞。但它们**走不到图像边界**（被身体包着），
    所以可以用连通性判出来并补实。

    在 4× 下采样图上做洪水填充（全尺寸要迭代上千轮，一张两分钟）。
    """
    step = max(1, int(round(max(alpha.shape) / 384.0)))
    transp = alpha < 0.35
    small = transp[::step, ::step]
    outside = reachable_from_border(small)
    holes = small & (~outside)
    if not holes.any():
        return alpha
    # 只补面积够大的洞（零星 1~2 格的是边缘噪声）
    fill_small = np.zeros_like(holes)
    rest = holes.copy()
    while rest.any():
        ys, xs = np.nonzero(rest)
        seed = np.zeros_like(rest)
        seed[ys[0], xs[0]] = True
        cur = seed
        while True:
            nxt = _dilate4(cur) & holes
            if np.array_equal(nxt, cur):
                break
            cur = nxt
        if cur.sum() * step * step >= 200:
            fill_small |= cur
        rest &= ~cur
    if not fill_small.any():
        return alpha
    fill = np.zeros(alpha.shape, dtype=bool)
    fill[::step, ::step] = fill_small
    for _ in range(step):
        fill = _dilate4(fill)
    fill &= (alpha < 0.35)
    a = alpha.copy()
    a[fill] = 1.0
    return a


def _gate_near_body(alpha, dist, gap=6, strong=85.0):
    """
    只保留"贴着主体"的像素，把外围那些淡淡的区域全部丢掉。

    被丢掉的是什么：光晕、地面投影、以及背景颗粒噪点 —— 这三样是"怎么抠都像有瑕疵"的
    全部来源。它们共同点是**离主体远、且与背景差别不大**。

    保留规则（满足任一即可）：
      · 在主体向外 gap 像素以内（正常轮廓、发丝、披风边缘都在这儿）；
      · 与背景色差 strong 以上（真正实心的角色部件，哪怕伸得远，比如长剑刃）。

    ⚠️ 代价：> gap 的**柔和光晕**会消失。这是刻意的取舍 —— 光晕留着必然带背景色，
       在深色底上就是一圈色晕；宁可去掉。
    """
    # "主体" = 又实又**明显不像背景**的像素。光晕/投影虽然 alpha 也是 1，
    # 但它们离背景近（dist 小），所以这里排除掉，只拿真正的角色本体当种子。
    solid = (alpha > 0.85) & (dist > strong)
    if solid.sum() < alpha.size * 0.005:      # 整张都偏浅（白袍角色）→ 不做门控
        return alpha
    grow = solid
    for _ in range(int(gap)):
        grow = _dilate4(grow)
    keep = grow | (dist > strong)
    return np.where(keep, alpha, 0.0)


def _erode4(mask):
    """4 邻域腐蚀一格。"""
    out = mask.copy()
    out[1:, :] &= mask[:-1, :]
    out[:-1, :] &= mask[1:, :]
    out[:, 1:] &= mask[:, :-1]
    out[:, :-1] &= mask[:, 1:]
    return out


def _drop_islands(alpha, reach=12, core_thresh=0.5):
    """
    除掉远离主体的孤立碎屑（背景噪声/压缩噪点被算出一点 alpha，远处就成了"橙点"）。

    做法：从主体（alpha 高、腐蚀过）出发膨胀 reach 圈，够不到的可见像素一律置 0。
    不用连通域标记 —— 碎屑可能几十个，逐个洪水填充太慢；膨胀 12 轮是 O(1) 的。
    """
    core = alpha > core_thresh
    core = _erode4(_erode4(core))
    if not core.any():
        return alpha
    grown = core
    for _ in range(int(reach)):
        grown = _dilate4(grown)
    killed = (alpha > 0.02) & (~grown)
    if killed.any():
        alpha = alpha.copy()
        alpha[killed] = 0.0
    return alpha


def _bleed_edge_color(rgb, alpha, outward=4, core_thresh=0.5, erode=2):
    """
    把**角色本体的颜色**向边缘外扩（颜色扩散 / color bleed）。

    为什么不用反预乘 `F = (C-(1-a)B)/a`
    ------------------------------------
    反预乘在数学上是对的，但 a 很小时会把噪声放大几十倍：实测某个边缘像素的
    蓝色通道因此从 42 被算成 117，结果描边从「橙色」变成「紫色」—— 换个颜色难看而已。
    颜色扩散没有除法，不放大噪声，而且边缘颜色**必然**等于紧邻的主体色，
    所以既不会有背景色描边，也不会有偏色。

    做法：把 alpha 高的区域往里腐蚀 2px 当"色源"，再一圈圈往外推 4 圈。
    更远的像素（已经完全透明）不用管。
    """
    core = alpha > core_thresh
    for _ in range(erode):
        core = _erode4(core)
    if not core.any():                     # 极细的图（整张都是边缘）→ 不扩散
        return rgb, np.zeros(alpha.shape, dtype=bool)

    filled = core.copy()
    out = rgb.copy()
    changed = np.zeros(alpha.shape, dtype=bool)
    for _ in range(outward):
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
        changed |= new
    return out, changed


def _box_blur(a, r):
    """浮点 box 滤波（积分图实现）。PIL 的 BoxBlur 不支持 F 模式，所以自己算。"""
    k = 2 * r + 1
    ap = np.pad(a, ((r, r), (r, r)), mode='edge')
    ii = ap.cumsum(0).cumsum(1)
    ii = np.pad(ii, ((1, 0), (1, 0)), mode='constant')
    s = ii[k:, k:] - ii[:-k, k:] - ii[k:, :-k] + ii[:-k, :-k]
    return s / float(k * k)


def _fringe_score(rgb, alpha, bg, radius=6):
    """
    边缘残留色评分：半透明边缘像素 vs "最近的实心角色色"的平均色差。

    越接近 0 说明边缘颜色越像角色本体；数值大 = 还带着背景色的描边。
    """
    opaque = alpha > 0.95
    if opaque.sum() == 0:
        return -1.0
    wgt = opaque.astype(np.float32)
    num = rgb * wgt[:, :, None]
    n3 = np.dstack([_box_blur(num[:, :, i], radius) for i in range(3)])
    d1 = _box_blur(wgt, radius)
    ok = d1 > 0.02
    fg_local = np.zeros_like(rgb)
    fg_local[ok] = n3[ok] / d1[ok][:, None]

    band = (alpha > 0.15) & (alpha < 0.85) & ok
    if not band.any():
        return -1.0
    return float(np.abs(rgb[band] - fg_local[band]).mean())


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('input')
    ap.add_argument('output', nargs='?')
    ap.add_argument('--lo', type=float, default=None)
    ap.add_argument('--hi', type=float, default=None)
    ap.add_argument('--feather', type=float, default=0.5)
    ap.add_argument('--decontam', type=float, default=1.0,
                    help='去色强度 0~1（0 = 不去色，边缘会留背景色描边）')
    ap.add_argument('--decontam-min-a', type=float, default=0.30,
                    help='反解除法的 alpha 下限（越小去色越狠、噪声放大越明显）')
    ap.add_argument('--alpha-cut', type=float, default=0.20,
                    help='低于它的 alpha 直接置 0')
    ap.add_argument('--key', choices=('line', 'dist'), default='line',
                    help='line = 色线投影（默认，边缘更干净）；dist = 仅按颜色距离')
    ap.add_argument('--no-glow', dest='glow', action='store_false',
                    help='不把光晕/投影分解成半透明的白/黑')
    ap.add_argument('--no-wm', dest='wm', action='store_false')
    ap.set_defaults(wm=True, glow=True)
    ap.add_argument('--check', action='store_true',
                    help='输出"洞"与"边缘残留色"检查')
    ap.add_argument('--only', default='', help='只处理文件名里含这个串的')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    out_dir = args.output or (args.input.rstrip('/\\') + '_matting')
    if not args.dry_run:
        os.makedirs(out_dir, exist_ok=True)

    files = sorted(glob.glob(os.path.join(args.input, '*.png')))
    if args.only:
        files = [f for f in files if args.only in os.path.basename(f)]
    if not files:
        print('没有找到 PNG')
        return 1

    print('%-20s %-18s %-10s %-7s %s' % ('文件', '背景色', 'lo/hi', '透明%', '检查'))
    for f in files:
        name = os.path.basename(f)
        try:
            img, info = build_matte(f, args.lo, args.hi, args.feather,
                                    args.decontam, args.decontam_min_a,
                                    args.alpha_cut, args.wm, args.check,
                                    args.key, args.glow)
        except Exception as exc:                        # 单张失败不连累整批
            print('  FAIL %-22s %s' % (name, exc))
            continue

        if not args.dry_run:
            img.save(os.path.join(out_dir, name))

        extra = ''
        if args.check:
            extra = '洞 %d 个 / 边缘残色 %.1f' % (info['holes'], info['fringe'])
        if info['wm_px']:
            extra += ' / 水印 %d px' % info['wm_px']
        print('%-20s (%3.0f,%3.0f,%3.0f)   %4.0f/%3.0f  %5.1f%%  %s' % (
            name[:19], info['bg'][0], info['bg'][1], info['bg'][2],
            info['lo'], info['hi'], info['transparent'] * 100.0, extra))

    if not args.dry_run:
        print('\n输出目录：' + out_dir)
    return 0


if __name__ == '__main__':
    sys.exit(main())
