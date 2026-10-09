"""The validation gate must reject engine errors even when Godot exits successfully."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from godot_process import run_godot


class GodotProcessTests(unittest.TestCase):
    def run_script(self, script, **kwargs):
        return run_godot([sys.executable, "-c", script], cwd=self.folder,
                         log=self.log, **kwargs)

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.folder = Path(self.temporary.name)
        self.log = self.folder / "logs/run.log"

    def test_success_preserves_output(self):
        output = self.run_script("print('READY')")
        self.assertEqual(output, self.log.read_text(encoding="utf-8"))
        self.assertIn("READY", output)

    def test_editor_settings_error_with_zero_exit_is_fatal(self):
        with self.assertRaisesRegex(SystemExit, "Godot reported an error"):
            self.run_script('print(\'ERROR: EditorSettings not instantiated yet when getting setting "export/android/android_sdk_path".\')')
        self.assertIn("EditorSettings", self.log.read_text(encoding="utf-8"))

    def test_unrelated_engine_error_with_zero_exit_is_fatal(self):
        with self.assertRaises(SystemExit):
            self.run_script("print('ERROR: Failed to load resource')")

    def test_stderr_script_error_with_zero_exit_is_fatal(self):
        with self.assertRaises(SystemExit):
            self.run_script("import sys; print('SCRIPT ERROR: broken', file=sys.stderr)")
        self.assertIn("SCRIPT ERROR:", self.log.read_text(encoding="utf-8"))

    def test_nonzero_exit_without_error_text_is_fatal(self):
        with self.assertRaises(subprocess.CalledProcessError):
            self.run_script("import sys; print('incomplete'); sys.exit(7)")
        self.assertIn("incomplete", self.log.read_text(encoding="utf-8"))

    def test_timeout_is_fatal_and_preserves_partial_log(self):
        with self.assertRaisesRegex(SystemExit, "timed out"):
            self.run_script("import time; print('started', flush=True); time.sleep(30)", timeout=2)
        self.assertIn("started", self.log.read_text(encoding="utf-8"))
        self.assertIn("TIMEOUT", self.log.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
