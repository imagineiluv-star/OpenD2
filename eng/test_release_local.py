"""Unit tests for eng/release-local.py helpers (no build is run)."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("release_local", Path(__file__).with_name("release-local.py"))
release_local = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release_local)
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

    def test_build_directory_is_private_unique_and_removed(self):
        with release_local.BuildDirectory() as first, release_local.BuildDirectory() as second:
            self.assertNotEqual(first, second)
            self.assertEqual(0o700, first.stat().st_mode & 0o777)
            self.assertTrue(first.is_relative_to(release_local.CACHE_ROOT))
        self.assertFalse(first.exists() or second.exists())


if __name__ == "__main__":
    unittest.main()
