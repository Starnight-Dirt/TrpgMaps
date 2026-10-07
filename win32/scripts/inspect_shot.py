#!/usr/bin/env python3
"""
Inspect the UI screenshots produced by scripts/ui_shot.ps1.

Eyeballing a PNG is a bad way to check pixel-level layout (is the gap 2px or
6px? is that a scrollbar or a shadow?), so this reads the actual pixels.

Usage:
  python inspect_shot.py px    shot.png <x> <y>
  python inspect_shot.py row   shot.png <y> [step]
  python inspect_shot.py col   shot.png <x> [step]
  python inspect_shot.py crop  shot.png <x> <y> <w> <h> <zoom> <out.png>

'row' / 'col' print colour transitions along a line (colour runs), which makes
edges measurable to the pixel. 'step' skips every N-th pixel when scanning
(default 1 = exact).
"""
import sys

from PIL import Image


def fmt(c):
    if c[3] != 255:
        return f"#{c[0]:02X}{c[1]:02X}{c[2]:02X}a{c[3]}"
    return f"#{c[0]:02X}{c[1]:02X}{c[2]:02X}"


def rgba(p):
    return p if len(p) == 4 else (p[0], p[1], p[2], 255)


def scan(pixels, coords, label, origin, step):
    """coords: list of (u, x, y). Walk it, print colour runs."""
    print(f"{label} 扫描 (起始 {origin}, step={step})")
    prev = None
    run_start = None
    runs = 0
    for i in range(0, len(coords), step):
        u, x, y = coords[i]
        c = rgba(pixels[x, y])
        if c != prev:
            if prev is not None:
                print(f"  {run_start:>5} .. {u - step:>5}  {fmt(prev)}")
                runs += 1
            prev = c
            run_start = u
    if prev is not None:
        u, _, _ = coords[-1]
        print(f"  {run_start:>5} .. {u:>5}  {fmt(prev)}")
        runs += 1
    print(f"  共 {runs} 段")


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1

    cmd = sys.argv[1].lower()
    im = Image.open(sys.argv[2]).convert("RGBA")
    print(f"图片 {sys.argv[2]}  {im.width}x{im.height}")

    px = im.load()

    if cmd == "px":
        x, y = int(sys.argv[3]), int(sys.argv[4])
        print(f"({x},{y}) = {fmt(rgba(px[x, y]))}")
        return 0

    if cmd in ("row", "col"):
        idx = int(sys.argv[3])
        step = int(sys.argv[4]) if len(sys.argv) > 4 else 1
        if cmd == "row":
            coords = [(x, x, idx) for x in range(im.width)]
            scan(px, coords, f"第 {idx} 行", f"y={idx}", step)
        else:
            coords = [(y, idx, y) for y in range(im.height)]
            scan(px, coords, f"第 {idx} 列", f"x={idx}", step)
        return 0

    if cmd == "crop":
        x, y, w, h, zoom = (int(sys.argv[i]) for i in range(3, 8))
        out = sys.argv[8]
        box = im.crop((x, y, x + w, y + h))
        box = box.resize((w * zoom, h * zoom), Image.NEAREST)
        box.save(out)
        print(f"已写出 {out}  ({w * zoom}x{h * zoom})")
        return 0

    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main())
