#!/usr/bin/env python3
"""The Welcome and Finish image of the Windows setup and its uninstaller (D104), drawn for each display scale.

Usage: wizard-image.py <folder>
Writes dialshift-wizard-<scale>.bmp for the scales in SCALES (percent) into <folder>: 24-bit, uncompressed Windows
bitmaps of 164 x 314 pixels at 100 % (the Modern UI's Welcome/Finish image, 109 x 193 dialog units) times the scale.

The picture is DialShift's own app icon (DialShift.App/Assets/icon-512.png, its 512 px source): the green clock mark
on the icon's dark background, redrawn here from the icon's geometry so that every size is drawn, not resampled.
The geometry is computed in floating point and rounded once per size to whole sample units (1/8 pixel); every value
rounded lies at least 0.02 units from a rounding boundary, far more than floating-point error can move it. From there
on everything is integer arithmetic (each pixel is covered by 4 x 4 samples), so the bytes are the same on every host
and Python version: the setup built on the Mac and in CI stays byte-identical (D98).
Every drawing has the 100 % image's proportions; the image control's own proportions vary a little with the display
scale, so fitting a drawing to the control stretches it slightly out of round at some scales (D104, a known limitation).
Called by scripts/build-win-setup.sh; standard library only.
"""
import os
import struct
import sys

SCALES = (100, 125, 150, 175, 200, 250, 300)
WIDTH, HEIGHT = 164, 314            # pixels at 100 %
BACKGROUND = (23, 37, 35)           # the icon's background, #172523
MARK = (194, 242, 120)              # the icon's clock mark, #C2F278

# The mark at 100 %: centre and outer radius of the ring, in pixels.
CENTRE_X, CENTRE_Y, RADIUS = 82, 118, 58
# The icon's geometry, in icon pixels around its centre (256, 256): the ring's outer and inner radii, and the hands as
# half-open rectangles (x0, y0, x1, y1): the minute hand up from the centre to the ring, the hour hand to the left.
ICON_OUTER, ICON_INNER = 171.5, 148.5
ICON_HANDS = ((-11, -149, 12, 0), (-109, -11, 0, 12))

SAMPLES = 4                          # per axis, per pixel
UNIT = 2 * SAMPLES                   # sample centres sit at odd multiples of 1 / UNIT pixel


def draw(scale):
    width, height = WIDTH * scale // 100, HEIGHT * scale // 100
    factor = RADIUS * scale / 100 / ICON_OUTER   # icon pixels -> output pixels

    def units(value):                             # output pixels -> integer sample units, rounded once
        return int(round(value * UNIT))

    cx, cy = units(CENTRE_X * scale / 100), units(CENTRE_Y * scale / 100)
    outer2 = units(ICON_OUTER * factor) ** 2
    inner2 = units(ICON_INNER * factor) ** 2
    hands = [tuple(units(v * factor) for v in hand) for hand in ICON_HANDS]
    reach = units(ICON_OUTER * factor) + UNIT     # beyond this box around the centre every sample is background

    def covered(x, y):
        dx, dy = x - cx, y - cy
        if inner2 <= dx * dx + dy * dy <= outer2:
            return True
        return any(x0 <= dx < x1 and y0 <= dy < y1 for x0, y0, x1, y1 in hands)

    total = SAMPLES * SAMPLES
    rows = []
    for py in range(height):
        row = bytearray()
        for px in range(width):
            hits = 0
            if abs(px * UNIT - cx) <= reach and abs(py * UNIT - cy) <= reach:
                for sy in range(SAMPLES):
                    for sx in range(SAMPLES):
                        if covered(px * UNIT + 2 * sx + 1, py * UNIT + 2 * sy + 1):
                            hits += 1
            # Blue, green, red: each channel mixed by coverage, rounded half up in integers.
            for channel in (2, 1, 0):
                mixed = BACKGROUND[channel] * (total - hits) + MARK[channel] * hits
                row.append((2 * mixed + total) // (2 * total))
        row.extend(b"\0" * (-len(row) % 4))
        rows.append(bytes(row))
    pixels = b"".join(reversed(rows))    # bottom-up
    header = struct.pack("<2sIHHI", b"BM", 54 + len(pixels), 0, 0, 54)
    info = struct.pack("<IiiHHIIiiII", 40, width, height, 1, 24, 0, len(pixels), 2835, 2835, 0, 0)
    return header + info + pixels


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: wizard-image.py <folder>")
    folder = sys.argv[1]
    for scale in SCALES:
        with open(os.path.join(folder, "dialshift-wizard-%d.bmp" % scale), "wb") as f:
            f.write(draw(scale))


if __name__ == "__main__":
    main()
