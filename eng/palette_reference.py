"""Independent palette reference renderer for P2-2 (no OpenD2 code; Python standard library only).

Decodes a DC6 background itself, stitches its frames row-major into an 800x600 buffer (the same layout as
LegacyScreenRenderer), optionally remaps indices through a raw PL2 light table (offset 1024 + level * 256), converts
with a raw pal.dat (BGR) and prints the SHA-256 of the RGB bytes. Only hashes are printed; it never writes game data.

Input is a folder of original files laid out by logical path, e.g. the raw/ folder that
`OpenD2.AssetAudit --build-cache <data> --cache <tmp> --include "data\\global\\palette" --png none` writes outside the
repository (run it again with --include "data\\global\\ui\\frontend"; several --raw folders may be given).

  python eng/palette_reference.py --raw /tmp/c1/1.11b/raw --raw /tmp/c2/1.11b/raw [--palette all|NAME ...] [--light N]
"""
import argparse
import hashlib
from pathlib import Path
import struct
import sys

PALETTES = ("act1", "act2", "act3", "act4", "act5", "endgame", "endgame2", "fechar", "loading", "menu0", "menu1",
            "menu2", "menu3", "menu4", "sky", "static", "trademark", "units")
WIDTH, HEIGHT = 800, 600


def decode_dc6(dc6):
    """[(width, height, indices, opaque)] per frame; transparent runs leave opaque = 0."""
    version, _flags, _encoding, _terminator, directions, frames = struct.unpack_from("<6I", dc6, 0)
    if version != 6:
        raise ValueError("not a DC6 file")
    pointers = struct.unpack_from(f"<{directions * frames}I", dc6, 24)
    result = []
    for pointer in pointers:
        flip, width, height, _ox, _oy, _unknown, _next, length = struct.unpack_from("<I4iIII", dc6, pointer)
        data = dc6[pointer + 32:pointer + 32 + length]
        indices, opaque = bytearray(width * height), bytearray(width * height)
        x, y, p = 0, (0 if flip else height - 1), 0
        while p < len(data) and 0 <= y < height:
            code = data[p]
            p += 1
            if code == 0x80:  # end of scanline
                x, y = 0, y + (1 if flip else -1)
            elif code & 0x80:  # transparent run
                x += code & 0x7F
            else:  # opaque run
                for k in range(code):
                    if x < width:
                        indices[y * width + x], opaque[y * width + x] = data[p + k], 1
                    x += 1
                p += code
        result.append((width, height, indices, opaque))
    return result


def render(dc6, pal, pl2=None, level=None):
    if len(pal) != 768:
        raise ValueError("pal.dat must be 768 bytes")
    buffer, covered = bytearray(WIDTH * HEIGHT), bytearray(WIDTH * HEIGHT)
    x = y = row = 0
    for width, height, indices, opaque in decode_dc6(dc6):
        if x >= WIDTH:
            x, y, row = 0, y + row, 0
        for r in range(height):
            for c in range(width):
                sx, sy = x + c, y + r
                if sx < WIDTH and sy < HEIGHT and opaque[r * width + c]:
                    buffer[sy * WIDTH + sx], covered[sy * WIDTH + sx] = indices[r * width + c], 1
        x, row = x + width, max(row, height)
    rgb = bytearray(WIDTH * HEIGHT * 3)
    for i in range(WIDTH * HEIGHT):
        if covered[i]:
            index = buffer[i] if pl2 is None else pl2[1024 + level * 256 + buffer[i]]
            b, g, r = pal[index * 3:index * 3 + 3]
            rgb[3 * i:3 * i + 3] = bytes((r, g, b))
    return hashlib.sha256(rgb).hexdigest()


def find(roots, logical):
    for root in roots:
        path = Path(root, *logical.lower().split("\\"))
        if path.is_file():
            return path.read_bytes()
    raise FileNotFoundError(f"{logical} not under any --raw folder")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--raw", action="append", required=True, help="folder of original files by logical path")
    parser.add_argument("--dc6", default="data\\global\\ui\\frontend\\gameselectscreenexp.dc6")
    parser.add_argument("--palette", nargs="+", default=["all"])
    parser.add_argument("--light", type=int, choices=range(32), metavar="0-31")
    parser.add_argument("--pl2-file", type=Path, help="pal.pl2 to use for --light (the cache's raw/ listing may omit it)")
    args = parser.parse_args(argv)
    names = PALETTES if args.palette == ["all"] else args.palette
    dc6 = find(args.raw, args.dc6)
    for name in names:
        pal = find(args.raw, f"data\\global\\palette\\{name}\\pal.dat")
        pl2 = None
        if args.light is not None:
            pl2 = args.pl2_file.read_bytes() if args.pl2_file else find(args.raw, f"data\\global\\palette\\{name}\\pal.pl2")
        print(f"{name}\t{'-' if args.light is None else args.light}\t{render(dc6, pal, pl2, args.light)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
