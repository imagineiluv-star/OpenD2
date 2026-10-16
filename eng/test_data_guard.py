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
            release.pack(self.export, output, "Linux", "v0.1.0", "a" * 40, rehearsal=True)
        self.assertEqual([], list(output.glob("OpenD2-*")))

    def add(self, name, data):
        path = self.export / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def tar_bytes(self, mode, name="d2data.mpq", data=FAKE):
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode=mode) as archive:
            info = tarfile.TarInfo(name)
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
        return buffer.getvalue()

    def test_every_tar_compression_is_opened(self):
        for mode, suffix in (("w:gz", "tgz"), ("w:bz2", "tar.bz2"), ("w:xz", "tar.xz"), ("w", "tar")):
            with self.subTest(mode=mode):
                path = self.add(f"bundle.{suffix}", self.tar_bytes(mode))
                self.assertTrue(any("bundle." + suffix + "!d2data.mpq" in item for item in self.blocked()), self.blocked())
                path.unlink()

    def test_single_compressed_streams_are_expanded(self):
        import bz2, gzip, lzma
        for name, data in (("blob.xz", lzma.compress(b"MPQ\x1a" + FAKE)), ("blob.bz2", bz2.compress(b"MPQ\x1a" + FAKE)),
                           ("blob.gz", gzip.compress(b"MPQ\x1a" + FAKE))):
            with self.subTest(name=name):
                path = self.add(name, data)
                self.assertTrue(any("MPQ archive header" in item for item in self.blocked()), self.blocked())
                path.unlink()

    def test_prefixed_sfx_zip_is_found_by_central_directory(self):
        inner = io.BytesIO()
        with zipfile.ZipFile(inner, "w") as archive:
            archive.writestr("x.wav", FAKE)
        self.add("setup.exe", b"MZ fake stub " * 50 + inner.getvalue())
        self.assertTrue(any("setup.exe!x.wav" in item for item in self.blocked()), self.blocked())

    def test_uninspectable_formats_block(self):
        for name, data in (("a.7z", b"7z\xbc\xaf\x27\x1c" + FAKE), ("payload", b"Rar!\x1a\x07\x00" + FAKE),
                           ("payload2", b"\x28\xb5\x2f\xfd" + FAKE), ("x.cab", b"MSCF" + FAKE),
                           ("renamed.rar", FAKE), ("broken.tar.xz", b"\xfd7zXZ\x00garbage")):
            with self.subTest(name=name):
                path = self.add(name, data)
                found = self.blocked()
                self.assertTrue(any(item.startswith(name) for item in found), found)
                path.unlink()

    def test_renamed_mpq_fails_by_header_without_hash_list(self):
        for data in (b"MPQ\x1a" + FAKE, b"MPQ\x1b" + FAKE, b"\0" * 1024 + b"MPQ\x1a" + FAKE):
            with self.subTest(data=data[:8]):
                self.add("music.res", data)
                self.assertTrue(any("music.res: MPQ archive header" in item for item in self.blocked()), self.blocked())
        self.add("music.res", b"\0" * 7 + b"MPQ\x1a")  # unaligned bytes are not a header
        self.assertEqual([], self.blocked())

    def test_pck_appended_to_executable_is_scanned(self):
        pck = fake_pck({"res://audio/hit.wav": FAKE})
        self.add("OpenD2.x86_64", b"\x7fELF fixture" + pck + struct.pack("<Q", len(pck)) + b"GDPC")
        self.assertTrue(any("OpenD2.x86_64#embedded.pck!audio/hit.wav" in item for item in self.blocked()), self.blocked())
        self.add("OpenD2.x86_64", b"\x7fELF fixture" + struct.pack("<Q", 999) + b"GDPC")
        self.assertTrue(any("could not inspect" in item for item in self.blocked()), self.blocked())

    def test_zip_bomb_ratio_and_total_limits_block(self):
        bomb = io.BytesIO()
        with zipfile.ZipFile(bomb, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("zeros.res", b"\0" * (data_guard.RATIO_FLOOR * 4))
        self.add("bomb.zip", bomb.getvalue())
        self.assertTrue(any("compression ratio" in item for item in self.blocked()), self.blocked())
        (self.export / "bomb.zip").unlink()
        self.add("ok.tar", self.tar_bytes("w", "a.res", b"x" * 4096))
        guard = data_guard.Guard(max_total_bytes=1000)
        guard.path(self.export)
        self.assertTrue(any("decompressed total" in item for item in guard.blocked), guard.blocked)

    def test_name_normalisation_and_extra_extensions(self):
        for name in ("a.wav ", "a.wav~", "a.wav.", "a.\uff57av", "ui/font16.fnt", "act1.pal", "panel.pcx",
                     "excel/armor.bin", "Excel/Weapons.txt", "palette/pal.dat", "data/local/lng/eng/x.txt"):
            with self.subTest(name=name):
                path = self.add(name, FAKE)
                self.assertTrue(self.blocked(), name)
                path.unlink()

    def test_third_party_txt_and_dat_are_not_false_positives(self):
        for name in ("icudt72l.dat", "LICENSE.txt", "licenses/glm/copying.txt", "README.txt"):
            self.add(name, FAKE)
        self.assertEqual([], self.blocked())

    def test_encrypted_pck_entry_and_symlinks_block(self):
        pck = bytearray(fake_pck({"res://Main.cs": b"code"}))
        flags_at = pck.rfind(b"\0" * 4)  # last entry flags field
        pck[flags_at] = 1
        (self.export / "OpenD2.pck").write_bytes(bytes(pck))
        self.assertTrue(any("encrypted PCK entry" in item for item in self.blocked()), self.blocked())
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://Main.cs": b"code"}))
        (self.export / "link").symlink_to(self.export)
        self.assertTrue(any("link: symlink" in item for item in self.blocked()), self.blocked())

    def test_release_requires_hash_list_and_never_reports_bare_pass_without_it(self):
        with self.assertRaises(data_guard.MissingOriginalHashes):
            data_guard.scan([self.export], set(), require_original_hashes=True)
        self.assertEqual("PASS (no original hash list)", data_guard.scan([self.export]).result)
        self.assertEqual("PASS", data_guard.scan([self.export], {"0" * 64}).result)
        (self.export / "OpenD2.x86_64").chmod(0o755)
        output = self.root / "out"
        with mock.patch.dict(os.environ, {}, clear=False):
            for key in ("OPEND2_ORIGINAL_SHA256SUMS", "D2_DATA_PATH", "OPEND2_RELEASE_REHEARSAL"):
                os.environ.pop(key, None)
            with self.assertRaises(data_guard.MissingOriginalHashes):
                release.pack(self.export, output, "Linux", "v0.1.0", "a" * 40)
            self.assertEqual([], list(output.glob("OpenD2-*")))

    def test_pack_prints_the_size_warning(self):
        (self.export / "OpenD2.x86_64").chmod(0o755)
        output = self.root / "out"
        with mock.patch.object(data_guard, "WARN_BYTES", 10), mock.patch("builtins.print") as printed:
            original = data_guard.Guard.__init__.__defaults__
            data_guard.Guard.__init__.__defaults__ = ((), 10, data_guard.MAX_TOTAL_BYTES)
            try:
                metadata = release.pack(self.export, output, "Linux", "v0.1.0", "a" * 40, rehearsal=True)
            finally:
                data_guard.Guard.__init__.__defaults__ = original
        lines = [" ".join(map(str, call.args)) for call in printed.call_args_list]
        self.assertTrue(any(line.startswith("WARNING data guard:") and "exceeds 10" in line for line in lines), lines)
        self.assertTrue(metadata["data_guard"]["warnings"])
        self.assertEqual("PASS (no original hash list)", metadata["data_guard"]["result"])


if __name__ == "__main__":
    unittest.main()
