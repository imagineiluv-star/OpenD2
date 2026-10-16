"""Negative tests for the release game-data guard. Every payload here is synthetic fake bytes, never game data."""
import hashlib
import io
import os
from pathlib import Path
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest import mock
import zipfile

import data_guard
import release

FAKE = b"synthetic fixture bytes, not game data"


def fake_pck(entries, version=3, flags=2):
    """A minimal Godot PCK (format 2 or 3+, relative file base) holding the given {path: bytes}."""
    blobs, offsets, cursor = b"", [], 0
    for data in entries.values():
        offsets.append(cursor)
        blobs += data
        cursor += len(data)
    header_size = 4 + 5 * 4 + 8 + (8 if version >= 3 else 0) + 16 * 4
    table = struct.pack("<I", len(entries))
    for (name, data), offset in zip(entries.items(), offsets):
        raw = name.encode()
        raw += b"\0" * (-len(raw) % 4)
        table += struct.pack("<I", len(raw)) + raw + struct.pack("<QQ", offset, len(data)) + b"\0" * 16 + b"\0" * 4
    if version >= 3:
        file_base = header_size
        head = b"GDPC" + struct.pack("<5I", version, 4, 7, 2, flags) + struct.pack("<QQ", file_base, file_base + len(blobs))
        return head + b"\0" * 64 + blobs + table
    file_base = header_size + len(table)
    head = b"GDPC" + struct.pack("<5I", version, 4, 0, 0, flags) + struct.pack("<Q", file_base)
    return head + b"\0" * 64 + table + blobs


class DataGuardTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.export = self.root / "export"
        self.export.mkdir()
        (self.export / "OpenD2.x86_64").write_bytes(b"fixture executable")
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://Main.cs": b"code", "res://icon.png": b"png"}))

    def blocked(self, *paths, hashes=()):
        return data_guard.scan(paths or [self.export], hashes).blocked

    def test_clean_export_passes(self):
        self.assertEqual([], self.blocked())

    def test_injected_mpq_wav_bik_and_global_paths_fail(self):
        for name in ("d2data.mpq", "sfx/hit.wav", "video/intro.bik", "data/global/ui/cursor.dc6",
                     "Data/Global/Excel/armor.txt", "UPPER.MPQ", "tables/string.TBL", "art/act1.Pl2"):
            with self.subTest(name=name):
                path = self.export / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(FAKE)
                self.assertTrue(any(item.startswith(name) for item in self.blocked()), self.blocked())
                path.unlink()

    def test_inside_pck_including_godot_import_names(self):
        for name in ("res://audio/hit.wav", "res://.godot/imported/hit.wav-0123abcd.sample", "res://intro.bik.import",
                     "res://data/global/palette/act1/pal.dat"):
            with self.subTest(name=name):
                (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://Main.cs": b"code", name: FAKE}))
                self.assertTrue(any("OpenD2.pck!" in item for item in self.blocked()), self.blocked())

    def test_format_2_pck_and_encrypted_directory(self):
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://music/town.wav": FAKE}, version=2, flags=0))
        self.assertTrue(any("town.wav" in item for item in self.blocked()))
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://Main.cs": b"code"}, flags=3))
        self.assertTrue(any("could not inspect" in item for item in self.blocked()))

    def test_nested_archives_and_renamed_originals_by_hash(self):
        inner = io.BytesIO()
        with zipfile.ZipFile(inner, "w") as archive:
            archive.writestr("deep/speech.wav", FAKE)
        outer = self.root / "outer.tar.gz"
        with tarfile.open(outer, "w:gz") as archive:
            info = tarfile.TarInfo("bundle.zip")
            info.size = len(inner.getvalue())
            archive.addfile(info, io.BytesIO(inner.getvalue()))
        self.assertTrue(any("bundle.zip!deep/speech.wav" in item for item in self.blocked(outer)))
        (self.export / "harmless.bin").write_bytes(FAKE)
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://renamed.res": FAKE}))
        found = self.blocked(hashes={hashlib.sha256(FAKE).hexdigest()})
        self.assertEqual(2, sum("original game file hash" in item for item in found), found)

    def test_original_hashes_load_from_sums_file_or_folder_at_run_time(self):
        sums = self.root / "SHA256SUMS"
        sums.write_text(f"{hashlib.sha256(FAKE).hexdigest()}  fake.mpq\n")
        self.assertEqual({hashlib.sha256(FAKE).hexdigest()}, data_guard.load_original_hashes(sums))
        folder = self.root / "owned"
        folder.mkdir()
        (folder / "x.bin").write_bytes(b"other")
        with mock.patch.dict(os.environ, {"D2_DATA_PATH": str(folder)}, clear=False):
            os.environ.pop("OPEND2_ORIGINAL_SHA256SUMS", None)
            self.assertEqual({hashlib.sha256(b"other").hexdigest()}, data_guard.load_original_hashes())

    def test_command_exits_1_and_names_the_file(self):
        (self.export / "d2music.mpq").write_bytes(FAKE)
        result = subprocess.run([sys.executable, str(Path(data_guard.__file__)), str(self.export)],
                                capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertIn("d2music.mpq: game-data extension .mpq", result.stderr)

    def test_release_pack_refuses_and_removes_the_archive(self):
        (self.export / "OpenD2.x86_64").chmod(0o755)
        (self.export / "sound.wav").write_bytes(FAKE)
        output = self.root / "out"
        with self.assertRaises(data_guard.GameDataError):
            release.pack(self.export, output, "Linux", "v0.1.0", "a" * 40)
        self.assertEqual([], list(output.glob("OpenD2-*")))


if __name__ == "__main__":
    unittest.main()
