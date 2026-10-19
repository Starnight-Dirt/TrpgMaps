#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TrpgMaps —— 把原声带母带（FLAC）压成游戏里用的 OGG，并统计体积。

为什么要有这个脚本
------------------
音乐是 48 kHz / 立体声 / 16 bit 的 FLAC，8 首约 144 MB。FLAC 已经是无损压缩，
再"无损压一遍"最多省几个百分点（换成 WavPack 能省 5% 左右，但播放器不认），
所以真正的体积来自**有损但听得出来的人不多**的 Vorbis：
实测这套素材 `-q:a 2`（约 90 kbps）时 >16 kHz 的能量只比原文件低 1~2 dB，
体积却只有原来的 1/9 —— 这类 BGM 在笔记本喇叭 / 平板上听不出差别。

本脚本只做一件事：**母带 → OGG**。母带永远不删、也不进编译版，
它由 make_release.ps1 打包进 open\\audio-flac\\（见那里的注释）。

目录约定
--------
    D:\\DND_Maps\\audio-master\\战斗\\xxx.flac     ← 母带（不进任何发布包）
    D:\\DND_Maps\\win7版本\\audio\\战斗\\xxx.ogg    ← 游戏里用的（进源码包与安装包）

用法
----
    python scripts/encode_audio.py                # 增量：母带比 ogg 新才转
    python scripts/encode_audio.py --force        # 全部重转
    python scripts/encode_audio.py -q 3           # 换个音质档（0-10，越大越好）
    python scripts/encode_audio.py --ffmpeg "C:\\path\\ffmpeg.exe"

ffmpeg 从哪来
-------------
优先 `--ffmpeg`，其次环境变量 TRPG_FFMPEG，最后看下面 FFMPEG_CANDIDATES。
本机这份是从 imageio-ffmpeg 的 wheel 里解出来的（gyan.dev 的 essentials build，
带 libvorbis），放在用户目录而不是工程里 —— 它是工具，不是源码。
"""

import argparse
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # win7版本
WORKSPACE = os.path.dirname(ROOT)
MASTER_DIR = os.path.join(WORKSPACE, 'audio-master')
OUT_DIR = os.path.join(ROOT, 'audio')

FFMPEG_CANDIDATES = [
    os.path.join(os.environ.get('LOCALAPPDATA', ''), 'TrpgAudioTools',
                 'ffmpeg-win-x86_64-v7.1.exe'),
    os.path.join(os.environ.get('LOCALAPPDATA', ''), 'TrpgAudioTools', 'ffmpeg.exe'),
]


def find_ffmpeg(explicit):
    if explicit:
        return explicit
    env = os.environ.get('TRPG_FFMPEG', '')
    if env:
        return env
    for cand in FFMPEG_CANDIDATES:
        if cand and os.path.isfile(cand):
            return cand
    return 'ffmpeg'          # 指望它在 PATH 里


def human(n):
    return '%.1f MB' % (n / 1048576.0) if n >= 1048576 else '%.0f KB' % (n / 1024.0)


def main():
    parser = argparse.ArgumentParser(description='把 audio-master 里的 FLAC 转成 audio/ 里的 OGG')
    parser.add_argument('-q', '--quality', type=float, default=2.0,
                        help='libvorbis 音质档（默认 2，约 90kbps；3 约 110kbps）')
    parser.add_argument('--force', action='store_true', help='全部重转（默认只转母带更新过的）')
    parser.add_argument('--ffmpeg', default='', help='ffmpeg.exe 的完整路径')
    args = parser.parse_args()

    ffmpeg = find_ffmpeg(args.ffmpeg)
    if not os.path.isdir(MASTER_DIR):
        print('找不到母带目录：' + MASTER_DIR)
        return 2

    jobs = []
    for base, _dirs, names in os.walk(MASTER_DIR):
        for name in sorted(names):
            if not name.lower().endswith('.flac'):
                continue
            src = os.path.join(base, name)
            rel = os.path.relpath(src, MASTER_DIR)
            dst = os.path.join(OUT_DIR, os.path.splitext(rel)[0] + '.ogg')
            jobs.append((src, dst))

    if not jobs:
        print('母带目录里没有 .flac：' + MASTER_DIR)
        return 2

    total_in = 0
    total_out = 0
    done = 0

    for src, dst in jobs:
        in_size = os.path.getsize(src)
        total_in += in_size

        if (not args.force and os.path.isfile(dst) and
                os.path.getmtime(dst) >= os.path.getmtime(src)):
            total_out += os.path.getsize(dst)
            print('  skip  %-32s %s' % (os.path.relpath(dst, ROOT), human(os.path.getsize(dst))))
            continue

        os.makedirs(os.path.dirname(dst), exist_ok=True)
        cmd = [ffmpeg, '-hide_banner', '-loglevel', 'error', '-y',
               '-i', src,
               '-vn', '-map_metadata', '-1',          # 封面/标签不要（能省一点，也避免 m4a 那种容器冲突）
               '-c:a', 'libvorbis', '-q:a', str(args.quality),
               dst]
        proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if proc.returncode != 0:
            print('  FAIL  ' + src)
            print((proc.stdout or b'').decode('utf-8', 'replace').strip()[:500])
            return 1

        out_size = os.path.getsize(dst)
        total_out += out_size
        done += 1
        print('  enc   %-32s %s -> %s' % (os.path.relpath(dst, ROOT), human(in_size), human(out_size)))

    print('')
    print('转码 %d 首，跳过 %d 首' % (done, len(jobs) - done))
    print('合计：%s -> %s（%.1f%%）' % (human(total_in), human(total_out),
                                       100.0 * total_out / max(1, total_in)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
