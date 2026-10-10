"""QA handoff boundary tests using fake bytes; not actual MPQ/GUI acceptance."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import release

spec = importlib.util.spec_from_file_location("mpq_handoff", Path(__file__).parent / "qa/mpq_handoff.py")
qa = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qa)


class MpqHandoffTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.data = self.root / "data"
        self.data.mkdir()
        self.kit = self.root / "kit"
        self.kit.mkdir()
        for name in ("demo-town-scene.json", "MPQ_GROK_TASK.md"):
            (self.kit / name).write_bytes((qa.HERE / name).read_bytes())
        hashes = []
        for name in ("d2data.mpq", "patch_d2.mpq"):
            file = self.data / name
            file.write_bytes(b"fake MPQ for boundary test")
            hashes.append(dict(name=name, bytes=file.stat().st_size, sha256=qa.digest(file)))
        (self.kit / "demo-mpq-hashes.json").write_text(json.dumps(hashes))
        self.package = self.root / "OpenD2-ci-linux-x64.tar.gz"
        self.package.write_bytes(b"fake packaged game")
        self.metadata = self.root / "package-Linux.json"
        self.identity = dict(schema_version=1, platform="Linux", version="ci", commit="a" * 40,
                             file=self.package.name, bytes=self.package.stat().st_size,
                             sha256=qa.digest(self.package), headless_smoke="PASS")
        self.metadata.write_text(json.dumps(self.identity))
        (self.kit / "kit.json").write_text(json.dumps(self.identity))
        self.output = self.root / "qa output"
        context = patch.object(qa, "HERE", self.kit)
        context.start()
        self.addCleanup(context.stop)

    def prepare(self):
        return qa.prepare(self.data, self.package, self.metadata, self.output)

    def test_handoff_does_not_promote_hash_match_to_gui_or_decode_pass(self):
        before = {p.name: p.read_bytes() for p in self.data.iterdir()}
        result = self.prepare()
        self.assertEqual("PASS", result["mpq_integrity"])
        self.assertEqual("NOT_RUN", result["gui_qa"])
        self.assertEqual("NOT_RUN", result["full_asset_audit"])
        self.assertEqual("NOT_VERIFIED", result["full_lod_compatibility"])
        self.assertFalse(result["original_rules_validated"])
        self.assertTrue(all(c["status"] == "NOT_RUN" for c in result["cases"]))
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.data.iterdir()})
        self.assertFalse(list(self.output.rglob("*.mpq")))
        self.assertNotIn(str(self.root), json.dumps(result))

    def test_modified_missing_extra_mpqs_fail_before_creating_output(self):
        path = self.data / "d2data.mpq"
        original = path.read_bytes()
        path.write_bytes(b"x" * len(original))
        with self.assertRaisesRegex(ValueError, "mismatch"):
            self.prepare()
        path.unlink()
        with self.assertRaises(ValueError):
            self.prepare()
        path.write_bytes(original)
        (self.data / "extra.mpq").write_bytes(b"extra")
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertFalse(self.output.exists())

    def test_case_variants_are_supported(self):
        (self.data / "d2data.mpq").rename(self.data / "D2DATA.MPQ")
        self.assertEqual("PASS", self.prepare()["mpq_integrity"])

    def test_bad_package_or_wrong_kit_commit_is_rejected(self):
        for key, value in (("sha256", "b" * 64), ("bytes", 1), ("headless_smoke", "NOT_RUN"), ("commit", "b" * 40)):
            self.metadata.write_text(json.dumps({**self.identity, key: value}))
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.prepare()
            self.assertFalse(self.output.exists())

    def test_never_overwrites_existing_evidence_or_writes_into_mpqs(self):
        self.output.mkdir()
        evidence = self.output / "action-log.txt"
        evidence.write_text("existing observation")
        with self.assertRaises(FileExistsError):
            self.prepare()
        self.assertEqual("existing observation", evidence.read_text())
        self.output = self.data / "qa"
        with self.assertRaises(ValueError):
            self.prepare()

    def test_kit_is_reproducible_allowlisted_and_binds_build(self):
        first = release.mpq_kit(self.root, "ci", "a" * 40).read_bytes()
        second = release.mpq_kit(self.root, "ci", "a" * 40).read_bytes()
        self.assertEqual(first, second)
        with zipfile.ZipFile(self.root / "mpq-qa-kit.zip") as archive:
            self.assertEqual({"mpq_handoff.py", "demo-town-scene.json", "demo-mpq-hashes.json", "MPQ_GROK_TASK.md", "kit.json"}, set(archive.namelist()))
            self.assertEqual("a" * 40, json.loads(archive.read("kit.json"))["commit"])


if __name__ == "__main__":
    unittest.main()
