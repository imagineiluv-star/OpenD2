"""Unit tests for eng/release-local.py helpers (no build is run)."""
import importlib.util
from pathlib import Path
import os
import unittest

spec = importlib.util.spec_from_file_location("release_local", Path(__file__).with_name("release-local.py"))
release_local = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release_local)
for _key in ("D2_DATA_PATH", "OPEND2_ORIGINAL_SHA256SUMS", "OPEND2_RELEASE_REHEARSAL"):
    os.environ.pop(_key, None)
os.environ["D2_ASSET_CACHE"] = "none"  # hermetic; this module runs no subprocess that needs them
NPC = "FAIL NPC bundled native runtime executes and invalid model startup cleans up for retry: boom"


class ReleaseLocalTests(unittest.TestCase):
    def test_test_verdict_needs_summary_and_only_known_failures(self):
        ok = release_local.tests_acceptable
        self.assertTrue(ok(0, "3/3 passed\n")[0])
        self.assertTrue(ok(1, f"{NPC}\n464/465 passed\n")[0])
        self.assertFalse(ok(1, f"{NPC}\n")[0], "crash before summary")
        self.assertFalse(ok(1, f"{NPC}\nFAIL Other contract: x\n463/465 passed\n")[0])
        self.assertFalse(ok(1, f"{NPC}\n463/465 passed\n")[0], "a failure without a FAIL line")
        self.assertFalse(ok(1, "FAIL Something mentioning NPC bundled native runtime executes\n0/1 passed\n")[0])

    def test_compare_sums_names_every_difference(self):
        import tempfile
        with tempfile.TemporaryDirectory() as folder:
            a, b = Path(folder) / "a", Path(folder) / "b"
            a.write_text("11  x.tar.gz\n22  notes.md\n33  only-a\n")
            b.write_text("11  x.tar.gz\n99  notes.md\n44  only-b\n")
            self.assertEqual(["notes.md", "only-a", "only-b"], release_local.compare_sums(a, b))
            self.assertEqual([], release_local.compare_sums(a, a))

    def test_build_directory_is_private_unique_and_removed(self):
        with release_local.BuildDirectory() as first, release_local.BuildDirectory() as second:
            self.assertNotEqual(first, second)
            self.assertEqual(0o700, first.stat().st_mode & 0o777)
            self.assertTrue(first.is_relative_to(release_local.CACHE_ROOT))
        self.assertFalse(first.exists() or second.exists())


if __name__ == "__main__":
    unittest.main()
