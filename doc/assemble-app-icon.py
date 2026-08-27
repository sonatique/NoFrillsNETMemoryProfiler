"""Assembles src/.../Assets/app.ico from the icon set's PNGs.

The delivered windows/app.ico holds a single 16x16 image, so Windows scales that one
picture everywhere: the desktop shortcut, the alt-tab list and the Explorer large-icon
view all end up as a blurred 16 pixel badge. The png-set beside it is correct, with the
simplified badge up to 48 and the detailed mole from 64, so the icon is built from those
instead.

Run it after dropping in a new icon set:

    python doc/assemble-app-icon.py <folder holding png-set>

An .ico is a small header followed by whole image files, and Windows has read PNG
entries since Vista, so the PNGs go in untouched.
"""

import os
import struct
import sys

# Below 48 the badge, above it the mole: the sizes the shell actually asks for.
SIZES = [16, 24, 32, 48, 64, 128, 256]

def assemble(png_folder, output):
    images = []

    for size in SIZES:
        path = os.path.join(png_folder, 'icon-%d.png' % size)

        if not os.path.exists(path):
            raise SystemExit('missing %s' % path)

        with open(path, 'rb') as handle:
            images.append((size, handle.read()))

    header = struct.pack('<HHH', 0, 1, len(images))
    offset = len(header) + 16 * len(images)

    entries = []
    for size, data in images:
        # A width or height byte of zero means 256.
        entries.append(struct.pack(
            '<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        offset += len(data)

    with open(output, 'wb') as handle:
        handle.write(header)
        for entry in entries:
            handle.write(entry)
        for _, data in images:
            handle.write(data)

    print('wrote %s: %d entries, %s' % (output, len(images), [size for size, _ in images]))

if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('usage: assemble-app-icon.py <folder holding png-set>')

    here = os.path.dirname(os.path.abspath(__file__))

    assemble(
        os.path.join(sys.argv[1], 'png-set'),
        os.path.join(here, '..', 'src', 'NoFrills.MemoryProfiler.App', 'Assets', 'app.ico'))
