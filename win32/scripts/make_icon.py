"""Turn the master PNG into the Windows icon (.ico) used by both editions.

Input  : the single *.png sitting in the repository root (256x256 RGBA master art)
Output : <repo>\\win7版本\\Resources\\appicon.ico
             multi-size, classic BMP entries -> understood by Windows 7, which
             does NOT read PNG-compressed icon entries reliably
         <repo>\\maui版本\\Resources\\AppIcon\\appicon.png
             a straight copy; MAUI's own icon pipeline turns it into the .ico

Requires Pillow (the system Python 3.11 has it; the isolated 3.13 runtime does not).
This file is a dev helper only - it is not part of either build.

Run:  python scripts\\make_icon.py
"""

import glob
import os
import shutil
import struct
import sys
from io import BytesIO

SIZES = [16, 24, 32, 48, 64, 128, 256]

HERE = os.path.dirname(os.path.abspath(__file__))
WIN7 = os.path.dirname(HERE)                 # ...\win7版本
REPO = os.path.dirname(WIN7)                 # ...\DND_Maps


def find_master():
    # Referenced by glob on purpose: a literal Chinese file name inside a script
    # is a recurring source of encoding pain on this machine.
    files = sorted(glob.glob(os.path.join(REPO, "*.png")))
    if len(files) != 1:
        raise SystemExit("expected exactly one *.png in %s, found %d" % (REPO, len(files)))
    return files[0]


def dib_entry(img):
    """One ICO entry in classic BMP (DIB) form, as Windows has always understood it.

    Layout: BITMAPINFOHEADER with biHeight = 2*height (XOR image + AND mask),
    then the XOR pixels bottom-up as BGRA, then the 1bpp AND mask (all zeroes -
    transparency comes from the alpha channel).
    """
    w, h = img.size
    pixels = img.load()
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = pixels[x, y]
            xor += bytes((b, g, r, a))
    mask_row = ((w + 31) // 32) * 4          # 1bpp rows are padded to 4 bytes
    and_mask = bytes(mask_row * h)
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(and_mask),
                         0, 0, 0, 0)
    return bytes(header) + bytes(xor) + and_mask


def write_ico(master, target, sizes):
    try:
        from PIL import Image
    except ImportError:
        raise SystemExit("Pillow is required: use the system Python 3.11 to run this")

    source = Image.open(master).convert("RGBA")
    if source.width != source.height:
        raise SystemExit("master art must be square, got %dx%d" % (source.size))

    entries = []
    for size in sizes:
        if size > source.width:
            continue
        frame = source.resize((size, size), Image.LANCZOS)
        if size >= 256:
            # Windows Vista and later read PNG-compressed entries, and only there
            # it saves real space (a 256x256 DIB alone is 262 KB, more than the
            # whole executable). Smaller sizes stay classic BMP for compatibility.
            buf = BytesIO()
            frame.save(buf, format="PNG")
            entries.append((size, size, 32, buf.getvalue()))
        else:
            entries.append((size, size, 32, dib_entry(frame)))

    out = BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for w, h, bits, blob in entries:
        out.write(struct.pack("<BBBBHHII", w & 0xFF, h & 0xFF, 0, 0, 1, bits, len(blob), offset))
        offset += len(blob)
    for _, _, _, blob in entries:
        out.write(blob)

    os.makedirs(os.path.dirname(target), exist_ok=True)
    data = out.getvalue()
    open(target, "wb").write(data)
    return entries, len(data)


def describe(path):
    data = open(path, "rb").read()
    reserved, kind, count = struct.unpack("<HHH", data[:6])
    if reserved != 0 or kind != 1:
        raise SystemExit("not an ICO: %s" % path)
    out = []
    for i in range(count):
        w, h, colors, pad, planes, bits, size, offset = struct.unpack("<BBBBHHII", data[6 + i * 16:22 + i * 16])
        out.append("%dx%d/%dbpp %dB" % (w or 256, h or 256, bits, size))
    return count, out, len(data)


def main():
    master = find_master()
    print("master :", master)

    ico = os.path.join(WIN7, "Resources", "appicon.ico")
    entries, total = write_ico(master, ico, SIZES)
    print("ico    :", ico)
    print("         %d entries, %d bytes total" % (len(entries), total))
    for w, h, bits, blob in entries:
        print("           %dx%d/%dbpp %dB" % (w, h, bits, len(blob)))

    count, described, size = describe(ico)
    if count != len(entries):
        raise SystemExit("index says %d entries, wrote %d" % (count, len(entries)))
    print("         read back: %d entries, %d bytes" % (count, size))

    png = os.path.join(REPO, "maui版本", "Resources", "AppIcon", "appicon.png")
    os.makedirs(os.path.dirname(png), exist_ok=True)
    shutil.copyfile(master, png)
    print("png    :", png, "(%d bytes)" % os.path.getsize(png))


if __name__ == "__main__":
    sys.exit(main())
