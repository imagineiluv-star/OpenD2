"""eng/palette_reference.py on synthetic DC6/pal.dat/pal.pl2 bytes (never game data)."""
import contextlib
import hashlib
import io
from pathlib import Path
import struct
import tempfile
import unittest

import palette_reference as ref


def dc6(frames):
    """A 1-direction DC6 with the given [(width, height, rows)] frames; rows are lists of (skip, pixels) runs, bottom-up."""
    blobs = []
    for width, height, rows in frames:
        data = bytearray()
        for runs in rows:
            for skip, pixels in runs:
                if skip:
                    data.append(0x80 | skip)
                if pixels:
                    data.append(len(pixels))
                    data += bytes(pixels)
            data.append(0x80)
        blobs.append(struct.pack("<I4iIII", 0, width, height, 0, 0, 0, 0, len(data)) + bytes(data) + b"\xee\xee\xee")
    table = 24 + 4 * len(frames)
    pointers, cursor = [], table
    for blob in blobs:
        pointers.append(cursor)
        cursor += len(blob)
    head = struct.pack("<6I", 6, 1, 0, 0xEEEEEEEE, 1, len(frames))
    return head + struct.pack(f"<{len(frames)}I", *pointers) + b"".join(blobs)


PAL = bytes(v for i in range(256) for v in (i, (i * 3) % 256, 255 - i))  # BGR


def expected_rgb(pixels):
    """800x600 RGB from {(x, y): index} computed independently of the module."""
    rgb = bytearray(800 * 600 * 3)
    for (x, y), index in pixels.items():
        b, g, r = PAL[index * 3:index * 3 + 3]
        rgb[(y * 800 + x) * 3:(y * 800 + x) * 3 + 3] = bytes((r, g, b))
    return hashlib.sha256(rgb).hexdigest()


class PaletteReferenceTests(unittest.TestCase):
    def test_decodes_runs_bottom_up_and_maps_bgr(self):
        # 3x2 frame: bottom row "skip 1, [5, 6]", top row "[7]".
        data = dc6([(3, 2, [[(1, [5, 6])], [(0, [7])]])])
        frame = ref.decode_dc6(data)[0]
        self.assertEqual((3, 2), frame[:2])
        self.assertEqual(bytes([7, 0, 0, 0, 5, 6]), bytes(frame[2]))
        self.assertEqual(bytes([1, 0, 0, 0, 1, 1]), bytes(frame[3]))
        self.assertEqual(expected_rgb({(0, 0): 7, (1, 1): 5, (2, 1): 6}), ref.render(data, PAL))

    def test_frames_are_stitched_row_major_and_wrap_at_800(self):
        frames = [(400, 1, [[(0, [i + 1] * 1)]]) for i in range(3)]  # three 400-wide frames, one pixel each
        pixels = {(0, 0): 1, (400, 0): 2, (0, 1): 3}
        self.assertEqual(expected_rgb(pixels), ref.render(dc6(frames), PAL))

    def test_light_uses_the_raw_pl2_table(self):
        pl2 = bytearray(1024 + 32 * 256)
        pl2[1024 + 12 * 256 + 5] = 9
        data = dc6([(1, 1, [[(0, [5])]])])
        self.assertEqual(expected_rgb({(0, 0): 9}), ref.render(data, PAL, bytes(pl2), 12))

    def test_rejects_bad_inputs(self):
        with self.assertRaises(ValueError):
            ref.render(dc6([(1, 1, [[(0, [5])]])]), PAL[:767])
        with self.assertRaises(ValueError):
            ref.decode_dc6(struct.pack("<6I", 5, 1, 0, 0, 1, 1) + b"\0" * 8)

    def test_cli_reads_a_raw_folder_and_prints_hashes_only(self):
        with tempfile.TemporaryDirectory() as temporary:
            raw = Path(temporary)
            (raw / "data/global/palette/units").mkdir(parents=True)
            (raw / "data/global/palette/units/pal.dat").write_bytes(PAL)
            (raw / "data/global/ui/frontend").mkdir(parents=True)
            data = dc6([(1, 1, [[(0, [5])]])])
            (raw / "data/global/ui/frontend/gameselectscreenexp.dc6").write_bytes(data)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(0, ref.main(["--raw", str(raw), "--palette", "units"]))
            self.assertEqual(f"units\t-\t{expected_rgb({(0, 0): 5})}\n", out.getvalue())
            with self.assertRaises(FileNotFoundError):
                ref.main(["--raw", str(raw), "--palette", "act1"])


if __name__ == "__main__":
    unittest.main()
