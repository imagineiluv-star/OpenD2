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
import bz2
import gzip
import lzma
import zlib
from unittest import mock
import zipfile

import data_guard
import release

_ENV_KEYS = ("D2_DATA_PATH", "OPEND2_ORIGINAL_SHA256SUMS", "OPEND2_RELEASE_REHEARSAL")


def _hermetic_cache():
    os.environ["D2_ASSET_CACHE"] = "none"  # never hash the developer's real extracted cache
_saved_env = {}


def setUpModule():
    # Hermetic: a developer's D2_DATA_PATH / hash list must not change results.
    for key in _ENV_KEYS:
        if key in os.environ:
            _saved_env[key] = os.environ.pop(key)
    _saved_env.setdefault("D2_ASSET_CACHE", os.environ.get("D2_ASSET_CACHE"))
    _hermetic_cache()
    global _saved_manifest
    _saved_manifest = data_guard.MPQ_MANIFEST
    data_guard.MPQ_MANIFEST = Path("/nonexistent/d2-mpq-manifest.json")  # tests pick their hash lists explicitly


def tearDownModule():
    data_guard.MPQ_MANIFEST = _saved_manifest
    os.environ.pop("D2_ASSET_CACHE", None)
    os.environ.update({k: v for k, v in _saved_env.items() if v is not None})


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
                # Case-insensitive file systems (Windows/macOS) reuse the data/global folder an earlier subtest made.
                self.assertTrue(any(item.lower().startswith(name.lower()) for item in self.blocked()), self.blocked())
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
            data_guard.Guard.__init__.__defaults__ = ((), 10, data_guard.MAX_TOTAL_BYTES, ())
            try:
                metadata = release.pack(self.export, output, "Linux", "v0.1.0", "a" * 40, rehearsal=True)
            finally:
                data_guard.Guard.__init__.__defaults__ = original
        lines = [" ".join(map(str, call.args)) for call in printed.call_args_list]
        self.assertTrue(any(line.startswith("WARNING data guard:") and "exceeds 10" in line for line in lines), lines)
        self.assertTrue(metadata["data_guard"]["warnings"])
        self.assertEqual("PASS (no original hash list)", metadata["data_guard"]["result"])
    def test_wav_and_bink_detected_by_content_under_any_name(self):
        for name, data in (("a.res", b"RIFF\x10\0\0\0WAVEfmt " + FAKE), ("b.bin", b"BIKi" + FAKE), ("c", b"KB2j" + FAKE)):
            with self.subTest(name=name):
                path = self.add(name, data)
                self.assertTrue(any(item.startswith(name + ":") for item in self.blocked()), self.blocked())
                path.unlink()
        self.add("riff.res", b"RIFF\x10\0\0\0AVI LIST")  # other RIFF types are not WAV
        self.add("bike.txt", b"BIKE ride")
        self.assertEqual([], self.blocked())

    def test_trailing_underscore_and_cyrillic_lookalikes(self):
        for name in ("x.mpq_", "x.\u043cpq", "x.w\u0430v"):
            with self.subTest(name=name):
                path = self.add(name, FAKE)
                self.assertTrue(self.blocked(), name)
                path.unlink()
        self.add("\u0442\u0435\u043a\u0441\u0442.txt", FAKE)  # Cyrillic base name, plain extension: allowed
        self.assertEqual([], self.blocked())

    def test_scan_started_inside_package_still_sees_data_global(self):
        start = self.export / "pkg" / "data"
        self.add("pkg/data/global/excel/foo.res", FAKE)
        self.assertTrue(any("data/global path" in item for item in self.blocked(start)), self.blocked(start))

    def test_lzma_and_compress_short_magics_are_not_false_positives(self):
        self.add("level.res", b"]\0\0 ordinary bytes")
        self.add("other.res", b"\x1f\x9d ordinary bytes")
        self.assertEqual([], self.blocked())
        self.add("x.lzma", FAKE)
        self.assertTrue(self.blocked())

    def test_hash_list_needs_a_plausible_number_of_entries(self):
        few = {f"{i:064x}" for i in range(data_guard.MIN_ORIGINAL_HASHES - 1)}
        with self.assertRaises(data_guard.MissingOriginalHashes) as caught:
            data_guard.scan([self.export], few, require_original_hashes=True)
        self.assertIn("looks incomplete", str(caught.exception))
        self.assertIn("secret OPEND2_ORIGINAL_SHA256SUMS", str(caught.exception))
        enough = few | {"f" * 64}
        self.assertEqual("PASS", data_guard.scan([self.export], enough, require_original_hashes=True).result)

    def test_explicit_hash_sources_ignore_the_environment(self):
        sums = self.root / "SHA256SUMS"
        sums.write_text(f"{'a' * 64}  d2data.mpq\n")
        owned = self.root / "owned"
        owned.mkdir()
        (owned / "x").write_bytes(b"other")
        with mock.patch.dict(os.environ, {"D2_DATA_PATH": str(owned)}):
            self.assertEqual({"a" * 64}, data_guard.load_original_hashes(sums))
    @staticmethod
    def fake_dc6(directions=1, frames=2):
        table_end = 24 + 4 * directions * frames
        head = struct.pack("<3I", 6, 1, 0) + b"\xee" * 4 + struct.pack("<II", directions, frames)
        return head + struct.pack("<I", table_end) + b"\0" * (4 * directions * frames - 4) + FAKE

    @staticmethod
    def fake_rscc(payload, mode=1, block=16):
        blocks = [payload[i:i + block] for i in range(0, len(payload), block)]
        if len(payload) % block == 0:
            blocks.append(b"")
        packed = [zlib.compress(b) if mode == 1 else gzip.compress(b, mtime=0) for b in blocks]
        return b"RSCC" + struct.pack("<III", mode, block, len(payload)) + b"".join(struct.pack("<I", len(p)) for p in packed) + b"".join(packed)

    def test_ogg_and_dc6_detected_by_content(self):
        for name, data in (("music.res", b"OggS\0\x02" + FAKE), ("sprite.png", self.fake_dc6()),
                           ("big.png", self.fake_dc6(8, 16))):
            with self.subTest(name=name):
                path = self.add(name, data)
                self.assertTrue(any(item.startswith(name + ":") for item in self.blocked()), self.blocked())
                path.unlink()
        near = bytearray(self.fake_dc6())
        near[24] ^= 1  # wrong first frame pointer: not a DC6
        self.add("x.res", bytes(near))
        self.add("y.res", b"OggSync notes")
        self.assertEqual([], self.blocked())

    def test_flattened_data_global_names(self):
        for name in ("data_global_items_x.png", "Data-Global/x.res", "dataglobal/ui.res", "data_local_font.res"):
            with self.subTest(name=name):
                path = self.add(name, FAKE)
                self.assertTrue(self.blocked(), name)
                path.unlink()
        self.add("metadata_globals.json", FAKE)
        self.assertEqual([], self.blocked())

    def test_raw_zlib_and_godot_rscc_are_opened(self):
        self.add("wrapped.res", zlib.compress(b"RIFF\x10\0\0\0WAVE" + FAKE))
        self.assertTrue(any("wrapped.res!zlib" in item for item in self.blocked()), self.blocked())
        (self.export / "wrapped.res").unlink()
        self.add("x\u009c.res", b"x\x9c not really zlib")  # valid header bytes, does not inflate: plain data
        self.assertEqual([], self.blocked())
        for mode in (1, 3):
            with self.subTest(mode=mode):
                (self.export / "OpenD2.pck").write_bytes(
                    fake_pck({"res://snd.res": self.fake_rscc(b"MPQ\x1a" + FAKE * 3, mode)}))
                self.assertTrue(any("snd.res!rscc: MPQ archive header" in item for item in self.blocked()), self.blocked())
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://snd.res": b"RSCC" + struct.pack("<III", 2, 16, 4)}))
        self.assertTrue(any("compression mode 2" in item for item in self.blocked()), self.blocked())

    def test_lzip_blocks_and_zip_symlink_entries_block(self):
        self.add("a.res", b"LZIP\x01" + FAKE)
        self.assertTrue(any(item.startswith("a.res:") for item in self.blocked()))
        (self.export / "a.res").unlink()
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            info = zipfile.ZipInfo("link")
            info.external_attr = (0o120777) << 16
            archive.writestr(info, "../../data/x")
        self.add("links.zip", buffer.getvalue())
        self.assertTrue(any("links.zip!link: symlink entry" in item for item in self.blocked()), self.blocked())

    def test_asset_cache_hashes_are_loaded_at_run_time(self):
        cache = self.root / "cache" / "1.11b"
        (cache / "png").mkdir(parents=True)
        (cache / "cache-manifest.json").write_text('{"converter": "p1-mpq-2"}')
        derived = b"converted sprite frame bytes (synthetic) " * 4
        (cache / "png" / "d00_f000.png").write_bytes(derived)
        (cache / "tiny.png").write_bytes(b"x")  # below the size floor: ignored
        hashes = data_guard.load_derived_hashes(str(self.root / "cache"))
        self.assertEqual({hashlib.sha256(derived).hexdigest()}, hashes)
        other = self.root / "other"
        other.mkdir()
        (other / "cache-manifest.json").write_text('{"converter": "something-else"}')
        (other / "f").write_bytes(derived)
        self.assertEqual(set(), data_guard.load_derived_hashes(str(other)))
        self.assertEqual(set(), data_guard.load_derived_hashes("none"))
        self.add("icon2.png", derived)
        found = data_guard.scan([self.export], derived_hashes=hashes).blocked
        self.assertTrue(any("extracted asset-cache file hash" in item for item in found), found)
    def rscc_raw(self, blocks, block_size, total):
        packed = [zlib.compress(b) for b in blocks]
        return (b"RSCC" + struct.pack("<III", 1, block_size, total) + b"".join(struct.pack("<I", len(p)) for p in packed)
                + b"".join(packed))

    def test_rscc_block_bomb_is_bounded_and_blocked(self):
        bomb = self.rscc_raw([b"\0" * (300 * 1024 * 1024)], 100, 50)  # declares 50 bytes, inflates to 300 MB
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://x.res": bomb}))
        found = self.blocked()
        self.assertTrue(any("x.res: could not inspect" in item and "block size" in item for item in found), found)

    def test_rscc_payload_hidden_past_declared_total_is_blocked(self):
        hidden = self.rscc_raw([b"A" * 16 + b"RIFF\x10\0\0\0WAVE"], 64, 16)
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://y.res": hidden}))
        found = self.blocked()
        self.assertTrue(any("y.res: could not inspect" in item and "declares 16" in item for item in found), found)

    def test_corrupt_rscc_and_zlib_streams_are_reported_not_raised(self):
        good = zlib.compress(b"payload " * 8)
        corrupt = (b"RSCC" + struct.pack("<III", 1, 64, 64) + struct.pack("<II", len(good), 0)
                   + good[:4] + b"\xff" * (len(good) - 4))
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://z.res": corrupt}))
        found = self.blocked()
        self.assertTrue(any("z.res: could not inspect" in item for item in found), found)
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://Main.cs": b"code"}))
        stream = zlib.compress(os.urandom(200000), 0)
        self.add("cut.res", stream[:-50])  # starts fine, then ends early: flush() fails
        found = self.blocked()
        self.assertTrue(any("cut.res: could not inspect" in item for item in found), found)

    def test_small_window_zlib_and_raw_deflate_are_opened(self):
        wav = b"RIFF\x10\0\0\0WAVE" + FAKE + bytes(range(256))
        for wbits in (9, 12):
            with self.subTest(wbits=wbits):
                packer = zlib.compressobj(9, zlib.DEFLATED, wbits)
                self.add("w.res", packer.compress(wav) + packer.flush())
                self.assertTrue(any("w.res!zlib" in item for item in self.blocked()), self.blocked())
        (self.export / "w.res").unlink()
        raw = zlib.compressobj(9, zlib.DEFLATED, -15)
        self.add("r.res", raw.compress(wav) + raw.flush())
        self.assertTrue(any("r.res!deflate" in item for item in self.blocked()), self.blocked())

    def test_data_path_needs_an_exact_data_component(self):
        self.add("mydata/global/x.res", FAKE)
        self.add("metadata_global.res", FAKE)
        self.assertEqual([], self.blocked())
        start = self.export / "mydata"
        self.assertEqual([], self.blocked(start))  # start folder merely ending in "data"

    def test_pck_at_an_odd_offset_is_scanned(self):
        pck = fake_pck({"res://audio/hit.wav": FAKE})
        self.add("bundle.bin2", b"\x7fELF junk" * 7 + b"x" + pck + b"tail bytes")
        self.assertTrue(any("bundle.bin2@" in item and "hit.wav" in item for item in self.blocked()), self.blocked())
        self.add("bundle.bin2", b"text GDPC mention without a header")
        self.assertEqual([], self.blocked())

    def test_committed_mpq_manifest_is_the_default_hash_list(self):
        real = _saved_manifest
        hashes = data_guard.manifest_hashes(real)
        self.assertGreaterEqual(len(hashes), data_guard.MIN_ORIGINAL_HASHES)
        with mock.patch.object(data_guard, "MPQ_MANIFEST", real):
            self.assertEqual(hashes, data_guard.load_original_hashes())
            sums = self.root / "S"
            sums.write_text("a" * 64 + "  x\n")
            self.assertEqual({"a" * 64}, data_guard.load_original_hashes(sums))  # explicit list stays exact

    def test_unknown_binary_formats_are_listed_not_blocked(self):
        self.add("mystery.res", b"\x01\x02\0\0 opaque")
        self.add("lib.so", b"\x7fELF\0\0")
        guard = data_guard.scan([self.export])
        self.assertEqual([], guard.blocked)
        self.assertEqual(["mystery.res"], [u for u in guard.unknown if "mystery" in u or "lib.so" in u])
    def test_trailing_bytes_after_zlib_and_raw_deflate_are_scanned(self):
        ogg = b"OggS\0\x02" + FAKE
        for name, tail in (("t1.res", ogg), ("t2.res", gzip.compress(b"MPQ\x1a" + FAKE, mtime=0)),
                           ("t3.res", zlib.compress(b"MPQ\x1a" + FAKE))):
            with self.subTest(name=name):
                path = self.add(name, zlib.compress(b"harmless text " * 20) + tail)
                self.assertTrue(any(item.startswith(name + "!zlib-trailer") for item in self.blocked()), self.blocked())
                path.unlink()
        raw = zlib.compressobj(9, zlib.DEFLATED, -15)
        body = b"".join(hashlib.sha256(bytes([i])).digest() for i in range(64))  # incompressible: >= 1 KiB stream
        self.add("t4.res", raw.compress(body) + raw.flush() + ogg)
        self.assertTrue(any(item.startswith("t4.res!deflate-trailer") for item in self.blocked()), self.blocked())

    def test_corrupt_mpq_manifest_gives_a_clear_error(self):
        broken = self.root / "d2-mpq-manifest.json"
        for text in ("{ not json", "", "[1, 2]"):
            with self.subTest(text=text):
                broken.write_text(text)
                with self.assertRaises(data_guard.ManifestError) as caught:
                    data_guard.manifest_hashes(broken)
                self.assertIn("cannot read the MPQ hash manifest", str(caught.exception))
        result = subprocess.run([sys.executable, "-c", "import sys, data_guard; from pathlib import Path; "
                                 f"data_guard.MPQ_MANIFEST = Path({str(broken)!r}); sys.argv = ['g', {str(self.export)!r}]; "
                                 "data_guard.main()"], cwd=Path(data_guard.__file__).parent, capture_output=True, text=True,
                                env=dict(os.environ, D2_ASSET_CACHE="none"))
        self.assertEqual(1, result.returncode)
        self.assertIn("DATA GUARD FAILED: cannot read the MPQ hash manifest", result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_cache_variable_matches_the_app_with_deprecated_alias(self):
        with mock.patch.dict(os.environ, {}, clear=False):
            for key in ("D2_ASSET_CACHE", "OPEND2_ASSET_CACHE"):
                os.environ.pop(key, None)
            roots = data_guard.cache_roots()
            self.assertEqual(data_guard.app_cache_root(), roots[0])
            self.assertTrue(str(roots[0]).endswith(os.path.join("OpenD2", "asset-cache")))
            (self.root / "old").mkdir()
            (self.root / "new").mkdir()
            os.environ["OPEND2_ASSET_CACHE"] = str(self.root / "old")
            with mock.patch("sys.stderr", new_callable=io.StringIO) as err:
                self.assertEqual([self.root / "old"], data_guard.cache_roots())
            self.assertIn("deprecated", err.getvalue())
            os.environ["D2_ASSET_CACHE"] = str(self.root / "new")
            self.assertEqual([self.root / "new"], data_guard.cache_roots())
            os.environ["D2_ASSET_CACHE"] = "none"
            self.assertEqual([], data_guard.cache_roots())
        source = (Path(data_guard.__file__).resolve().parents[1] / "src/OpenD2.Assets/D2DataPaths.cs").read_text()
        self.assertIn(f'CachePathVariable = "{data_guard.CACHE_VARIABLE}"', source)


    def test_manifest_with_wrong_inner_types_is_a_manifest_error(self):
        broken = self.root / "d2-mpq-manifest.json"
        for text in ('{"versions": [{"files": null}]}', '{"versions": [{"files": [1]}]}',
                     '{"versions": [{"files": {"a.mpq": 5}}]}', '{"versions": [{"files": {"a.mpq": "abc"}}]}',
                     '{"versions": [{"files": {"a.mpq": {"sha256": 7}}}]}'):
            with self.subTest(text=text):
                broken.write_text(text)
                with self.assertRaises(data_guard.ManifestError):
                    data_guard.manifest_hashes(broken)
        broken.write_text('{"versions": [{"files": {"a.mpq": {"sha256": "' + "B" * 64 + '"}}}, {"placeholder": true}]}')
        self.assertEqual({"b" * 64}, data_guard.manifest_hashes(broken))

    def test_explicit_missing_asset_cache_is_an_error(self):
        missing = str(self.root / "no-such-cache")
        with self.assertRaises(data_guard.CacheError):
            data_guard.load_derived_hashes(missing)
        with mock.patch.dict(os.environ, {"D2_ASSET_CACHE": missing}):
            with self.assertRaises(data_guard.CacheError) as caught:
                data_guard.load_derived_hashes()
            self.assertIn("D2_ASSET_CACHE=none", str(caught.exception))
        result = subprocess.run([sys.executable, "data_guard.py", "--asset-cache", missing, str(self.export)],
                                cwd=Path(data_guard.__file__).parent, capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertIn("is not a directory", result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_rscc_block_size_has_an_upper_bound(self):
        huge = self.rscc_raw([b"x" * 64], 64 * 1024 * 1024, 64)
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://h.res": huge}))
        found = self.blocked()
        self.assertTrue(any("h.res: could not inspect" in item and "exceeds" in item for item in found), found)

    def test_bytes_after_a_stream_inside_an_rscc_block_are_scanned(self):
        packed = zlib.compress(b"A" * 16) + b"OggS\0\x02" + FAKE
        rscc = b"RSCC" + struct.pack("<IIII", 1, 64, 16, len(packed)) + packed
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://b.res": rscc}))
        found = self.blocked()
        self.assertTrue(any("b.res!rscc-block0-trailer" in item for item in found), found)
        (self.export / "OpenD2.pck").write_bytes(fake_pck({"res://b.res": self.fake_rscc(b"plain text " * 20)}))
        self.assertEqual([], self.blocked())

    def test_short_highly_compressed_raw_deflate_trailer_is_scanned(self):
        raw = zlib.compressobj(9, zlib.DEFLATED, -15)
        stream = raw.compress(b"\0" * 4000) + raw.flush()  # a few dozen bytes inflating 4000
        self.assertLess(len(stream), 1024)
        self.add("s.res", stream + b"OggS\0\x02" + FAKE)
        self.assertTrue(any(item.startswith("s.res!deflate-trailer") for item in self.blocked()), self.blocked())

    def test_raw_deflate_far_beyond_its_input_hits_the_ratio_limit(self):
        raw = zlib.compressobj(9, zlib.DEFLATED, -15)
        self.add("big.res", raw.compress(b"MPQ\x1a" + b"\0" * (300 * 1024 * 1024)) + raw.flush())
        found = self.blocked()
        self.assertTrue(any("big.res: could not inspect" in item and "ratio" in item for item in found), found)

    def test_bytes_after_bz2_and_xz_streams_are_scanned(self):
        ogg = b"OggS\0\x02" + FAKE
        for name, compress in (("a.bin.bz2", bz2.compress), ("a.bin.xz", lzma.compress)):
            with self.subTest(name=name):
                path = self.add(name, compress(b"harmless " * 50) + compress(b"second stream") + ogg)
                found = self.blocked()
                self.assertTrue(any("-trailer" in item and item.startswith(name) for item in found), found)
                path.write_bytes(compress(b"harmless " * 50) + compress(b"second stream"))
                self.assertEqual([], self.blocked())
                path.unlink()

    def test_random_blobs_do_not_trigger_short_deflate_rescans(self):
        import random
        rng = random.Random(1017)
        guard = data_guard.Guard()
        hits = sum(bool(guard.raw_deflate("r", rng.randbytes(rng.choice((80, 300, 2000, 9000))), 0)) for _ in range(3000))
        self.assertEqual([], guard.blocked)
        self.assertEqual(0, hits)

    # Synthetic 16-bit mono PCM header with 16 bytes of silence (not game audio).
    WAV = (b"RIFF\x34\0\0\0WAVEfmt \x10\0\0\0" + b"\x01\0\x01\0\x22\x56\0\0\x44\xac\0\0\x02\0\x10\0"
           + b"data\x10\0\0\0" + b"\0" * 16)

    @staticmethod
    def ogg_page(payload=FAKE):
        page = bytearray(b"OggS\0\x02" + b"\0" * 8 + b"\x01\0\0\0" + b"\0\0\0\0" + b"\0\0\0\0"
                         + bytes([1, len(payload)]) + payload)
        struct.pack_into("<I", page, 22, data_guard._ogg_crc(page))
        return bytes(page)

    def test_wav_after_one_leading_byte_is_blocked(self):
        self.add("sound.bin", b"x" + self.WAV)
        found = self.blocked()
        self.assertTrue(any(item.startswith("sound.bin:") and "WAV audio" in item and "offset 1" in item
                            for item in found), found)

    def test_wav_inside_a_second_member_is_blocked(self):
        for name, compress in (("m.bin.gz", lambda b: gzip.compress(b, mtime=0)), ("m.bin.bz2", bz2.compress),
                               ("m.bin.xz", lzma.compress)):
            with self.subTest(name=name):
                path = self.add(name, compress(b"harmless") + compress(self.WAV))
                found = self.blocked()
                self.assertTrue(any(item.startswith(f"{name}!m.bin#member2:") and "WAV audio" in item
                                    for item in found), found)
                path.write_bytes(compress(b"harmless") + compress(b"second harmless member"))
                self.assertEqual([], self.blocked())
                path.unlink()

    def test_gzip_garbage_after_the_last_member_is_still_refused(self):
        self.add("g.bin.gz", gzip.compress(b"harmless", mtime=0) + b"junk!")
        found = self.blocked()
        self.assertTrue(any("g.bin.gz: could not inspect" in item for item in found), found)
        (self.export / "g.bin.gz").write_bytes(gzip.compress(b"harmless", mtime=0) + b"\0" * 8)  # zero padding is fine
        self.assertEqual([], self.blocked())

    def test_media_headers_at_other_offsets_are_found_with_structural_checks(self):
        bink = b"BIKi" + struct.pack("<6I", 4000, 10, 300, 10, 640, 480) + b"\0" * 4000
        mpq = b"MPQ\x1a" + struct.pack("<IIHH", 32, 4096, 1, 3) + b"\0" * 64
        cases = {"WAV audio": self.WAV, "Ogg audio": self.ogg_page(), "Bink video": bink, "MPQ archive": mpq,
                 "DC6 sprite": self.fake_dc6()}
        for kind, payload in cases.items():
            with self.subTest(kind=kind):
                hit = data_guard.Guard.embedded_media(b"prefix bytes " * 3 + b"!" + payload + b"tail")
                self.assertIsNotNone(hit)
                self.assertIn(kind, hit[1])
                self.assertEqual(40, hit[0])

    def test_bare_magic_bytes_in_ordinary_binaries_are_not_media(self):
        broken_ogg = bytearray(self.ogg_page())
        broken_ogg[-1] ^= 1  # CRC no longer matches
        for blob in (b"\x7fELF code RIFF strings WAVE and fmt elsewhere", b"..." + bytes(broken_ogg),
                     b"xx BIKi" + b"\xff" * 40, b"xMPQ\x1a" + b"\xff" * 40, b"text OggS mention"):
            with self.subTest(blob=blob[:16]):
                self.assertIsNone(data_guard.Guard.embedded_media(blob))
        import random
        rng = random.Random(1018)
        self.assertEqual(0, sum(bool(data_guard.Guard.embedded_media(rng.randbytes(20000))) for _ in range(500)))

    def test_decoys_before_the_payload_do_not_hide_it(self):
        page = self.ogg_page()
        for name, blob in (("riff5000.bin", b"\0" + b"RIFF" * 5000 + self.WAV), ("riff4096.bin", b"\0" + b"RIFF" * 4096 + self.WAV),
                           ("ogg256.bin", b"\0" + b"OggS" * 256 + page)):
            with self.subTest(name=name):
                path = self.add(name, blob)
                found = self.blocked()
                self.assertTrue(any(item.startswith(name + ":") and ("WAV audio" in item or "Ogg audio" in item) for item in found), found)
                path.unlink()

    def test_reaching_the_candidate_cap_fails_closed(self):
        decoy = b"RIFF\x04\0\0\0WAVEJUNK"  # passes the prefilter, fails the fmt/data check
        hit = data_guard.Guard.embedded_media(b"\0" + decoy * (data_guard.Guard.EMBED_CANDIDATES + 1) + self.WAV)
        self.assertIsNotNone(hit)
        self.assertIn("fail closed", hit[1])
        ogg_decoy = b"OggS\x00\xff"  # bad header type: prefilter match, structural reject
        hit = data_guard.Guard.embedded_media(b"\0" + ogg_decoy * 257 + self.ogg_page())
        self.assertIn("fail closed", hit[1])
        self.add("decoys.bin", b"\0" + decoy * (data_guard.Guard.EMBED_CANDIDATES + 1))
        self.assertTrue(any("decoys.bin" in item and "fail closed" in item for item in self.blocked()), self.blocked())

    def test_compressed_data_that_looks_like_an_empty_tar_is_still_scanned(self):
        for name, compress in (("z.bin.gz", lambda b: gzip.compress(b, mtime=0)), ("z.bin.bz2", bz2.compress),
                               ("z.bin.xz", lzma.compress)):
            for prefix in (b"\0" * 512, b"\0" * 1024):
                with self.subTest(name=name, prefix=len(prefix)):
                    path = self.add(name, compress(prefix + self.WAV))
                    found = self.blocked()
                    self.assertTrue(any(item.startswith(name) and "WAV audio" in item for item in found), found)
                    path.unlink()

    def test_bytes_after_the_end_of_a_compressed_tar_are_scanned(self):
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w", format=tarfile.PAX_FORMAT) as archive:
            info = tarfile.TarInfo("readme.txt")
            info.size = len(FAKE)
            archive.addfile(info, io.BytesIO(FAKE))
        clean = buffer.getvalue()
        self.add("pack.tar.gz", gzip.compress(clean, mtime=0))
        self.assertEqual([], self.blocked())
        (self.export / "pack.tar.gz").write_bytes(gzip.compress(clean + self.WAV, mtime=0))
        found = self.blocked()
        self.assertTrue(any(item.startswith("pack.tar.gz!tar-trailer") and "WAV audio" in item for item in found), found)

    def test_wav_header_templates_in_binaries_are_not_audio(self):
        # ALSA's built-in WAV header template (libasound): RIFF size 36, fmt fields all zero, empty data chunk.
        template = b"RIFF\x24\0\0\0WAVEfmt \x10\0\0\0" + b"\0" * 16 + b"data\0\0\0\0"
        self.add("libasound.so.2", b"\x7fELF" + b"\0" * 100 + template + b"\0" * 64)
        self.assertEqual([], self.blocked())
        self.add("libasound.so.2", b"\x7fELF" + b"\0" * 100 + self.WAV + b"\0" * 64)
        self.assertTrue(any("libasound.so.2" in item and "WAV audio" in item for item in self.blocked()), self.blocked())

    def test_dc6_candidate_checks_do_not_copy_large_entries(self):
        import time
        blob = b"x" + b"\xcd" * (32 * 1024 * 1024)
        started = time.perf_counter()
        self.assertIsNone(data_guard.Guard.embedded_media(blob))
        self.assertLess(time.perf_counter() - started, 3.0)  # was ~18 s when every candidate sliced the rest
        self.assertIsNotNone(data_guard.Guard.embedded_media(b"\xcd" * 4096 + self.fake_dc6() + b"\xcd" * 4096))


if __name__ == "__main__":
    unittest.main()
