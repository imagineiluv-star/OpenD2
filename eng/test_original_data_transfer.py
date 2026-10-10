"""Synthetic transfer tests; these do not decode or certify real MPQs."""
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch
import warnings
import zipfile

import original_data as data
import original_data_transfer as transfer


class TransferTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        source = self.root / "source"
        source.mkdir()
        (source / "d2data.mpq").write_bytes(b"synthetic base data")
        (source / "patch_d2.mpq").write_bytes(b"synthetic patch data")
        self.dataset = data.register(source, self.root / "vault", "unverified", "ko")
        self.archive = self.root / "transfer.zip"
        self.destination = self.root / "other computer"

    def rewrite(self, transform):
        transfer.pack(self.dataset, self.archive)
        with zipfile.ZipFile(self.archive) as archive:
            entries = [(e, archive.read(e)) for e in archive.infolist()]
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(self.archive, "w") as archive:
                for name, payload in transform(entries):
                    archive.writestr(name, payload)

    def assert_rejected(self):
        with self.assertRaises((ValueError, zipfile.BadZipFile)):
            transfer.unpack(self.archive, self.destination)
        self.assertFalse(self.destination.exists() and list(self.destination.iterdir()))

    def test_roundtrip_preserves_identity_bytes_and_unverified_status(self):
        transfer.pack(self.dataset, self.archive)
        restored = transfer.unpack(self.archive, self.destination)
        self.assertEqual(data.verify(self.dataset), data.verify(restored))
        self.assertEqual(restored.name, self.dataset.name)
        self.assertEqual(transfer.unpack(self.archive, self.destination), restored)
        for source in (self.dataset / "mpq").iterdir():
            self.assertEqual(source.read_bytes(), (restored / "mpq" / source.name).read_bytes())

    def test_existing_output_and_corrupted_destination_are_not_overwritten(self):
        transfer.pack(self.dataset, self.archive)
        before = self.archive.read_bytes()
        with self.assertRaises(FileExistsError):
            transfer.pack(self.dataset, self.archive)
        self.assertEqual(self.archive.read_bytes(), before)
        restored = transfer.unpack(self.archive, self.destination)
        damaged = restored / "mpq/d2data.mpq"
        damaged.write_bytes(b"damaged")
        with self.assertRaises(ValueError):
            transfer.unpack(self.archive, self.destination)
        self.assertEqual(damaged.read_bytes(), b"damaged")
        self.assertEqual([p.resolve() for p in self.destination.iterdir()], [restored.resolve()])

    def test_changed_payload_with_valid_zip_crc_fails_dataset_hash(self):
        self.rewrite(lambda entries: [(e, b"changed" if e.filename.endswith("d2data.mpq") else b) for e, b in entries])
        self.assert_rejected()

    def test_unsafe_paths_and_duplicate_entries_are_rejected(self):
        for name in ("../escape.mpq", "/absolute.mpq", "C:/escape.mpq", self.dataset.name + "/mpq/../escape.mpq", self.dataset.name + "/mpq\\escape.mpq"):
            with self.subTest(name=name):
                self.rewrite(lambda entries: entries + [(name, b"invalid")])
                self.assert_rejected()
                self.archive.unlink()
        self.rewrite(lambda entries: entries + [entries[1]])
        self.assert_rejected()

    def test_symlink_metadata_is_rejected_without_needing_os_privileges(self):
        def linked(entries):
            entries[1][0].create_system = 3
            entries[1][0].external_attr = (stat.S_IFLNK | 0o777) << 16
            return entries
        self.rewrite(linked)
        self.assert_rejected()

    def test_missing_manifest_and_mixed_roots_are_rejected(self):
        self.rewrite(lambda entries: entries[1:])
        self.assert_rejected()
        self.archive.unlink()
        self.rewrite(lambda entries: entries + [("0" * 64 + "/mpq/extra.mpq", b"other dataset")])
        self.assert_rejected()

    def test_size_budget_and_git_checkout_are_rejected(self):
        transfer.pack(self.dataset, self.archive)
        with patch.object(data, "MAX_FILE_BYTES", 1):
            self.assert_rejected()
        self.destination.mkdir()
        (self.destination / ".git").write_text("gitdir: elsewhere")
        with self.assertRaises(ValueError):
            transfer.unpack(self.archive, self.destination)
        with self.assertRaises(ValueError):
            transfer.pack(self.dataset, self.destination / "data.zip")

    def test_source_change_during_pack_leaves_no_output(self):
        verify = data.verify
        def changed(folder):
            manifest = verify(folder)
            (folder / "mpq/d2data.mpq").write_bytes(b"changed")
            return manifest
        with patch.object(data, "verify", side_effect=changed):
            with self.assertRaises(ValueError):
                transfer.pack(self.dataset, self.archive)
        self.assertFalse(self.archive.exists())
        self.assertFalse(list(self.root.glob(".transfer-*")))


if __name__ == "__main__":
    unittest.main()
