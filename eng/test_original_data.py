"""Registry tests use fake bytes, not real Diablo assets or MPQ decoding evidence."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import original_data as data


class OriginalDataTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / "source"
        self.source.mkdir()
        self.vault = self.root / "vault"
        (self.source / "d2data.mpq").write_bytes(b"synthetic registry fixture")

    def test_registration_preserves_source_and_excludes_unrelated_files(self):
        (self.source / "save.d2s").write_bytes(b"not an asset")
        folder = data.register(self.source, self.vault, "1.10f", "ko")
        result = data.verify(folder)
        self.assertEqual(result["compatibility_status"], "NOT_RUN")
        self.assertEqual(result["gui_status"], "NOT_RUN")
        self.assertEqual([p.name for p in (folder / "mpq").iterdir()], ["d2data.mpq"])
        self.assertEqual((self.source / "d2data.mpq").read_bytes(), (folder / "mpq/d2data.mpq").read_bytes())
        self.assertEqual(data.register(self.source, self.vault, "1.10f", "ko"), folder)

    def test_changed_bytes_and_labels_create_separate_datasets(self):
        first = data.register(self.source, self.vault)
        second = data.register(self.source, self.vault, language="ko")
        (self.source / "d2data.mpq").write_bytes(b"changed")
        third = data.register(self.source, self.vault)
        self.assertEqual(len({first, second, third}), 3)
        data.verify(first)

    def test_corruption_is_rejected_without_overwriting_existing_data(self):
        folder = data.register(self.source, self.vault)
        (folder / "mpq/d2data.mpq").write_bytes(b"corrupted")
        with self.assertRaises(ValueError):
            data.verify(folder)
        with self.assertRaises(ValueError):
            data.register(self.source, self.vault)
        self.assertEqual((folder / "mpq/d2data.mpq").read_bytes(), b"corrupted")
        self.assertFalse(list(self.vault.glob(".register-*")))

    def test_git_checkout_and_source_overlap_are_rejected(self):
        self.vault.mkdir()
        (self.vault / ".git").write_text("gitdir: elsewhere")
        with self.assertRaises(ValueError):
            data.register(self.source, self.vault / "private")
        with self.assertRaises(ValueError):
            data.register(self.source, self.source / "vault")

    def test_empty_and_size_limited_inputs_leave_no_dataset(self):
        with patch.object(data, "MAX_FILE_BYTES", 1):
            with self.assertRaises(ValueError):
                data.register(self.source, self.vault)
        self.assertFalse(list(self.vault.iterdir()))
        (self.source / "d2data.mpq").unlink()
        with self.assertRaises(ValueError):
            data.register(self.source, self.vault)

    def test_extra_missing_and_modified_manifest_are_rejected(self):
        folder = data.register(self.source, self.vault)
        extra = folder / "mpq/extra.txt"
        extra.write_text("unexpected")
        with self.assertRaises(ValueError):
            data.verify(folder)
        extra.unlink()
        manifest = folder / "manifest.json"
        content = json.loads(manifest.read_text())
        content["identity"]["archives"][0]["name"] = "../outside.mpq"
        manifest.write_text(json.dumps(content))
        with self.assertRaises(ValueError):
            data.verify(folder)

    def test_links_are_rejected(self):
        linked = self.source / "linked.mpq"
        try:
            linked.symlink_to(self.source / "d2data.mpq")
        except OSError:
            self.skipTest("Symlinks unavailable for this account")
        with self.assertRaises(ValueError):
            data.register(self.source, self.vault)


if __name__ == "__main__":
    unittest.main()
