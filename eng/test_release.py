"""Release boundary tests using tiny fake exports; never presented as game QA."""
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
import zipfile

import release


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.output = self.root / "out"
        self.version = "v0.2.0-rc.1"
        self.commit = "a" * 40

    def fixture(self, preset):
        source = self.root / preset
        source.mkdir(exist_ok=True)
        if preset == "macOS":
            with zipfile.ZipFile(source / "OpenD2.zip", "w") as archive:
                archive.writestr("OpenD2.app/Contents/Info.plist", "fixture")
        else:
            executable = source / ("OpenD2.x86_64" if preset == "Linux" else "OpenD2.exe")
            executable.write_bytes(b"fixture executable")
            executable.chmod(0o755)
            (source / "OpenD2.pck").write_bytes(b"fixture resources")
            (source / "data/runtime").mkdir(parents=True, exist_ok=True)
            (source / "data/runtime/dependency.bin").write_bytes(b"fixture runtime")
        return source

    def packages(self):
        # Synthetic handoff fixtures, not real exports or game smoke evidence.
        self.output.mkdir()
        for preset in release.PLATFORMS:
            archive = self.output / release.package_name(self.version, preset)
            archive.write_bytes(b"synthetic handoff fixture")
            release.write_json(self.output / f"package-{preset}.json", {
                "schema_version": 1, "version": self.version, "commit": self.commit, "platform": preset,
                "file": archive.name, "bytes": archive.stat().st_size,
                "sha256": release.sha256(archive), "headless_smoke": "PASS",
            })

    def prepare(self):
        release.prepare(self.output, self.version, self.commit, "owner/repo", "https://example.test/run/1")

    def test_candidate_names_reject_paths_stable_versions_and_shell_text(self):
        for value in ("v1.2.3", "../v1.2.3-rc.1", "v1.2.3-rc.0", "v1.2.3-rc.1\n", "$(id)", "-bad", "ci"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                release.check_version(value)
        self.assertEqual("v0.2.0-rc.1", release.check_version(self.version))

    @unittest.skipIf(os.name == "nt", "POSIX permissions are verified on Linux/macOS")
    def test_linux_archive_preserves_runtime_and_executable_permission(self):
        metadata = release.pack(self.fixture("Linux"), self.output, "Linux", self.version, self.commit)
        with tarfile.open(self.output / metadata["file"]) as archive:
            self.assertTrue(archive.getmember("OpenD2.x86_64").mode & 0o111)
            self.assertEqual(b"fixture runtime", archive.extractfile("data/runtime/dependency.bin").read())
            self.assertIn("THIRD_PARTY_NOTICES.md", archive.getnames())

    def test_windows_archive_keeps_the_entire_export(self):
        metadata = release.pack(self.fixture("Windows"), self.output, "Windows", self.version, self.commit)
        with zipfile.ZipFile(self.output / metadata["file"]) as archive:
            self.assertEqual(b"fixture resources", archive.read("OpenD2.pck"))
            self.assertIn("data/runtime/dependency.bin", archive.namelist())

    def test_macos_zip_is_byte_identical(self):
        source = self.fixture("macOS")
        metadata = release.pack(source, self.output, "macOS", self.version, self.commit)
        self.assertEqual((source / "OpenD2.zip").read_bytes(), (self.output / metadata["file"]).read_bytes())

    def test_complete_handoff_is_pending_and_all_checksums_match(self):
        self.packages()
        self.prepare()
        manifest = json.loads((self.output / "release-manifest.json").read_text())
        self.assertEqual("NOT_RUN", manifest["gui_qa_status"])
        report = json.loads((self.output / "qa-result-template.json").read_text())
        self.assertEqual("NOT_RUN", report["status"])
        self.assertTrue(all(case["status"] == "NOT_RUN" for case in report["cases"]))
        for line in (self.output / "SHA256SUMS").read_text().splitlines():
            digest, name = line.split("  ", 1)
            self.assertEqual(digest, release.sha256(self.output / name))

    def test_missing_platform_rejects_handoff(self):
        self.packages()
        (self.output / "package-Windows.json").unlink()
        with self.assertRaises(FileNotFoundError):
            self.prepare()

    def test_changed_archive_rejects_handoff(self):
        self.packages()
        with (self.output / release.package_name(self.version, "Linux")).open("ab") as stream:
            stream.write(b"tampered")
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.prepare()

    def test_wrong_commit_path_or_missing_smoke_rejects_handoff(self):
        self.packages()
        path = self.output / "package-Linux.json"
        original = json.loads(path.read_text())
        for key, value in (("commit", "b" * 40), ("file", "../wrong.tar.gz"), ("headless_smoke", "NOT_RUN")):
            release.write_json(path, {**original, key: value})
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.prepare()

    def test_existing_archive_is_not_overwritten(self):
        source = self.fixture("Windows")
        release.pack(source, self.output, "Windows", self.version, self.commit)
        with self.assertRaises(FileExistsError):
            release.pack(source, self.output, "Windows", self.version, self.commit)

    def test_missing_pck_rejects_incomplete_export(self):
        source = self.fixture("Windows")
        (source / "OpenD2.pck").unlink()
        with self.assertRaises(ValueError):
            release.pack(source, self.output, "Windows", self.version, self.commit)


if __name__ == "__main__":
    unittest.main()
