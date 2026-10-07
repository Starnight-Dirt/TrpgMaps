#!/usr/bin/env python3
"""Measure the on-screen size of one grid cell in a DND Maps screenshot.

The whole point of the grid slider is "2.5 cm/cell means 2.5 cm on the glass",
so the only honest check is to look at the rendered pixels and measure the
distance between neighbouring grid lines. This script finds the vertical grid
lines by scoring every column against its own neighbourhood (a grid line is a
narrow bright vertical streak, so it stands out no matter what the basemap
photo underneath looks like), then reports the spacing statistics.

The PNG is decoded with the standard library only (zlib), so this script runs
on a bare Python 3 with no pip install. It understands the files the app and
GDI+ actually produce: 8-bit greyscale / RGB / RGBA / palette PNGs, non
interlaced. Anything else fails with a clear message instead of quietly giving
a wrong number.

Usage:
    python measure_grid.py <png> [--xmin 520] [--yfrac 0.25 0.85] [--expect 94.5]

Nothing here needs the app to run; it only reads an already captured PNG.
"""

import argparse
import statistics
import struct
import sys
import zlib


# --------------------------------------------------------------- PNG decoder

def _chunks(blob):
    """Yield (type, data) for every chunk after the 8-byte signature."""
    if blob[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG file")
    pos = 8
    end = len(blob)
    while pos + 8 <= end:
        (length,) = struct.unpack(">I", blob[pos:pos + 4])
        ctype = blob[pos + 4:pos + 8]
        data = blob[pos + 8:pos + 8 + length]
        yield ctype, data
        pos += 12 + length          # length + type + data + CRC


def _paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def load_rgb(path):
    """Decode a PNG and return (width, height, rows) as lists of RGB bytes.

    rows[y] is a bytearray holding width*3 bytes (R,G,B per pixel).
    """
    with open(path, "rb") as fh:
        blob = fh.read()

    width = height = None
    bit_depth = color_type = interlace = None
    palette = b""
    idat = []

    for ctype, data in _chunks(blob):
        if ctype == b"IHDR":
            width, height, bit_depth, color_type, _comp, _filt, interlace = \
                struct.unpack(">IIBBBBB", data)
        elif ctype == b"PLTE":
            palette = data
        elif ctype == b"IDAT":
            idat.append(data)
        elif ctype == b"IEND":
            break

    if width is None:
        raise ValueError("PNG has no IHDR chunk")
    if interlace:
        raise ValueError("interlaced (Adam7) PNGs are not supported")
    if bit_depth != 8:
        raise ValueError("only 8-bit PNGs are supported (got %d)" % bit_depth)

    # channels in the raw scanline, per PNG colour type
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}.get(color_type)
    if channels is None:
        raise ValueError("unsupported colour type %d" % color_type)

    raw = zlib.decompress(b"".join(idat))
    stride = width * channels
    out = []

    prev = bytearray(stride)
    pos = 0
    for _y in range(height):
        ftype = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        pos += stride

        if ftype == 1:                      # Sub
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ftype == 2:                    # Up
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ftype == 3:                    # Average
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ftype == 4:                    # Paeth
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                c = prev[i - channels] if i >= channels else 0
                line[i] = (line[i] + _paeth(a, prev[i], c)) & 0xFF
        elif ftype != 0:
            raise ValueError("unknown PNG filter type %d" % ftype)

        out.append(line)
        prev = line

    # expand to flat RGB rows so the rest of the script is colour-model agnostic
    rgb_rows = []
    for line in out:
        rgb = bytearray(width * 3)
        if color_type == 2:
            rgb[:] = line
        elif color_type == 6:
            for x in range(width):
                rgb[x * 3:x * 3 + 3] = line[x * 4:x * 4 + 3]
        elif color_type == 4:
            for x in range(width):
                g = line[x * 2]
                rgb[x * 3] = rgb[x * 3 + 1] = rgb[x * 3 + 2] = g
        elif color_type == 0:
            for x in range(width):
                g = line[x]
                rgb[x * 3] = rgb[x * 3 + 1] = rgb[x * 3 + 2] = g
        else:                               # color_type == 3, palette
            for x in range(width):
                idx = line[x]
                base = idx * 3
                rgb[x * 3:x * 3 + 3] = palette[base:base + 3]
        rgb_rows.append(rgb)

    return width, height, rgb_rows


# ------------------------------------------------------------------ analysis

def column_scores(rows, xmin, y0, y1, width):
    """Mean brightness of each column minus the average of its +-4 neighbours."""
    y1 = min(y1, len(rows))
    luma = []
    for x in range(xmin, width):
        total = 0
        n = 0
        for y in range(y0, y1, 2):          # every other row is plenty and 2x faster
            off = x * 3
            r = rows[y][off]
            g = rows[y][off + 1]
            b = rows[y][off + 2]
            total += (r * 299 + g * 587 + b * 114) // 1000
            n += 1
        luma.append(total / n if n else 0.0)

    scores = [0.0] * len(luma)
    for i in range(len(luma)):
        a = luma[i - 4] if i - 4 >= 0 else luma[i]
        b = luma[i + 4] if i + 4 < len(luma) else luma[i]
        scores[i] = luma[i] - (a + b) / 2.0
    return scores


def find_lines(scores, xmin, threshold):
    """Cluster adjacent above-threshold columns and return their centres."""
    peaks = []
    i = 0
    while i < len(scores):
        if scores[i] > threshold:
            j = i
            best = i
            while j < len(scores) and scores[j] > threshold:
                if scores[j] > scores[best]:
                    best = j
                j += 1
            peaks.append(xmin + best)
            i = j
        else:
            i += 1
    return peaks


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("png")
    ap.add_argument("--xmin", type=int, default=0,
                    help="ignore everything left of this column (sidebar + panel)")
    ap.add_argument("--yfrac", type=float, nargs=2, default=(0.25, 0.85),
                    help="vertical band to sample, as fractions of the height")
    ap.add_argument("--expect", type=float, default=None,
                    help="expected cell size in pixels (cm/cell * 37.8)")
    ap.add_argument("--threshold", type=float, default=6.0,
                    help="peak score threshold; lower it for very dark maps")
    args = ap.parse_args()

    try:
        w, h, rows = load_rgb(args.png)
    except (ValueError, zlib.error) as exc:
        sys.exit("cannot read %s: %s" % (args.png, exc))

    y0, y1 = int(h * args.yfrac[0]), int(h * args.yfrac[1])

    print("file        : %s" % args.png)
    print("size        : %dx%d   sampling x>=%d, y=%d..%d" % (w, h, args.xmin, y0, y1))

    scores = column_scores(rows, args.xmin, y0, y1, w)
    lines = find_lines(scores, args.xmin, args.threshold)

    if len(lines) < 3:
        print("vertical lines found: %d  -> cannot measure "
              "(try a lower --threshold)" % len(lines))
        return 2

    gaps = [b - a for a, b in zip(lines, lines[1:])]
    med = statistics.median(gaps)
    print("lines        : %d  first=%d  last=%d" % (len(lines), lines[0], lines[-1]))
    print("first gaps   : %s" % gaps[:12])
    print("median gap   : %.1f px" % med)

    if args.expect:
        err = (med - args.expect) / args.expect * 100.0
        print("expected     : %.1f px   deviation = %+.1f%%" % (args.expect, err))
        if abs(err) <= 2.0:
            print("verdict      : MATCH - the slider value really is the on-screen size")
        else:
            print("verdict      : MISMATCH")

    return 0


if __name__ == "__main__":
    sys.exit(main())
