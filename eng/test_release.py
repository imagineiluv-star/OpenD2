"""Release boundary tests using tiny fake exports; never presented as game QA."""
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest import mock
import zipfile

import release


_ENV_KEYS = ("D2_DATA_PATH", "OPEND2_ORIGINAL_SHA256SUMS", "OPEND2_RELEASE_REHEARSAL")
_saved_env = {}


def setUpModule():
    # Hermetic: a developer's D2_DATA_PATH / hash list must not change results.
    for key in _ENV_KEYS:
        if key in os.environ:
            _saved_env[key] = os.environ.pop(key)


def tearDownModule():
    os.environ.update(_saved_env)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        patcher = mock.patch.dict(os.environ, {"OPEND2_RELEASE_REHEARSAL": "1"})
        patcher.start()
        self.addCleanup(patcher.stop)
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

    def server_info(self, preset, **changes):
        runtime = release.SERVER_RUNTIMES[preset]
        archive = self.output / f"OpenD2-server-{runtime}.zip"
        archive.write_bytes(b"synthetic server fixture " + runtime.encode())
        info = {"commit": self.commit, "runtime": runtime, "file": archive.name, "bytes": archive.stat().st_size,
                "sha256": release.sha256(archive), "rendered_gameplay": "NOT_RUN",
                **{key: "PASS" for key in release.SERVER_REQUIRED_PASS},
                **{key: "NOT_RUN" for key in release.SERVER_MUST_BE_NOT_RUN}, **changes}
        release.write_json(self.output / f"server-{runtime}.json", info)
        return info

    def servers(self):
        # CI-built server packages as eng/pack-online-server.py records them (synthetic bytes, not a real server).
        for preset in release.SERVER_RUNTIMES:
            self.server_info(preset)

    def prepare(self):
        release.prepare(self.output, self.version, self.commit, "owner/repo", "https://example.test/run/1")

    def test_tags_accept_stable_and_candidates_and_must_match_version_file(self):
        for value in ("v0.1.0", "v0.1.0-rc.1", "v10.20.30-rc.12"):
            self.assertEqual(value, release.check_version(value))
        (self.root / "VERSION").write_text("0.1.0\n")
        self.assertEqual("v0.1.0", release.check_tag_matches_version("v0.1.0", self.root))
        self.assertEqual("v0.1.0-rc.2", release.check_tag_matches_version("v0.1.0-rc.2", self.root))
        for value in ("v0.2.0", "v0.1.1-rc.1", "v1.0.0"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                release.check_tag_matches_version(value, self.root)
        for bad in ("v0.1.0\n", "0.1", "0.1.0-rc.1", "01.0.0"):
            (self.root / "VERSION").write_text(bad if bad.endswith("\n") and bad.startswith("v") else bad)
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                release.read_version(self.root)

    def test_repository_version_is_single_source(self):
        import version
        self.assertEqual(release.read_version(), version.godot_version())
        props = (release.ROOT / "Directory.Build.props").read_text()
        self.assertIn("ReadAllText('$(MSBuildThisFileDirectory)VERSION')", props)

    def test_ci_checks_version_sync_and_stamps_assembly_metadata(self):
        release_yml = (release.ROOT / ".github/workflows/release.yml").read_text()
        self.assertIn("python eng/version.py check", release_yml)
        m0 = (release.ROOT / ".github/workflows/m0.yml").read_text()
        self.assertIn("SourceRevisionId=$GITHUB_SHA", m0)
        self.assertIn("VersionSuffix=${RELEASE_TAG#*-}", m0)

    def test_candidate_names_reject_paths_and_shell_text(self):
        for value in ("0.3", "v0.3", "v0.3.0-beta", "v01.2.3", "../v1.2.3-rc.1", "v1.2.3-rc.0", "v1.2.3-rc.1\n", "$(id)", "-bad", "ci"):
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
        self.servers()
        self.prepare()
        manifest = json.loads((self.output / "release-manifest.json").read_text())
        self.assertEqual(3, manifest["schema_version"])
        self.assertEqual("NOT_RUN", manifest["gui_qa_status"])
        self.assertEqual("synthetic_preview", manifest["gui_qa_scope"])
        self.assertEqual("NOT_RUN", manifest["legacy_scene_qa_status"])
        self.assertEqual("NOT_RUN", manifest["two_hour_soak_status"])
        self.assertFalse(manifest["original_rules_validated"])
        report = json.loads((self.output / "qa-result-template.json").read_text())
        self.assertEqual(3, report["schema_version"])
        self.assertEqual("RC5", report["qa_plan"])
        self.assertEqual(report["qa_plan"], manifest["qa_plan"])
        self.assertEqual(manifest["required_case_ids"], [case["id"] for case in report["cases"] if case["required"]])
        self.assertEqual((release.ROOT / "docs/migration/RC5_QA.md").read_bytes(),
                         (self.output / "FEATURE_QA.md").read_bytes())
        self.assertEqual("NOT_RUN", report["status"])
        self.assertEqual("synthetic_preview", report["status_scope"])
        self.assertEqual("NOT_RUN", report["legacy_scene_qa"]["status"])
        self.assertFalse(report["legacy_scene_qa"]["original_rules_validated"])
        self.assertEqual("NOT_RUN", report["two_hour_soak"]["status"])
        self.assertEqual(0, report["two_hour_soak"]["active_seconds"])
        self.assertEqual({"QA-01", "QA-02", "QA-03", "QA-04", "QA-05", "QA-06", "QA-08", "QA-11", "QA-12", "QA-13", "QA-14"},
                         {case["id"] for case in report["cases"] if case["required"]})
        self.assertEqual({"QA-07", "QA-09", "QA-10", "QA-15", "QA-16", "QA-17", "QA-18"},
                         {case["id"] for case in report["cases"] if not case["required"]})
        # Online acceptance is tracked on its own and is never part of the desktop GUI verdict.
        self.assertEqual("NOT_RUN", manifest["online_qa_status"])
        self.assertEqual(["QA-15", "QA-16", "QA-17", "QA-18"], manifest["online_case_ids"])
        self.assertTrue(set(manifest["online_case_ids"]).isdisjoint(manifest["required_case_ids"]))
        self.assertEqual("NOT_RUN", report["online_qa_status"])
        self.assertEqual("", report["server_package_sha256"])
        self.assertEqual({"linux-x64", "win-x64", "osx-arm64"}, {item["runtime"] for item in manifest["server_packages"]})
        for item in manifest["server_packages"]:
            archive = self.output / item["file"]
            self.assertEqual(release.server_name(self.version, item["platform"]), item["file"])
            self.assertEqual(item["sha256"], release.sha256(archive))
            self.assertEqual("PASS", item["validation"]["cooperative_world_realm"])
            self.assertEqual("NOT_RUN", item["validation"]["physical_multi_pc"])
            self.assertFalse((self.output / f"OpenD2-server-{item['runtime']}.zip").exists(), "only the versioned name is released")
            self.assertTrue((self.output / f"server-package-{item['platform']}.json").is_file())
            self.assertFalse((self.output / f"server-{item['runtime']}.json").exists())
        notes = (self.output / "RELEASE_NOTES.md").read_text(encoding="utf-8")
        self.assertIn("**Online QA: NOT_RUN.**", notes)
        self.assertIn("server-linux-x64.zip", notes.replace("<version>-server-<runtime>.zip", "server-linux-x64.zip"))
        self.assertEqual((release.ROOT / "docs/migration/PLAY_INTEGRATION.md").read_bytes(),
                         (self.output / "LEGACY_PLAY_SETUP.md").read_bytes())
        self.assertTrue(all(case["status"] == "NOT_RUN" for case in report["cases"]))
        for line in (self.output / "SHA256SUMS").read_text().splitlines():
            digest, name = line.split("  ", 1)
            self.assertEqual(digest, release.sha256(self.output / name))

    def test_missing_platform_rejects_handoff(self):
        self.packages()
        self.servers()
        (self.output / "package-Windows.json").unlink()
        with self.assertRaises(FileNotFoundError):
            self.prepare()

    def test_changed_archive_rejects_handoff(self):
        self.packages()
        self.servers()
        with (self.output / release.package_name(self.version, "Linux")).open("ab") as stream:
            stream.write(b"tampered")
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.prepare()

    def test_wrong_commit_path_or_missing_smoke_rejects_handoff(self):
        self.packages()
        self.servers()
        path = self.output / "package-Linux.json"
        original = json.loads(path.read_text())
        for key, value in (("commit", "b" * 40), ("file", "../wrong.tar.gz"), ("headless_smoke", "NOT_RUN")):
            release.write_json(path, {**original, key: value})
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.prepare()

    def test_missing_server_package_rejects_handoff(self):
        self.packages()
        self.servers()
        (self.output / "server-win-x64.json").unlink()
        with self.assertRaises(FileNotFoundError):
            self.prepare()

    def test_changed_or_misattributed_server_package_rejects_handoff(self):
        self.packages()
        self.servers()
        with (self.output / "OpenD2-server-linux-x64.zip").open("ab") as stream:
            stream.write(b"tampered")
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.prepare()
        for key, value in (("commit", "b" * 40), ("runtime", "win-x64"), ("file", "../wrong.zip")):
            self.setUp()
            self.packages()
            self.servers()
            self.server_info("Linux", **{key: value})
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "provenance"):
                self.prepare()

    def test_server_without_every_passing_validation_is_rejected(self):
        for key in release.SERVER_REQUIRED_PASS:
            self.setUp()
            self.packages()
            self.servers()
            self.server_info("macOS", **{key: "NOT_RUN"})
            with self.subTest(missing=key), self.assertRaisesRegex(ValueError, key):
                self.prepare()
        self.setUp()
        self.packages()
        self.servers()
        info = self.server_info("Windows")
        del info["cooperative_world_network_open"]
        release.write_json(self.output / "server-win-x64.json", info)
        with self.assertRaisesRegex(ValueError, "cooperative_world_network_open"):
            self.prepare()

    def test_server_claiming_unrun_validation_is_rejected(self):
        for key in release.SERVER_MUST_BE_NOT_RUN:
            self.setUp()
            self.packages()
            self.servers()
            self.server_info("Linux", **{key: "PASS"})
            with self.subTest(claimed=key), self.assertRaisesRegex(ValueError, key):
                self.prepare()

    def test_server_checks_match_what_the_server_packager_records(self):
        # A key that the packager never writes would make every release handoff fail; one it writes under a different
        # value would let an unrun check through. Keep both lists tied to eng/pack-online-server.py.
        source = (release.ROOT / "eng/pack-online-server.py").read_text(encoding="utf-8")
        for key in release.SERVER_REQUIRED_PASS:
            self.assertIn(f"'{key}': 'PASS'", source, key)
        for key in release.SERVER_MUST_BE_NOT_RUN:
            self.assertIn(f"'{key}': 'NOT_RUN'", source, key)

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
